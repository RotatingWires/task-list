using static Store;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

// TaskList options are read here; everything else (such as --urls) goes to ASP.NET Core. Same as NewsReview.
if (args is ["--version"])
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    return;
}
if (args is ["backup", .. var backupArgs])
{
    var defaultDatabase = File.Exists(Path.Combine("data", "tasklist.db")) || !File.Exists(Path.Combine("data", "task-list.db"))
        ? Path.Combine("data", "tasklist.db") : Path.Combine("data", "task-list.db");
    Environment.ExitCode = DatabaseFiles.BackupCommand("TaskList", defaultDatabase, backupArgs);
    return;
}
var hostArgs = new List<string>();
string? dataDirectoryArgument = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data-dir" && i + 1 < args.Length) dataDirectoryArgument = args[++i];
    else hostArgs.Add(args[i]);
}

var builder = WebApplication.CreateBuilder(hostArgs.ToArray());
// --data-dir, then the TaskList:DataDirectory setting, then TASKLIST_DATA_DIR, then data/ next to the app.
var dataDir = Path.GetFullPath(dataDirectoryArgument ?? builder.Configuration["TaskList:DataDirectory"]
    ?? Environment.GetEnvironmentVariable("TASKLIST_DATA_DIR") ?? Path.Combine(builder.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(dataDir);
// Loopback unless --urls, ASPNETCORE_URLS, or an appsettings "urls" value chooses another address.
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:8711");
var consoleLogPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "console.log");
builder.Logging.AddProvider(new SingleFileLoggerProvider(consoleLogPath, 10 * 1024 * 1024));
// Session-cookie keys must outlive restarts, or every restart signs you out. On Windows with a loaded user profile
// (signed in and started from a terminal), they stay in that profile's ASP.NET key ring. Otherwise (Linux, including
// services under systemd ProtectHome, and Windows Task Scheduler without a loaded profile) they live in data/keys;
// Windows then uses machine-scope DPAPI, since user-scope DPAPI fails when the profile isn't loaded. Same as NewsReview.
var keysDirectory = Path.Combine(dataDir, "keys");
var profileKeyRing = OperatingSystem.IsWindows()
    && Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } localAppData
    && Directory.Exists(localAppData);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("TaskList");
if (!profileKeyRing)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}
var keyLocation = profileKeyRing ? "this Windows profile's ASP.NET key ring" : keysDirectory;

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
            else context.Response.Redirect(LoginRedirect(context));
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

// Brotli or gzip for text responses (data, scripts, styles), so they cross the network several times smaller.
// HTTPS responses are left uncompressed, ASP.NET's default, which avoids compression side-channel attacks there.
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "application/manifest+json"]);
});

var app = builder.Build();
app.UseResponseCompression();
var databasePath = DatabaseFiles.Locate(dataDir, "tasklist.db", "task-list.db", app.Logger);
var connectionString = $"Data Source={databasePath};Foreign Keys=True";
var authPath = Path.Combine(dataDir, "auth.json");
var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
Onboarding.Initialize(dataDir, databasePath);
var journalMode = ConfigureDatabaseJournal(connectionString, databasePath);
try
{
    InitializeDatabase(connectionString);
}
catch (InvalidOperationException error)
{
    // A database this version cannot use: say why and stop, without touching the file. Same as SpendTracker.
    app.Logger.LogError("{Error}", error.Message);
    Environment.ExitCode = 1;
    return;
}
MilestoneNotifications.Initialize(connectionString);
app.Logger.LogInformation("TaskList v{Version}, data folder {DataDirectory}.", appVersion, dataDir);
app.Logger.LogInformation("Database ready: journal_mode={JournalMode}, synchronous=NORMAL, busy_timeout=5000 ms.", journalMode);
app.Logger.LogInformation("Sign-in keys: {Location}.", keyLocation);

string? setupToken = PasswordAuth.IsConfigured(authPath) ? null : PasswordAuth.GenerateSetupToken();
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

// Browser hardening: API replies are never stored, and pages and scripts are checked for changes on every load.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.CacheControl = context.Request.Path.StartsWithSegments("/api") ? "no-store" : "no-cache";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});
// Cross-origin writes are refused before rate limiting, so they never use up login attempts.
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var sameOrigin = origin.Length == 0
            || (Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                && string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" || !sameOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Cross-origin writes are not allowed." });
            return;
        }
    }
    await next();
});
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
    version = appVersion,
    configured = PasswordAuth.IsConfigured(authPath),
    authenticated = context.User.Identity?.IsAuthenticated == true
}));
app.MapPost("/api/auth/setup", async (SetupRequest request, HttpContext context) =>
{
    if (PasswordAuth.IsConfigured(authPath)) return Results.Conflict(new { error = "A password is already configured." });
    if (setupToken is null) return Results.Conflict(new { error = "No setup token is active. Restart TaskList to generate a new one." });
    if (!PasswordAuth.SetupTokenMatches(setupToken, request.SetupToken)) return Results.Json(new { error = "Invalid setup token." }, statusCode: StatusCodes.Status401Unauthorized);
    var passwordError = PasswordAuth.ValidateNewPassword(request.Password, request.ConfirmPassword);
    if (passwordError is not null) return Results.BadRequest(new { error = passwordError });
    PasswordAuth.Save(authPath, request.Password!);
    setupToken = null;
    await PasswordAuth.SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (!PasswordAuth.IsConfigured(authPath)) return Results.Conflict(new { error = "No password has been configured yet." });
    if (string.IsNullOrEmpty(request.Password) || !PasswordAuth.Verify(authPath, request.Password, out var needsRehash)) return Results.Unauthorized();
    if (needsRehash)
    {
        PasswordAuth.Save(authPath, request.Password);
        app.Logger.LogInformation("Upgraded the saved password hash to {Iterations} PBKDF2 iterations.", PasswordAuth.Iterations);
    }
    await PasswordAuth.SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
MilestoneNotifications.MapEndpoints(api, connectionString);
Transfer.MapEndpoints(api, connectionString, dataDir, app.Logger);
FreshStart.MapEndpoints(api, connectionString, dataDir, app.Logger);
api.MapGet("/onboarding", () => Results.Ok(new { importReminderPending = Onboarding.Pending(dataDir) }));
api.MapPost("/onboarding/acknowledge", () =>
{
    Onboarding.Acknowledge(dataDir);
    return Results.NoContent();
});

api.MapGet("/lists", () =>
{
    var lists = new List<TaskListInfo>();
    using var connection = OpenConnection(connectionString);
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at,
               (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
               l.archived
        FROM lists l WHERE l.archived = 0 ORDER BY l.id;
        """);
    using var reader = command.ExecuteReader();
    while (reader.Read()) lists.Add(ReadList(reader));
    return Results.Ok(lists);
});
api.MapGet("/lists/archives", () =>
{
    var lists = new List<TaskListInfo>();
    using var connection = OpenConnection(connectionString);
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at,
               (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
               l.archived
        FROM lists l WHERE l.archived = 1 ORDER BY l.id;
        """);
    using var reader = command.ExecuteReader();
    while (reader.Read()) lists.Add(ReadList(reader));
    return Results.Ok(lists);
});
api.MapGet("/stats", () =>
{
    using var connection = OpenConnection(connectionString);
    var highestUniversalId = ScalarLong(connection, "SELECT COALESCE(MAX(id), 0) FROM universal_ids;");
    return Results.Ok(new { highestUniversalId });
});

api.MapPost("/lists", (CreateListRequest request) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "List name is required." });
    var description = request.Description?.Trim() ?? string.Empty;
    if ((TooLong("List name", name, MaxListNameLength) ?? TooLong("List description", description, MaxListDescriptionLength)) is { } listError)
        return Results.BadRequest(new { error = listError });
    var now = DateTimeOffset.UtcNow.ToString("O");
    using var connection = OpenConnection(connectionString);
    if (ListNameExists(connection, name, null)) return Results.BadRequest(new { error = "A list with that name already exists." });
    var id = ScalarLong(connection, """
        INSERT INTO lists (name, description, next_task_number, created_at)
        VALUES ($name, $description, 1, $createdAt);
        SELECT last_insert_rowid();
        """, ("$name", name), ("$description", description), ("$createdAt", now));
    return Results.Created($"/api/lists/{id}", new TaskListInfo(id, name, description, now, 0, false));
});
api.MapPatch("/lists/{id:long}", (long id, UpdateListRequest request) =>
{
    using var connection = OpenConnection(connectionString);
    var existing = GetList(connection, id);
    if (existing is null) return Results.NotFound();
    var name = request.Name is null ? existing.Name : request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "List name cannot be empty." });
    var description = request.Description is null ? existing.Description : request.Description.Trim();
    // Only values being changed are checked, so older long text never blocks other edits.
    if ((TooLong("List name", request.Name is null ? "" : name, MaxListNameLength)
        ?? TooLong("List description", request.Description is null ? "" : description, MaxListDescriptionLength)) is { } listError)
        return Results.BadRequest(new { error = listError });
    if (ListNameExists(connection, name, id)) return Results.BadRequest(new { error = "A list with that name already exists." });
    Execute(connection, "UPDATE lists SET name = $name, description = $description WHERE id = $id;", ("$name", name), ("$description", description), ("$id", id));
    return Results.Ok(existing with { Name = name, Description = description });
});
api.MapPost("/lists/{id:long}/archive", (long id) =>
{
    using var connection = OpenConnection(connectionString);
    var existing = GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (existing.Archived) return Results.Ok(existing);
    if (ScalarLong(connection, "SELECT COUNT(*) FROM lists WHERE archived = 0;") <= 1) return Results.BadRequest(new { error = "You cannot archive the only active list." });
    Execute(connection, "UPDATE lists SET archived = 1 WHERE id = $id;", ("$id", id));
    return Results.Ok(existing with { Archived = true });
});
api.MapPost("/lists/{id:long}/restore", (long id) =>
{
    using var connection = OpenConnection(connectionString);
    var existing = GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (!existing.Archived) return Results.Ok(existing);
    Execute(connection, "UPDATE lists SET archived = 0 WHERE id = $id;", ("$id", id));
    return Results.Ok(existing with { Archived = false });
});
api.MapDelete("/lists/{id:long}", (long id) =>
{
    using var connection = OpenConnection(connectionString);
    var existing = GetList(connection, id);
    if (existing is null) return Results.NotFound();
    if (!existing.Archived && ScalarLong(connection, "SELECT COUNT(*) FROM lists WHERE archived = 0;") <= 1) return Results.BadRequest(new { error = "You cannot delete the only active list." });

    using var transaction = connection.BeginTransaction();
    var deletedAt = DateTimeOffset.UtcNow.ToString("O");
    ExecuteTx(connection, transaction, """
        INSERT INTO task_events
            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
        SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
        FROM items
        WHERE list_id = $id;

        DELETE FROM lists WHERE id = $id;
        """, ("$deletedAt", deletedAt), ("$id", id));
    transaction.Commit();
    return Results.NoContent();
});

api.MapGet("/lists/{listId:long}/tasks", (long listId) =>
{
    using var connection = OpenConnection(connectionString);
    if (GetList(connection, listId) is null) return Results.NotFound(new { error = "List not found." });
    var allItems = new List<TaskItem>();
    var byDisplayId = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
    using (var command = Sql(connection, """
        SELECT universal_id, list_id, display_id, parent_display_id, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM items WHERE list_id = $listId;
        """, ("$listId", listId)))
    using (var reader = command.ExecuteReader())
    {
        while (reader.Read())
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

api.MapPost("/lists/{listId:long}/tasks", (long listId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Task title is required." });
    var description = request.Description?.Trim() ?? string.Empty;
    if ((TooLong("Task title", title, MaxTaskTitleLength) ?? TooLong("Description", description, MaxTaskDescriptionLength)) is { } taskError)
        return Results.BadRequest(new { error = taskError });
    var now = DateTimeOffset.UtcNow.ToString("O");
    using var connection = OpenConnection(connectionString);
    using var transaction = connection.BeginTransaction();
    var taskNumberValue = ScalarTx(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (taskNumberValue is null) return Results.NotFound(new { error = "List not found." });
    var taskNumber = Convert.ToInt64(taskNumberValue);
    var displayId = taskNumber.ToString(CultureInfo.InvariantCulture);
    var universalId = AllocateUniversalId(connection, transaction);
    ExecuteTx(connection, transaction, """
        INSERT INTO items
            (universal_id, list_id, display_id, parent_display_id, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
        VALUES
            ($uid, $listId, $displayId, NULL, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL, 1);
        UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;
        """,
        ("$uid", universalId), ("$listId", listId), ("$displayId", displayId), ("$title", title), ("$description", description), ("$createdAt", now), ("$nextNumber", taskNumber + 1));
    transaction.Commit();
    return Results.Created($"/api/lists/{listId}/items/{displayId}", new TaskItem(universalId, listId, displayId, null, false, title, description, "Open", now, null, null, null, null, []));
});

api.MapPost("/lists/{listId:long}/items/{displayId}/subtasks", (long listId, string displayId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Subtask title is required." });
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    var description = request.Description?.Trim() ?? string.Empty;
    if ((TooLong("Subtask title", title, MaxTaskTitleLength) ?? TooLong("Description", description, MaxTaskDescriptionLength)) is { } subtaskError)
        return Results.BadRequest(new { error = subtaskError });
    var now = DateTimeOffset.UtcNow.ToString("O");
    using var connection = OpenConnection(connectionString);
    var parent = GetItem(connection, listId, displayId);
    if (parent is null) return Results.NotFound(new { error = "Parent task was not found." });
    using var transaction = connection.BeginTransaction();
    var childNumberValue = ScalarTx(connection, transaction, "SELECT next_child_number FROM items WHERE list_id = $listId AND display_id = $displayId;", ("$listId", listId), ("$displayId", displayId));
    if (childNumberValue is null) return Results.NotFound(new { error = "Parent task was not found." });
    var childNumber = Convert.ToInt64(childNumberValue);
    var childDisplayId = $"{displayId}.{childNumber.ToString(CultureInfo.InvariantCulture)}";
    var universalId = AllocateUniversalId(connection, transaction);
    ExecuteTx(connection, transaction, """
        INSERT INTO items
            (universal_id, list_id, display_id, parent_display_id, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
        VALUES
            ($uid, $listId, $childDisplayId, $parentDisplayId, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL, 1);
        UPDATE items SET next_child_number = $nextNumber WHERE list_id = $listId AND display_id = $parentDisplayId;
        """,
        ("$uid", universalId), ("$listId", listId), ("$childDisplayId", childDisplayId), ("$parentDisplayId", displayId), ("$title", title), ("$description", description), ("$createdAt", now), ("$nextNumber", childNumber + 1));
    transaction.Commit();
    return Results.Created($"/api/lists/{listId}/items/{childDisplayId}", new TaskItem(universalId, listId, childDisplayId, displayId, true, title, description, "Open", now, null, null, null, null, []));
});

api.MapPatch("/lists/{listId:long}/items/{displayId}", (long listId, string displayId, UpdateTaskRequest request) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    using var connection = OpenConnection(connectionString);
    var existing = GetItem(connection, listId, displayId);
    if (existing is null) return Results.NotFound();
    var values = BuildUpdatedValues(existing.Title, existing.Description, existing.Status, existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);
    if (values.Error is not null) return Results.BadRequest(new { error = values.Error });
    ApplyItemUpdate(connection, listId, displayId, values);
    return Results.Ok(existing with
    {
        Title = values.Title!, Description = values.Description!, Status = values.Status!,
        UpdatedAt = values.UpdatedAt, CompletedAt = values.CompletedAt, CancelledAt = values.CancelledAt, ReopenedAt = values.ReopenedAt
    });
});

api.MapPost("/lists/{listId:long}/items/{displayId}/move", (long listId, string displayId, MoveTaskRequest request) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    if (request.TargetListId == listId) return Results.BadRequest(new { error = "Choose a different destination list." });
    using var connection = OpenConnection(connectionString);
    using var transaction = connection.BeginTransaction();
    var movingRows = new List<MoveItemRow>();
    using (var command = SqlTx(connection, transaction, """
        SELECT universal_id, display_id, parent_display_id, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number
        FROM items
        WHERE list_id = $listId AND (display_id = $displayId OR display_id LIKE $descendantPattern)
        ORDER BY LENGTH(display_id), display_id;
        """, ("$listId", listId), ("$displayId", displayId), ("$descendantPattern", $"{displayId}.%")))
    using (var reader = command.ExecuteReader())
    {
        while (reader.Read()) movingRows.Add(new MoveItemRow(
            reader.GetInt64(0), reader.GetString(1), NullableString(reader, 2), reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), reader.GetInt64(11)));
    }
    if (movingRows.Count == 0) return Results.NotFound(new { error = "Task was not found." });
    var targetNextValue = ScalarTx(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $targetListId AND archived = 0;", ("$targetListId", request.TargetListId));
    if (targetNextValue is null) return Results.NotFound(new { error = "Destination list was not found." });
    var targetTaskNumber = Convert.ToInt64(targetNextValue);
    var targetRootDisplayId = targetTaskNumber.ToString(CultureInfo.InvariantCulture);
    ExecuteTx(connection, transaction, "DELETE FROM items WHERE list_id = $listId AND display_id = $displayId;", ("$listId", listId), ("$displayId", displayId));
    foreach (var row in movingRows)
    {
        var newDisplayId = RemapMovedDisplayId(displayId, targetRootDisplayId, row.DisplayId);
        var newParentDisplayId = row.DisplayId == displayId ? null : RemapMovedDisplayId(displayId, targetRootDisplayId, row.ParentDisplayId!);
        ExecuteTx(connection, transaction, """
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
    ExecuteTx(connection, transaction, "UPDATE lists SET next_task_number = $nextNumber WHERE id = $targetListId;", ("$nextNumber", targetTaskNumber + 1), ("$targetListId", request.TargetListId));
    transaction.Commit();
    return Results.Ok(new { targetListId = request.TargetListId, displayId = targetRootDisplayId, universalId = movingRows[0].UniversalId, movedCount = movingRows.Count });
});

api.MapDelete("/lists/{listId:long}/items/{displayId}", (long listId, string displayId) =>
{
    if (!IsValidDisplayId(displayId)) return Results.BadRequest(new { error = "Invalid task ID." });
    using var connection = OpenConnection(connectionString);
    using var transaction = connection.BeginTransaction();

    var exists = ScalarTx(connection, transaction,
        "SELECT EXISTS(SELECT 1 FROM items WHERE list_id = $listId AND display_id = $displayId);",
        ("$listId", listId), ("$displayId", displayId));
    if (Convert.ToInt64(exists) == 0) return Results.NotFound();

    var deletedAt = DateTimeOffset.UtcNow.ToString("O");
    ExecuteTx(connection, transaction, """
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
    transaction.Commit();
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
        if (TooLong("Task title", title, MaxTaskTitleLength) is { } importError) return Results.BadRequest(new { error = importError });
        var indent = IndentWidth(match.Groups["indent"].Value);
        var done = match.Groups["state"].Value != " ";
        var node = new ImportNode(new ImportItem(title, done ? "Done" : "Open", createdMatch.Success ? createdMatch.Groups["date"].Value : "Unknown", done ? completedMatch.Success ? completedMatch.Groups["date"].Value : "Unknown" : null, indent), []);
        while (stack.Count > 0 && indent <= stack.Peek().Item.Indent) stack.Pop();
        if (stack.Count == 0) roots.Add(node); else stack.Peek().Children.Add(node);
        stack.Push(node);
    }
    using var connection = OpenConnection(connectionString);
    using var transaction = connection.BeginTransaction();
    var nextTaskValue = ScalarTx(connection, transaction, "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (nextTaskValue is null) return Results.NotFound(new { error = "List not found." });
    var nextTaskNumber = Convert.ToInt64(nextTaskValue);
    var tasksImported = 0;
    var subtasksImported = 0;
    for (var rootIndex = roots.Count - 1; rootIndex >= 0; rootIndex--)
    {
        var root = roots[rootIndex];
        var displayId = nextTaskNumber.ToString(CultureInfo.InvariantCulture);
        var rootUid = AllocateUniversalId(connection, transaction);
        InsertImportedItem(connection, transaction, listId, displayId, null, rootUid, root);
        tasksImported++;
        subtasksImported += InsertImportedChildren(connection, transaction, listId, displayId, root.Children);
        nextTaskNumber++;
    }
    ExecuteTx(connection, transaction, "UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;", ("$listId", listId), ("$nextNumber", nextTaskNumber));
    transaction.Commit();
    return Results.Ok(new { imported = tasksImported + subtasksImported, tasksImported, subtasksImported });
});

// An unknown API address answers 404 like NewsReview, instead of the page or 405.
app.MapFallback("/api/{**path}", () => Results.Json(new { error = "Not found" }, statusCode: StatusCodes.Status404NotFound)).RequireAuthorization();
app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();

// Signing in from the home page goes to plain /login.html, like NewsReview; only a deep link keeps a ReturnUrl.
static string LoginRedirect(RedirectContext<CookieAuthenticationOptions> context) =>
    (context.Request.Path.Value is null or "/" or "/index.html") && !context.Request.QueryString.HasValue ? "/login.html" : context.RedirectUri;

record ImportItem(string Title, string Status, string CreatedAt, object? CompletedAt, int Indent);
record ImportNode(ImportItem Item, List<ImportNode> Children);
record TaskListInfo(long Id, string Name, string Description, string CreatedAt, long TaskCount, bool Archived);
record CreateListRequest(string? Name, string? Description);
record UpdateListRequest(string? Name, string? Description);
record FreshStartRequest(string? Confirmation, Dictionary<string, bool>? Selections);
record LoginRequest(string? Password);
record SetupRequest(string? SetupToken, string? Password, string? ConfirmPassword);
record CreateTaskRequest(string? Title, string? Description);
record UpdateTaskRequest(string? Title, string? Description, string? Status);
record MoveTaskRequest(long TargetListId);
record MoveItemRow(long UniversalId, string DisplayId, string? ParentDisplayId, string Title, string Description, string Status, string CreatedAt, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, long NextChildNumber);
record TaskItem(long UniversalId, long ListId, string DisplayId, string? ParentDisplayId, bool IsSubtask, string Title, string Description, string Status, string CreatedAt, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, List<TaskItem> Subtasks);
record UpdateValues(string? Title, string? Description, string? Status, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt, string? Error)
{
    public static UpdateValues WithError(string error) => new(null, null, null, null, null, null, null, error);
}

/// <summary>The schema number in PRAGMA user_version; startup and restores refuse anything newer.</summary>
