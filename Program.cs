using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var consoleLogPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "console.log");
builder.Logging.AddProvider(new SingleFileLoggerProvider(consoleLogPath, 10 * 1024 * 1024));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "TaskList.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login.html";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var databasePath = Path.Combine(dataDir, "task-list.db");
var connectionString = $"Data Source={databasePath};Foreign Keys=True";
var authPath = Path.Combine(dataDir, "auth.json");
var journalMode = ConfigureDatabaseJournal(connectionString, databasePath);
InitializeDatabase(connectionString);
MilestoneNotifications.Initialize(connectionString);
app.Logger.LogInformation(
    "SQLite performance settings: journal_mode={JournalMode}, synchronous=NORMAL, busy_timeout=5000 ms.",
    journalMode);

string? setupToken = PasswordConfigured(authPath) ? null : GenerateSetupToken();
if (setupToken is not null)
{
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine("TASKLIST FIRST-RUN SETUP TOKEN");
    Console.WriteLine();
    Console.WriteLine($"  {setupToken}");
    Console.WriteLine();
    Console.WriteLine("Enter this token on the Create Password screen.");
    Console.WriteLine("It changes each time TaskList restarts until setup is complete.");
    Console.WriteLine("============================================================");
    Console.WriteLine();
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    if ((path == "/" || path == "/index.html") && !authenticated) { context.Response.Redirect("/login.html"); return; }
    if (path == "/login.html" && authenticated) { context.Response.Redirect("/"); return; }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/auth/status", (HttpContext context) => Results.Ok(new
{
    configured = PasswordConfigured(authPath),
    authenticated = context.User.Identity?.IsAuthenticated == true
}));
app.MapPost("/api/auth/setup", async (SetupRequest request, HttpContext context) =>
{
    if (PasswordConfigured(authPath)) return Results.Conflict(new { error = "A password has already been configured." });
    if (setupToken is null) return Results.Conflict(new { error = "No setup token is active. Restart TaskList to generate a new one." });
    if (!SetupTokenMatches(setupToken, request.SetupToken)) return Results.Json(new { error = "Invalid setup token." }, statusCode: StatusCodes.Status401Unauthorized);
    var passwordError = ValidateNewPassword(request.Password, request.ConfirmPassword);
    if (passwordError is not null) return Results.BadRequest(new { error = passwordError });
    SavePassword(authPath, request.Password!);
    setupToken = null;
    await SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (!PasswordConfigured(authPath)) return Results.Conflict(new { error = "No password has been configured yet." });
    if (string.IsNullOrEmpty(request.Password) || !VerifyPassword(authPath, request.Password)) return Results.Unauthorized();
    await SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
MilestoneNotifications.MapEndpoints(api, connectionString);

api.MapGet("/lists", async () =>
{
    var lists = new List<TaskListInfo>();
    await using var connection = await OpenConnection(connectionString);
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at,
               (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
               l.archived
        FROM lists l WHERE l.archived = 0 ORDER BY l.id;
        """);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) lists.Add(ReadList(reader));
    return Results.Ok(lists);
});
api.MapGet("/lists/archives", async () =>
{
    var lists = new List<TaskListInfo>();
    await using var connection = await OpenConnection(connectionString);
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at,
               (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
               l.archived
        FROM lists l WHERE l.archived = 1 ORDER BY l.id;
        """);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) lists.Add(ReadList(reader));
    return Results.Ok(lists);
});
api.MapGet("/stats", async () =>
{
    await using var connection = await OpenConnection(connectionString);
    var highestUniversalId = await ScalarLongAsync(connection, "SELECT COALESCE(MAX(id), 0) FROM universal_ids;");
    return Results.Ok(new { highestUniversalId });
});

api.MapPost("/lists", async (CreateListRequest request) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "List name is required." });
    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    if (await ListNameExists(connection, name, null)) return Results.BadRequest(new { error = "A list with that name already exists." });
    var id = await ScalarLongAsync(connection, """
        INSERT INTO lists (name, description, next_task_number, created_at)
        VALUES ($name, $description, 1, $createdAt);
        SELECT last_insert_rowid();
        """, ("$name", name), ("$description", description), ("$createdAt", now));
    return Results.Created($"/api/lists/{id}", new TaskListInfo(id, name, description, now, 0, false));
});
api.MapPatch("/lists/{id:long}", async (long id, UpdateListRequest request) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetList(connection, id);
    if (existing is null) return Results.NotFound();
    var name = request.Name is null ? existing.Name : request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "List name cannot be empty." });
    var description = request.Description is null ? existing.Description : request.Description.Trim();
    if (await ListNameExists(connection, name, id)) return Results.BadRequest(new { error = "A list with that name already exists." });
    await ExecuteAsync(connection, "UPDATE lists SET name = $name, description = $description WHERE id = $id;", ("$name", name), ("$description", description), ("$id", id));
    return Results.Ok(existing with { Name = name, Description = description });
});
api.MapPost("/lists/{id:long}/archive", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (existing.Archived) return Results.Ok(existing);
    if (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lists WHERE archived = 0;") <= 1) return Results.BadRequest(new { error = "You cannot archive the only active list." });
    await ExecuteAsync(connection, "UPDATE lists SET archived = 1 WHERE id = $id;", ("$id", id));
    return Results.Ok(existing with { Archived = true });
});
api.MapPost("/lists/{id:long}/restore", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (!existing.Archived) return Results.Ok(existing);
    await ExecuteAsync(connection, "UPDATE lists SET archived = 0 WHERE id = $id;", ("$id", id));
    return Results.Ok(existing with { Archived = false });
});
api.MapDelete("/lists/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (!existing.Archived && await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lists WHERE archived = 0;") <= 1) return Results.BadRequest(new { error = "You cannot delete the only active list." });

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var deletedAt = DateTimeOffset.UtcNow.ToString("O");
    await ExecuteTxAsync(connection, transaction, """
        INSERT INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
        FROM items
        WHERE list_id = $id;

        DELETE FROM lists WHERE id = $id;
        """, ("$deletedAt", deletedAt), ("$id", id));
    await transaction.CommitAsync();
    return Results.NoContent();
});

api.MapGet("/lists/{listId:long}/tasks", async (long listId) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await GetList(connection, listId) is null) return Results.NotFound(new { error = "List not found." });
    var allItems = new List<TaskItem>();
    var byDisplayId = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
    using (var command = Sql(connection, """
        SELECT universal_id, list_id, display_id, parent_display_id, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM items WHERE list_id = $listId;
        """, ("$listId", listId)))
    await using (var reader = await command.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var item = ReadItem(reader);
            allItems.Add(item);
            byDisplayId[item.DisplayId] = item;
        }
    }
    var roots = new List<TaskItem>();
    foreach (var item in allItems)
    {
        if (item.ParentDisplayId is null) roots.Add(item);
        else if (byDisplayId.TryGetValue(item.ParentDisplayId, out var parent)) parent.Subtasks.Add(item);
    }
    roots.Sort((a, b) => DisplaySegmentNumber(b.DisplayId).CompareTo(DisplaySegmentNumber(a.DisplayId)));
    foreach (var root in roots) SortChildren(root);
    return Results.Ok(roots);
});

api.MapPost("/lists/{listId:long}/tasks", async (long listId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Task title is required." });
    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var taskNumberValue = await ScalarTxAsync(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (taskNumberValue is null) return Results.NotFound(new { error = "List not found." });
    var taskNumber = Convert.ToInt64(taskNumberValue);
    var displayId = taskNumber.ToString(CultureInfo.InvariantCulture);
    var universalId = await AllocateUniversalId(connection, transaction);
    await ExecuteTxAsync(connection, transaction, """
        INSERT INTO items
            (universal_id, list_id, display_id, parent_display_id, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
        VALUES
            ($uid, $listId, $displayId, NULL, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL, 1);
        UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;
        """,
        ("$uid", universalId), ("$listId", listId), ("$displayId", displayId), ("$title", title), ("$description", description), ("$createdAt", now), ("$nextNumber", taskNumber + 1));
    await transaction.CommitAsync();
    return Results.Created($"/api/lists/{listId}/items/{displayId}", new TaskItem(universalId, listId, displayId, null, false, title, description, "Open", now, null, null, null, null, []));
});

api.MapPost("/lists/{listId:long}/items/{displayId}/subtasks", async (long listId, string displayId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Subtask title is required." });
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    var parent = await GetItem(connection, listId, displayId);
    if (parent is null) return Results.NotFound(new { error = "Parent task was not found." });
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var childNumberValue = await ScalarTxAsync(connection, transaction, "SELECT next_child_number FROM items WHERE list_id = $listId AND display_id = $displayId;", ("$listId", listId), ("$displayId", displayId));
    if (childNumberValue is null) return Results.NotFound(new { error = "Parent task was not found." });
    var childNumber = Convert.ToInt64(childNumberValue);
    var childDisplayId = $"{displayId}.{childNumber.ToString(CultureInfo.InvariantCulture)}";
    var universalId = await AllocateUniversalId(connection, transaction);
    await ExecuteTxAsync(connection, transaction, """
        INSERT INTO items
            (universal_id, list_id, display_id, parent_display_id, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
        VALUES
            ($uid, $listId, $childDisplayId, $parentDisplayId, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL, 1);
        UPDATE items SET next_child_number = $nextNumber WHERE list_id = $listId AND display_id = $parentDisplayId;
        """,
        ("$uid", universalId), ("$listId", listId), ("$childDisplayId", childDisplayId), ("$parentDisplayId", displayId), ("$title", title), ("$description", description), ("$createdAt", now), ("$nextNumber", childNumber + 1));
    await transaction.CommitAsync();
    return Results.Created($"/api/lists/{listId}/items/{childDisplayId}", new TaskItem(universalId, listId, childDisplayId, displayId, true, title, description, "Open", now, null, null, null, null, []));
});

api.MapPatch("/lists/{listId:long}/items/{displayId}", async (long listId, string displayId, UpdateTaskRequest request) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetItem(connection, listId, displayId);
    if (existing is null) return Results.NotFound();
    var values = BuildUpdatedValues(existing.Title, existing.Description, existing.Status, existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);
    if (values.Error is not null) return Results.BadRequest(new { error = values.Error });
    await ApplyItemUpdate(connection, listId, displayId, values);
    return Results.Ok(existing with
    {
        Title = values.Title!, Description = values.Description!, Status = values.Status!,
        UpdatedAt = values.UpdatedAt, CompletedAt = values.CompletedAt, CancelledAt = values.CancelledAt, ReopenedAt = values.ReopenedAt
    });
});

api.MapPost("/lists/{listId:long}/items/{displayId}/move", async (long listId, string displayId, MoveTaskRequest request) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    if (request.TargetListId == listId) return Results.BadRequest(new { error = "Choose a different destination list." });
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var movingRows = new List<MoveItemRow>();
    using (var command = SqlTx(connection, transaction, """
        SELECT universal_id, display_id, parent_display_id, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number
        FROM items
        WHERE list_id = $listId AND (display_id = $displayId OR display_id LIKE $descendantPattern)
        ORDER BY LENGTH(display_id), display_id;
        """, ("$listId", listId), ("$displayId", displayId), ("$descendantPattern", $"{displayId}.%")))
    await using (var reader = await command.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) movingRows.Add(new MoveItemRow(
            reader.GetInt64(0), reader.GetString(1), NullableString(reader, 2), reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), reader.GetInt64(11)));
    }
    if (movingRows.Count == 0) return Results.NotFound(new { error = "Task was not found." });
    var targetNextValue = await ScalarTxAsync(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $targetListId AND archived = 0;", ("$targetListId", request.TargetListId));
    if (targetNextValue is null) return Results.NotFound(new { error = "Destination list was not found." });
    var targetTaskNumber = Convert.ToInt64(targetNextValue);
    var targetRootDisplayId = targetTaskNumber.ToString(CultureInfo.InvariantCulture);
    await ExecuteTxAsync(connection, transaction, "DELETE FROM items WHERE list_id = $listId AND display_id = $displayId;", ("$listId", listId), ("$displayId", displayId));
    foreach (var row in movingRows)
    {
        var newDisplayId = RemapMovedDisplayId(displayId, targetRootDisplayId, row.DisplayId);
        var newParentDisplayId = row.DisplayId == displayId ? null : RemapMovedDisplayId(displayId, targetRootDisplayId, row.ParentDisplayId!);
        await ExecuteTxAsync(connection, transaction, """
            INSERT INTO items
                (universal_id, list_id, display_id, parent_display_id, title, description, status,
                 created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
            VALUES
                ($uid, $targetListId, $displayId, $parentDisplayId, $title, $description, $status,
                 $createdAt, $updatedAt, $completedAt, $cancelledAt, $reopenedAt, $nextChildNumber);
            """,
            ("$uid", row.UniversalId), ("$targetListId", request.TargetListId), ("$displayId", newDisplayId), ("$parentDisplayId", newParentDisplayId),
            ("$title", row.Title), ("$description", row.Description), ("$status", row.Status), ("$createdAt", row.CreatedAt), ("$updatedAt", row.UpdatedAt), ("$completedAt", row.CompletedAt), ("$cancelledAt", row.CancelledAt), ("$reopenedAt", row.ReopenedAt), ("$nextChildNumber", row.NextChildNumber));
    }
    await ExecuteTxAsync(connection, transaction, "UPDATE lists SET next_task_number = $nextNumber WHERE id = $targetListId;", ("$nextNumber", targetTaskNumber + 1), ("$targetListId", request.TargetListId));
    await transaction.CommitAsync();
    return Results.Ok(new { targetListId = request.TargetListId, displayId = targetRootDisplayId, universalId = movingRows[0].UniversalId, movedCount = movingRows.Count });
});

api.MapDelete("/lists/{listId:long}/items/{displayId}", async (long listId, string displayId) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    var exists = await ScalarTxAsync(connection, transaction,
        "SELECT EXISTS(SELECT 1 FROM items WHERE list_id = $listId AND display_id = $displayId);",
        ("$listId", listId), ("$displayId", displayId));
    if (Convert.ToInt64(exists) == 0) return Results.NotFound();

    var deletedAt = DateTimeOffset.UtcNow.ToString("O");
    await ExecuteTxAsync(connection, transaction, """
        INSERT INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
        FROM items
        WHERE list_id = $listId
          AND (display_id = $displayId OR display_id LIKE $descendantPattern);

        DELETE FROM items
        WHERE list_id = $listId AND display_id = $displayId;
        """,
        ("$deletedAt", deletedAt),
        ("$listId", listId),
        ("$displayId", displayId),
        ("$descendantPattern", $"{displayId}.%"));
    await transaction.CommitAsync();
    return Results.NoContent();
});

api.MapPost("/lists/{listId:long}/import/markdown", async (long listId, HttpRequest request) =>
{
    using var body = new StreamReader(request.Body);
    var markdown = await body.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(markdown)) return Results.BadRequest(new { error = "Markdown file is empty." });
    var checklist = new Regex(@"^(?<indent>[ \t]*)[-*+]\s+\[(?<state>[ xX])\]\s+(?<title>.+?)\s*$", RegexOptions.Compiled);
    var createdDate = new Regex(@"\s*➕\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    var completedDate = new Regex(@"\s*✅\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    var roots = new List<ImportNode>();
    var stack = new Stack<ImportNode>();
    foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
    {
        var match = checklist.Match(line);
        if (!match.Success) continue;
        var rawTitle = match.Groups["title"].Value.Trim();
        var createdMatch = createdDate.Match(rawTitle);
        var completedMatch = completedDate.Match(rawTitle);
        var title = completedDate.Replace(createdDate.Replace(rawTitle, string.Empty), string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) continue;
        var indent = IndentWidth(match.Groups["indent"].Value);
        var done = match.Groups["state"].Value != " ";
        var node = new ImportNode(new ImportItem(title, done ? "Done" : "Open", createdMatch.Success ? createdMatch.Groups["date"].Value : "Unknown", done ? completedMatch.Success ? completedMatch.Groups["date"].Value : "Unknown" : null, indent), []);
        while (stack.Count > 0 && indent <= stack.Peek().Item.Indent) stack.Pop();
        if (stack.Count == 0) roots.Add(node); else stack.Peek().Children.Add(node);
        stack.Push(node);
    }
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var nextTaskValue = await ScalarTxAsync(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (nextTaskValue is null) return Results.NotFound(new { error = "List not found." });
    var nextTaskNumber = Convert.ToInt64(nextTaskValue);
    var tasksImported = 0;
    var subtasksImported = 0;
    for (var rootIndex = roots.Count - 1; rootIndex >= 0; rootIndex--)
    {
        var root = roots[rootIndex];
        var displayId = nextTaskNumber.ToString(CultureInfo.InvariantCulture);
        var rootUid = await AllocateUniversalId(connection, transaction);
        await InsertImportedItem(connection, transaction, listId, displayId, null, rootUid, root);
        tasksImported++;
        subtasksImported += await InsertImportedChildren(connection, transaction, listId, displayId, root.Children);
        nextTaskNumber++;
    }
    await ExecuteTxAsync(connection, transaction, "UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;", ("$listId", listId), ("$nextNumber", nextTaskNumber));
    await transaction.CommitAsync();
    return Results.Ok(new { imported = tasksImported + subtasksImported, tasksImported, subtasksImported });
});

app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();

static void InitializeDatabase(string connectionString)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    ApplyConnectionPragmas(connection);
    using (var command = Sql(connection, """
        CREATE TABLE IF NOT EXISTS lists (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            description TEXT NOT NULL DEFAULT '',
            next_task_number INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS universal_ids (id INTEGER PRIMARY KEY AUTOINCREMENT);
        CREATE TABLE IF NOT EXISTS items (
            universal_id INTEGER NOT NULL UNIQUE,
            list_id INTEGER NOT NULL,
            display_id TEXT NOT NULL,
            parent_display_id TEXT NULL,
            title TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT 'Open' CHECK(status IN ('Open', 'Done', 'Cancelled')),
            created_at TEXT NOT NULL,
            updated_at TEXT NULL,
            completed_at TEXT NULL,
            cancelled_at TEXT NULL,
            reopened_at TEXT NULL,
            next_child_number INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (list_id, display_id),
            FOREIGN KEY (list_id) REFERENCES lists(id) ON DELETE CASCADE,
            FOREIGN KEY (list_id, parent_display_id) REFERENCES items(list_id, display_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_items_list_parent ON items(list_id, parent_display_id, display_id);

        CREATE TABLE IF NOT EXISTS task_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            universal_id INTEGER NOT NULL,
            event_type TEXT NOT NULL CHECK(event_type IN ('Created', 'Completed', 'Cancelled', 'Reopened', 'Deleted')),
            event_at TEXT NOT NULL,
            from_status TEXT NULL,
            to_status TEXT NULL,
            list_id INTEGER NOT NULL,
            display_id TEXT NOT NULL,
            parent_display_id TEXT NULL,
            title TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'live' CHECK(source IN ('live', 'legacy'))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_task_events_unique ON task_events(universal_id, event_type, event_at);
        CREATE INDEX IF NOT EXISTS idx_task_events_time ON task_events(event_at, id);
        CREATE INDEX IF NOT EXISTS idx_task_events_uid ON task_events(universal_id, event_at, id);
        CREATE INDEX IF NOT EXISTS idx_task_events_list ON task_events(list_id, event_at, id);

        CREATE TRIGGER IF NOT EXISTS trg_items_event_insert
        AFTER INSERT ON items
        BEGIN
            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            VALUES
                (NEW.universal_id, 'Created', NEW.created_at, NULL, 'Open', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                 CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END);

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT NEW.universal_id, 'Completed', NEW.completed_at, 'Open', 'Done', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                   CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
            WHERE NEW.status = 'Done' AND NEW.completed_at IS NOT NULL;

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT NEW.universal_id, 'Cancelled', NEW.cancelled_at, 'Open', 'Cancelled', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                   CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
            WHERE NEW.status = 'Cancelled' AND NEW.cancelled_at IS NOT NULL;
        END;

        CREATE TRIGGER IF NOT EXISTS trg_items_event_status
        AFTER UPDATE OF status ON items
        WHEN OLD.status <> NEW.status
        BEGIN
            INSERT INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            VALUES
                (NEW.universal_id,
                 CASE NEW.status WHEN 'Done' THEN 'Completed' WHEN 'Cancelled' THEN 'Cancelled' ELSE 'Reopened' END,
                 CASE NEW.status WHEN 'Done' THEN COALESCE(NEW.completed_at, NEW.updated_at)
                                 WHEN 'Cancelled' THEN COALESCE(NEW.cancelled_at, NEW.updated_at)
                                 ELSE COALESCE(NEW.reopened_at, NEW.updated_at) END,
                 OLD.status, NEW.status, NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title, 'live');
        END;
        """)) command.ExecuteNonQuery();

    EnsureTaskEventSchema(connection);

    var hasArchivedColumn = false;
    using (var columnCommand = Sql(connection, "PRAGMA table_info(lists);"))
    using (var reader = columnCommand.ExecuteReader())
    {
        while (reader.Read()) if (string.Equals(reader.GetString(1), "archived", StringComparison.OrdinalIgnoreCase)) { hasArchivedColumn = true; break; }
    }
    if (!hasArchivedColumn)
    {
        using var addArchived = Sql(connection, "ALTER TABLE lists ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;");
        addArchived.ExecuteNonQuery();
    }

    using (var backfill = Sql(connection, """
        INSERT OR IGNORE INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Created', created_at, NULL, 'Open', list_id, display_id, parent_display_id, title, 'legacy'
        FROM items;

        INSERT OR IGNORE INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Completed', completed_at, NULL, 'Done', list_id, display_id, parent_display_id, title, 'legacy'
        FROM items WHERE completed_at IS NOT NULL;

        INSERT OR IGNORE INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Cancelled', cancelled_at, NULL, 'Cancelled', list_id, display_id, parent_display_id, title, 'legacy'
        FROM items WHERE cancelled_at IS NOT NULL;

        INSERT OR IGNORE INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Reopened', reopened_at, NULL, 'Open', list_id, display_id, parent_display_id, title, 'legacy'
        FROM items WHERE reopened_at IS NOT NULL;
        """)) backfill.ExecuteNonQuery();

    using var ensureDefault = Sql(connection, """
        INSERT INTO lists (name, description, next_task_number, created_at)
        SELECT 'Tasks', '', 1, $createdAt WHERE NOT EXISTS (SELECT 1 FROM lists);
        """, ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));
    ensureDefault.ExecuteNonQuery();
}

static void EnsureTaskEventSchema(SqliteConnection connection)
{
    string tableSql;
    using (var command = Sql(connection, "SELECT COALESCE(sql, '') FROM sqlite_master WHERE type = 'table' AND name = 'task_events';"))
        tableSql = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;

    if (!tableSql.Contains("'Deleted'", StringComparison.Ordinal))
    {
        using var migrate = Sql(connection, """
            DROP TRIGGER IF EXISTS trg_items_event_insert;
            DROP TRIGGER IF EXISTS trg_items_event_status;
            DROP TRIGGER IF EXISTS trg_task_events_milestone_notification;

            ALTER TABLE task_events RENAME TO task_events_before_deleted;

            CREATE TABLE task_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                universal_id INTEGER NOT NULL,
                event_type TEXT NOT NULL CHECK(event_type IN ('Created', 'Completed', 'Cancelled', 'Reopened', 'Deleted')),
                event_at TEXT NOT NULL,
                from_status TEXT NULL,
                to_status TEXT NULL,
                list_id INTEGER NOT NULL,
                display_id TEXT NOT NULL,
                parent_display_id TEXT NULL,
                title TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'live' CHECK(source IN ('live', 'legacy'))
            );

            INSERT INTO task_events
                (id, universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT id, universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source
            FROM task_events_before_deleted
            ORDER BY id;

            DROP TABLE task_events_before_deleted;

            CREATE UNIQUE INDEX idx_task_events_unique ON task_events(universal_id, event_type, event_at);
            CREATE INDEX idx_task_events_time ON task_events(event_at, id);
            CREATE INDEX idx_task_events_uid ON task_events(universal_id, event_at, id);
            CREATE INDEX idx_task_events_list ON task_events(list_id, event_at, id);
            """);
        migrate.ExecuteNonQuery();
    }

    using var triggers = Sql(connection, """
        DROP TRIGGER IF EXISTS trg_items_event_insert;
        DROP TRIGGER IF EXISTS trg_items_event_status;

        CREATE TRIGGER trg_items_event_insert
        AFTER INSERT ON items
        BEGIN
            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            VALUES
                (NEW.universal_id, 'Created', NEW.created_at, NULL, 'Open', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                 CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END);

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT NEW.universal_id, 'Completed', NEW.completed_at, 'Open', 'Done', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                   CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
            WHERE NEW.status = 'Done' AND NEW.completed_at IS NOT NULL;

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT NEW.universal_id, 'Cancelled', NEW.cancelled_at, 'Open', 'Cancelled', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                   CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
            WHERE NEW.status = 'Cancelled' AND NEW.cancelled_at IS NOT NULL;
        END;

        CREATE TRIGGER trg_items_event_status
        AFTER UPDATE OF status ON items
        WHEN OLD.status <> NEW.status
        BEGIN
            INSERT INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            VALUES
                (NEW.universal_id,
                 CASE NEW.status WHEN 'Done' THEN 'Completed' WHEN 'Cancelled' THEN 'Cancelled' ELSE 'Reopened' END,
                 CASE NEW.status WHEN 'Done' THEN COALESCE(NEW.completed_at, NEW.updated_at)
                                 WHEN 'Cancelled' THEN COALESCE(NEW.cancelled_at, NEW.updated_at)
                                 ELSE COALESCE(NEW.reopened_at, NEW.updated_at) END,
                 OLD.status, NEW.status, NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title, 'live');
        END;
        """);
    triggers.ExecuteNonQuery();
}

static async Task<SqliteConnection> OpenConnection(string connectionString)
{
    var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await ApplyConnectionPragmasAsync(connection);
    return connection;
}

static string ConfigureDatabaseJournal(string connectionString, string databasePath)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    ApplyConnectionPragmas(connection);

    if (!CanUseWal(databasePath))
    {
        using var current = Sql(connection, "PRAGMA journal_mode;");
        return $"{current.ExecuteScalar()?.ToString() ?? "unknown"} (WAL skipped: network/unknown drive)";
    }

    using var wal = Sql(connection, "PRAGMA journal_mode=WAL;");
    return wal.ExecuteScalar()?.ToString() ?? "unknown";
}

static bool CanUseWal(string databasePath)
{
    try
    {
        var fullPath = Path.GetFullPath(databasePath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            return false;

        if (!OperatingSystem.IsWindows()) return true;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) return false;
        var drive = new DriveInfo(root);
        return drive.DriveType is not DriveType.Network and not DriveType.Unknown;
    }
    catch
    {
        return false;
    }
}

static SqliteCommand ConnectionPragmaCommand(SqliteConnection connection) =>
    Sql(connection, "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");

static void ApplyConnectionPragmas(SqliteConnection connection)
{
    using var command = ConnectionPragmaCommand(connection);
    command.ExecuteNonQuery();
}

static async Task ApplyConnectionPragmasAsync(SqliteConnection connection)
{
    using var command = ConnectionPragmaCommand(connection);
    await command.ExecuteNonQueryAsync();
}
static SqliteCommand Sql(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.CommandText = text; AddParameters(command, parameters); return command; }
static SqliteCommand SqlTx(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = text; AddParameters(command, parameters); return command; }
static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters) { foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
static async Task<object?> ScalarAsync(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { using var command = Sql(connection, text, parameters); return await command.ExecuteScalarAsync(); }
static async Task<object?> ScalarTxAsync(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { using var command = SqlTx(connection, transaction, text, parameters); return await command.ExecuteScalarAsync(); }
static async Task<long> ScalarLongAsync(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) => Convert.ToInt64(await ScalarAsync(connection, text, parameters));
static async Task<int> ExecuteAsync(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { using var command = Sql(connection, text, parameters); return await command.ExecuteNonQueryAsync(); }
static async Task<int> ExecuteTxAsync(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { using var command = SqlTx(connection, transaction, text, parameters); return await command.ExecuteNonQueryAsync(); }
static async Task<long> AllocateUniversalId(SqliteConnection connection, SqliteTransaction transaction) => Convert.ToInt64(await ScalarTxAsync(connection, transaction, "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();"));

static async Task<TaskListInfo?> GetList(SqliteConnection connection, long id)
{
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at,
               (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
               l.archived FROM lists l WHERE l.id = $id;
        """, ("$id", id));
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadList(reader) : null;
}
static TaskListInfo ReadList(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5) != 0);
static async Task<bool> ListNameExists(SqliteConnection connection, string name, long? excludingId)
{
    var sql = excludingId is null ? "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE);" : "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE AND id <> $id);";
    var result = excludingId is null ? await ScalarLongAsync(connection, sql, ("$name", name)) : await ScalarLongAsync(connection, sql, ("$name", name), ("$id", excludingId.Value));
    return result != 0;
}

static async Task ApplyItemUpdate(SqliteConnection connection, long listId, string displayId, UpdateValues values)
{
    await ExecuteAsync(connection, """
        UPDATE items SET title = $title, description = $description, status = $status,
            updated_at = $updatedAt, completed_at = $completedAt,
            cancelled_at = $cancelledAt, reopened_at = $reopenedAt
        WHERE list_id = $listId AND display_id = $displayId;
        """,
        ("$title", values.Title), ("$description", values.Description), ("$status", values.Status), ("$updatedAt", values.UpdatedAt), ("$completedAt", values.CompletedAt), ("$cancelledAt", values.CancelledAt), ("$reopenedAt", values.ReopenedAt), ("$listId", listId), ("$displayId", displayId));
}
static UpdateValues BuildUpdatedValues(string oldTitle, string oldDescription, string oldStatus, string? oldUpdatedAt, string? oldCompletedAt, string? oldCancelledAt, string? oldReopenedAt, UpdateTaskRequest request)
{
    var title = request.Title is null ? oldTitle : request.Title.Trim();
    if (string.IsNullOrWhiteSpace(title)) return UpdateValues.WithError("Task title cannot be empty.");
    var description = request.Description is null ? oldDescription : request.Description.Trim();
    var status = request.Status is null ? oldStatus : NormalizeStatus(request.Status);
    if (status is null) return UpdateValues.WithError("Status must be Open, Done, or Cancelled.");
    if (title == oldTitle && description == oldDescription && status == oldStatus) return new(title, description, status, oldUpdatedAt, oldCompletedAt, oldCancelledAt, oldReopenedAt, null);
    var now = DateTimeOffset.UtcNow.ToString("O");
    var completedAt = oldCompletedAt; var cancelledAt = oldCancelledAt; var reopenedAt = oldReopenedAt;
    if (status != oldStatus)
    {
        if (status == "Done") completedAt = now;
        else if (status == "Cancelled") cancelledAt = now;
        else reopenedAt = now;
    }
    return new(title, description, status, now, completedAt, cancelledAt, reopenedAt, null);
}
static string? NormalizeStatus(string status) => status.ToLowerInvariant() switch { "open" => "Open", "done" => "Done", "cancelled" => "Cancelled", _ => null };
static int IndentWidth(string indent) => indent.Sum(ch => ch == '\t' ? 4 : 1);
static TaskItem ReadItem(SqliteDataReader reader)
{
    var parentDisplayId = NullableString(reader, 3);
    return new TaskItem(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), parentDisplayId, parentDisplayId is not null, reader.GetString(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.GetString(6), reader.GetString(7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), []);
}
static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
static async Task<TaskItem?> GetItem(SqliteConnection connection, long listId, string displayId)
{
    using var command = Sql(connection, """
        SELECT universal_id, list_id, display_id, parent_display_id, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM items WHERE list_id = $listId AND display_id = $displayId;
        """, ("$listId", listId), ("$displayId", displayId));
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadItem(reader) : null;
}
static string RemapMovedDisplayId(string oldRootDisplayId, string newRootDisplayId, string oldDisplayId) => oldDisplayId == oldRootDisplayId ? newRootDisplayId : $"{newRootDisplayId}.{oldDisplayId[(oldRootDisplayId.Length + 1)..]}";
static long DisplaySegmentNumber(string displayId) { var separator = displayId.LastIndexOf('.'); var segment = separator < 0 ? displayId : displayId[(separator + 1)..]; return long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0; }
static bool IsValidDisplayId(string displayId) => Regex.IsMatch(displayId, @"^\d+(?:\.\d+)*$", RegexOptions.CultureInvariant);
static void SortChildren(TaskItem item) { item.Subtasks.Sort((a, b) => DisplaySegmentNumber(a.DisplayId).CompareTo(DisplaySegmentNumber(b.DisplayId))); foreach (var child in item.Subtasks) SortChildren(child); }

static async Task InsertImportedItem(SqliteConnection connection, SqliteTransaction transaction, long listId, string displayId, string? parentDisplayId, long universalId, ImportNode node)
{
    await ExecuteTxAsync(connection, transaction, """
        INSERT INTO items
            (universal_id, list_id, display_id, parent_display_id, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
        VALUES
            ($uid, $listId, $displayId, $parentDisplayId, $title, '', $status,
             $createdAt, 'Unknown', $completedAt, NULL, NULL, $nextChildNumber);
        """,
        ("$uid", universalId), ("$listId", listId), ("$displayId", displayId), ("$parentDisplayId", parentDisplayId), ("$title", node.Item.Title), ("$status", node.Item.Status), ("$createdAt", node.Item.CreatedAt), ("$completedAt", node.Item.CompletedAt), ("$nextChildNumber", node.Children.Count + 1));
}
static async Task<int> InsertImportedChildren(SqliteConnection connection, SqliteTransaction transaction, long listId, string parentDisplayId, List<ImportNode> children)
{
    var inserted = 0;
    for (var index = 0; index < children.Count; index++)
    {
        var child = children[index];
        var childNumber = index + 1;
        var displayId = $"{parentDisplayId}.{childNumber.ToString(CultureInfo.InvariantCulture)}";
        var universalId = await AllocateUniversalId(connection, transaction);
        await InsertImportedItem(connection, transaction, listId, displayId, parentDisplayId, universalId, child);
        inserted++;
        inserted += await InsertImportedChildren(connection, transaction, listId, displayId, child.Children);
    }
    return inserted;
}

static string GenerateSetupToken() { var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)); return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}"; }
static bool SetupTokenMatches(string expected, string? supplied) { if (string.IsNullOrWhiteSpace(supplied)) return false; var expectedValue = expected.Replace("-", string.Empty, StringComparison.Ordinal); var suppliedValue = Regex.Replace(supplied, @"[\s-]", string.Empty); return string.Equals(expectedValue, suppliedValue, StringComparison.OrdinalIgnoreCase); }
static bool PasswordConfigured(string authPath) => File.Exists(authPath);
static string? ValidateNewPassword(string? password, string? confirmation) { if (string.IsNullOrEmpty(password) || password.Length < 8) return "Password must be at least 8 characters long."; if (password != confirmation) return "Passwords do not match."; return null; }
static void SavePassword(string authPath, string password)
{
    const int iterations = 210_000;
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    File.WriteAllText(authPath, JsonSerializer.Serialize(new PasswordFile(1, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash))));
}
static bool VerifyPassword(string authPath, string password)
{
    try
    {
        var auth = JsonSerializer.Deserialize<PasswordFile>(File.ReadAllText(authPath));
        if (auth is null || auth.Version != 1 || auth.Iterations < 1) return false;
        var salt = Convert.FromBase64String(auth.Salt); var expected = Convert.FromBase64String(auth.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, auth.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch { return false; }
}
static async Task SignInOwner(HttpContext context)
{
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
}

record ImportItem(string Title, string Status, string CreatedAt, object? CompletedAt, int Indent);
record ImportNode(ImportItem Item, List<ImportNode> Children);
record TaskListInfo(long Id, string Name, string Description, string CreatedAt, long TaskCount, bool Archived);
record CreateListRequest(string? Name, string? Description);
record UpdateListRequest(string? Name, string? Description);
record LoginRequest(string? Password);
record SetupRequest(string? SetupToken, string? Password, string? ConfirmPassword);
record PasswordFile(int Version, int Iterations, string Salt, string Hash);
record CreateTaskRequest(string? Title, string? Description);
record UpdateTaskRequest(string? Title, string? Description, string? Status);
record MoveTaskRequest(long TargetListId);
record MoveItemRow(long UniversalId, string DisplayId, string? ParentDisplayId, string Title, string Description, string Status, string CreatedAt, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, long NextChildNumber);
record TaskItem(long UniversalId, long ListId, string DisplayId, string? ParentDisplayId, bool IsSubtask, string Title, string Description, string Status, string CreatedAt, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, List<TaskItem> Subtasks);
record UpdateValues(string? Title, string? Description, string? Status, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, string? Error)
{
    public static UpdateValues WithError(string error) => new(null, null, null, null, null, null, null, error);
}
