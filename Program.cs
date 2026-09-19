using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
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
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
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
var connectionString = $"Data Source={Path.Combine(dataDir, "task-list.db")};Foreign Keys=True";
var authPath = Path.Combine(dataDir, "auth.json");
InitializeDatabase(connectionString);

string? setupToken = PasswordConfigured(authPath) ? null : GenerateSetupToken();
if (setupToken is not null)
{
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine("TASK LIST FIRST-RUN SETUP TOKEN");
    Console.WriteLine();
    Console.WriteLine($"  {setupToken}");
    Console.WriteLine();
    Console.WriteLine("Enter this token on the Create Password screen.");
    Console.WriteLine("It changes each time Task List restarts until setup is complete.");
    Console.WriteLine("============================================================");
    Console.WriteLine();
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Keep the actual app shell behind the login screen. Static assets remain public,
// but every API route below is authenticated, so loading an asset alone exposes no task data.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var authenticated = context.User.Identity?.IsAuthenticated == true;

    if ((path == "/" || path == "/index.html") && !authenticated)
    {
        context.Response.Redirect("/login.html");
        return;
    }

    if (path == "/login.html" && authenticated)
    {
        context.Response.Redirect("/");
        return;
    }

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
    if (PasswordConfigured(authPath))
        return Results.Conflict(new { error = "A password has already been configured." });

    if (setupToken is null)
        return Results.Conflict(new { error = "No setup token is active. Restart Task List to generate a new one." });

    if (!SetupTokenMatches(setupToken, request.SetupToken))
        return Results.Json(new { error = "Invalid setup token." }, statusCode: StatusCodes.Status401Unauthorized);

    var passwordError = ValidateNewPassword(request.Password, request.ConfirmPassword);
    if (passwordError is not null)
        return Results.BadRequest(new { error = passwordError });

    SavePassword(authPath, request.Password!);
    setupToken = null;
    await SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    if (!PasswordConfigured(authPath))
        return Results.Conflict(new { error = "No password has been configured yet." });
    if (string.IsNullOrEmpty(request.Password) || !VerifyPassword(authPath, request.Password))
        return Results.Unauthorized();

    await SignInOwner(context);
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/lists", async () =>
{
    var lists = new List<TaskListInfo>();
    await using var connection = await OpenConnection(connectionString);
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at, COUNT(t.id)
        FROM lists l
        LEFT JOIN tasks t ON t.list_id = l.id
        GROUP BY l.id, l.name, l.description, l.created_at
        ORDER BY l.id;
        """);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        lists.Add(ReadList(reader));

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
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "List name is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);

    if (await ListNameExists(connection, name, null))
        return Results.BadRequest(new { error = "A list with that name already exists." });

    var id = await ScalarLongAsync(connection, """
        INSERT INTO lists (name, description, next_task_number, created_at)
        VALUES ($name, $description, 1, $createdAt);
        SELECT last_insert_rowid();
        """, ("$name", name), ("$description", description), ("$createdAt", now));

    return Results.Created($"/api/lists/{id}", new TaskListInfo(id, name, description, now, 0));
});

api.MapPatch("/lists/{id:long}", async (long id, UpdateListRequest request) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetList(connection, id);
    if (existing is null) return Results.NotFound();

    var name = request.Name is null ? existing.Name : request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "List name cannot be empty." });

    var description = request.Description is null ? existing.Description : request.Description.Trim();
    if (await ListNameExists(connection, name, id))
        return Results.BadRequest(new { error = "A list with that name already exists." });

    await ExecuteAsync(connection,
        "UPDATE lists SET name = $name, description = $description WHERE id = $id;",
        ("$name", name), ("$description", description), ("$id", id));

    return Results.Ok(existing with { Name = name, Description = description });
});

api.MapDelete("/lists/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lists;") <= 1)
        return Results.BadRequest(new { error = "You cannot delete the only remaining list." });
    if (await GetList(connection, id) is null) return Results.NotFound();

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    await ExecuteTxAsync(connection, transaction, """
        DELETE FROM subtasks WHERE parent_task_id IN (SELECT id FROM tasks WHERE list_id = $listId);
        DELETE FROM tasks WHERE list_id = $listId;
        DELETE FROM lists WHERE id = $listId;
        """, ("$listId", id));
    await transaction.CommitAsync();
    return Results.NoContent();
});

api.MapGet("/lists/{listId:long}/tasks", async (long listId) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await GetList(connection, listId) is null)
        return Results.NotFound(new { error = "List not found." });

    var tasks = new List<TaskItem>();
    var taskById = new Dictionary<long, TaskItem>();
    using (var command = Sql(connection, """
        SELECT id, universal_id, list_id, task_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM tasks WHERE list_id = $listId ORDER BY task_number DESC;
        """, ("$listId", listId)))
    await using (var reader = await command.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var task = ReadTask(reader);
            tasks.Add(task);
            taskById[task.Id] = task;
        }
    }

    if (taskById.Count > 0)
    {
        using var command = Sql(connection, """
            SELECT s.id, s.universal_id, s.parent_task_id, t.task_number, s.subtask_number,
                   s.title, s.description, s.status, s.created_at, s.updated_at,
                   s.completed_at, s.cancelled_at, s.reopened_at
            FROM subtasks s
            JOIN tasks t ON t.id = s.parent_task_id
            WHERE t.list_id = $listId
            ORDER BY t.task_number DESC, s.subtask_number;
            """, ("$listId", listId));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var subtask = ReadSubtask(reader);
            if (taskById.TryGetValue(subtask.ParentTaskId, out var parent))
                parent.Subtasks.Add(subtask);
        }
    }

    return Results.Ok(tasks);
});

api.MapPost("/lists/{listId:long}/tasks", async (long listId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Task title is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    var taskNumberValue = await ScalarTxAsync(connection, transaction,
        "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (taskNumberValue is null)
        return Results.NotFound(new { error = "List not found." });

    var taskNumber = Convert.ToInt32(taskNumberValue);
    var universalId = await AllocateUniversalId(connection, transaction);
    var id = await ScalarLongTxAsync(connection, transaction, """
        INSERT INTO tasks
            (universal_id, list_id, task_number, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_subtask_number)
        VALUES
            ($uid, $listId, $number, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL, 1);
        UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;
        SELECT last_insert_rowid();
        """,
        ("$uid", universalId), ("$listId", listId), ("$number", taskNumber),
        ("$title", title), ("$description", description), ("$createdAt", now),
        ("$nextNumber", taskNumber + 1));

    await transaction.CommitAsync();
    return Results.Created($"/api/tasks/{id}", new TaskItem(
        id, universalId, listId, taskNumber, taskNumber.ToString(), false,
        title, description, "Open", now, null, null, null, null, []));
});

api.MapPost("/tasks/{parentId:long}/subtasks", async (long parentId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Subtask title is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    var parent = await GetTask(connection, parentId);
    if (parent is null) return Results.NotFound(new { error = "Parent task was not found." });

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var subtaskNumber = Convert.ToInt32(await ScalarTxAsync(connection, transaction,
        "SELECT next_subtask_number FROM tasks WHERE id = $id;", ("$id", parentId)));
    var universalId = await AllocateUniversalId(connection, transaction);
    var id = await ScalarLongTxAsync(connection, transaction, """
        INSERT INTO subtasks
            (universal_id, parent_task_id, subtask_number, title, description, status,
             created_at, updated_at, completed_at, cancelled_at, reopened_at)
        VALUES
            ($uid, $parentId, $number, $title, $description, 'Open',
             $createdAt, NULL, NULL, NULL, NULL);
        UPDATE tasks SET next_subtask_number = $nextNumber WHERE id = $parentId;
        SELECT last_insert_rowid();
        """,
        ("$uid", universalId), ("$parentId", parentId), ("$number", subtaskNumber),
        ("$title", title), ("$description", description), ("$createdAt", now),
        ("$nextNumber", subtaskNumber + 1));

    await transaction.CommitAsync();
    return Results.Created($"/api/subtasks/{id}", new SubtaskItem(
        id, universalId, parentId, parent.TaskNumber, subtaskNumber,
        $"{parent.TaskNumber}.{subtaskNumber}", true,
        title, description, "Open", now, null, null, null, null));
});

api.MapPatch("/tasks/{id:long}", async (long id, UpdateTaskRequest request) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetTask(connection, id);
    if (existing is null) return Results.NotFound();

    var values = BuildUpdatedValues(existing.Title, existing.Description, existing.Status,
        existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);
    if (values.Error is not null) return Results.BadRequest(new { error = values.Error });

    await ApplyItemUpdate(connection, "tasks", id, values);
    return Results.Ok(existing with
    {
        Title = values.Title!, Description = values.Description!, Status = values.Status!,
        UpdatedAt = values.UpdatedAt, CompletedAt = values.CompletedAt,
        CancelledAt = values.CancelledAt, ReopenedAt = values.ReopenedAt
    });
});

api.MapPatch("/subtasks/{id:long}", async (long id, UpdateTaskRequest request) =>
{
    await using var connection = await OpenConnection(connectionString);
    var existing = await GetSubtask(connection, id);
    if (existing is null) return Results.NotFound();

    var values = BuildUpdatedValues(existing.Title, existing.Description, existing.Status,
        existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);
    if (values.Error is not null) return Results.BadRequest(new { error = values.Error });

    await ApplyItemUpdate(connection, "subtasks", id, values);
    return Results.Ok(existing with
    {
        Title = values.Title!, Description = values.Description!, Status = values.Status!,
        UpdatedAt = values.UpdatedAt, CompletedAt = values.CompletedAt,
        CancelledAt = values.CancelledAt, ReopenedAt = values.ReopenedAt
    });
});

api.MapDelete("/tasks/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await GetTask(connection, id) is null) return Results.NotFound();

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    await ExecuteTxAsync(connection, transaction, """
        DELETE FROM subtasks WHERE parent_task_id = $id;
        DELETE FROM tasks WHERE id = $id;
        """, ("$id", id));
    await transaction.CommitAsync();
    return Results.NoContent();
});

api.MapDelete("/subtasks/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    var changed = await ExecuteAsync(connection, "DELETE FROM subtasks WHERE id = $id;", ("$id", id));
    return changed == 0 ? Results.NotFound() : Results.NoContent();
});

api.MapPost("/lists/{listId:long}/import/markdown", async (long listId, HttpRequest request) =>
{
    using var body = new StreamReader(request.Body);
    var markdown = await body.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(markdown))
        return Results.BadRequest(new { error = "Markdown file is empty." });

    var checklist = new Regex(@"^(?<indent>[ \t]*)[-*+]\s+\[(?<state>[ xX])\]\s+(?<title>.+?)\s*$", RegexOptions.Compiled);
    var createdDate = new Regex(@"\s*➕\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    var completedDate = new Regex(@"\s*✅\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    // Parse in source order first so indentation can establish parent/subtask relationships.
    // Parent groups are imported bottom-up so older tasks receive lower task and Universal IDs.
    // Subtasks keep their source order within each parent.
    var groups = new List<ImportGroup>();
    ImportGroup? currentGroup = null;

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
        var item = new ImportItem(
            title,
            done ? "Done" : "Open",
            createdMatch.Success ? createdMatch.Groups["date"].Value : "Unknown",
            done ? completedMatch.Success ? completedMatch.Groups["date"].Value : "Unknown" : null,
            indent);

        if (currentGroup is null || indent <= currentGroup.Parent.Indent)
        {
            currentGroup = new ImportGroup(item, []);
            groups.Add(currentGroup);
        }
        else
        {
            currentGroup.Subtasks.Add(item);
        }
    }

    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var nextTaskValue = await ScalarTxAsync(connection, transaction,
        "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (nextTaskValue is null)
        return Results.NotFound(new { error = "List not found." });

    var nextTaskNumber = Convert.ToInt32(nextTaskValue);
    var tasksImported = 0;
    var subtasksImported = 0;

    for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
    {
        var group = groups[groupIndex];
        var parentUid = await AllocateUniversalId(connection, transaction);
        var parentId = await ScalarLongTxAsync(connection, transaction, """
            INSERT INTO tasks
                (universal_id, list_id, task_number, title, description, status,
                 created_at, updated_at, completed_at, cancelled_at, reopened_at, next_subtask_number)
            VALUES
                ($uid, $listId, $number, $title, '', $status,
                 $createdAt, 'Unknown', $completedAt, NULL, NULL, 1);
            SELECT last_insert_rowid();
            """,
            ("$uid", parentUid), ("$listId", listId), ("$number", nextTaskNumber),
            ("$title", group.Parent.Title), ("$status", group.Parent.Status),
            ("$createdAt", group.Parent.CreatedAt), ("$completedAt", group.Parent.CompletedAt));

        tasksImported++;
        var nextSubtaskNumber = 1;

        for (var subIndex = 0; subIndex < group.Subtasks.Count; subIndex++)
        {
            var subtask = group.Subtasks[subIndex];
            var subtaskUid = await AllocateUniversalId(connection, transaction);
            await ExecuteTxAsync(connection, transaction, """
                INSERT INTO subtasks
                    (universal_id, parent_task_id, subtask_number, title, description, status,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at)
                VALUES
                    ($uid, $parentId, $number, $title, '', $status,
                     $createdAt, 'Unknown', $completedAt, NULL, NULL);
                """,
                ("$uid", subtaskUid), ("$parentId", parentId), ("$number", nextSubtaskNumber),
                ("$title", subtask.Title), ("$status", subtask.Status),
                ("$createdAt", subtask.CreatedAt), ("$completedAt", subtask.CompletedAt));
            nextSubtaskNumber++;
            subtasksImported++;
        }

        await ExecuteTxAsync(connection, transaction,
            "UPDATE tasks SET next_subtask_number = $nextSubtaskNumber WHERE id = $id;",
            ("$nextSubtaskNumber", nextSubtaskNumber), ("$id", parentId));

        nextTaskNumber++;
    }

    await ExecuteTxAsync(connection, transaction,
        "UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;",
        ("$listId", listId), ("$nextNumber", nextTaskNumber));

    await transaction.CommitAsync();
    return Results.Ok(new { imported = tasksImported + subtasksImported, tasksImported, subtasksImported });
});

app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();

static void InitializeDatabase(string connectionString)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();
    using var command = Sql(connection, """
        CREATE TABLE IF NOT EXISTS lists (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            name             TEXT NOT NULL COLLATE NOCASE UNIQUE,
            description      TEXT NOT NULL DEFAULT '',
            next_task_number INTEGER NOT NULL DEFAULT 1,
            created_at       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS universal_ids (
            id INTEGER PRIMARY KEY AUTOINCREMENT
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            universal_id        INTEGER NOT NULL UNIQUE,
            list_id             INTEGER NOT NULL,
            task_number         INTEGER NOT NULL,
            title               TEXT NOT NULL,
            description         TEXT NOT NULL DEFAULT '',
            status              TEXT NOT NULL DEFAULT 'Open' CHECK(status IN ('Open', 'Done', 'Cancelled')),
            created_at          TEXT NOT NULL,
            updated_at          TEXT NULL,
            completed_at        TEXT NULL,
            cancelled_at        TEXT NULL,
            reopened_at         TEXT NULL,
            next_subtask_number INTEGER NOT NULL DEFAULT 1,
            UNIQUE(list_id, task_number),
            FOREIGN KEY(list_id) REFERENCES lists(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS subtasks (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            universal_id   INTEGER NOT NULL UNIQUE,
            parent_task_id INTEGER NOT NULL,
            subtask_number INTEGER NOT NULL,
            title          TEXT NOT NULL,
            description    TEXT NOT NULL DEFAULT '',
            status         TEXT NOT NULL DEFAULT 'Open' CHECK(status IN ('Open', 'Done', 'Cancelled')),
            created_at     TEXT NOT NULL,
            updated_at     TEXT NULL,
            completed_at   TEXT NULL,
            cancelled_at   TEXT NULL,
            reopened_at    TEXT NULL,
            UNIQUE(parent_task_id, subtask_number),
            FOREIGN KEY(parent_task_id) REFERENCES tasks(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_list ON tasks(list_id, task_number);
        CREATE INDEX IF NOT EXISTS idx_subtasks_parent ON subtasks(parent_task_id, subtask_number);
        """);
    command.ExecuteNonQuery();

    using var ensureDefault = Sql(connection, """
        INSERT INTO lists (name, description, next_task_number, created_at)
        SELECT 'Tasks', '', 1, $createdAt
        WHERE NOT EXISTS (SELECT 1 FROM lists);
        """, ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));
    ensureDefault.ExecuteNonQuery();
}

static async Task<SqliteConnection> OpenConnection(string connectionString)
{
    var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    return connection;
}

static SqliteCommand Sql(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters)
{
    var command = connection.CreateCommand();
    command.CommandText = text;
    AddParameters(command, parameters);
    return command;
}

static SqliteCommand SqlTx(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters)
{
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = text;
    AddParameters(command, parameters);
    return command;
}

static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
{
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

static async Task<object?> ScalarAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, parameters);
    return await command.ExecuteScalarAsync();
}

static async Task<object?> ScalarTxAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = SqlTx(connection, transaction, text, parameters);
    return await command.ExecuteScalarAsync();
}

static async Task<long> ScalarLongAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters) =>
    Convert.ToInt64(await ScalarAsync(connection, text, parameters));

static async Task<long> ScalarLongTxAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters) =>
    Convert.ToInt64(await ScalarTxAsync(connection, transaction, text, parameters));

static async Task<int> ExecuteAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, parameters);
    return await command.ExecuteNonQueryAsync();
}

static async Task<int> ExecuteTxAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = SqlTx(connection, transaction, text, parameters);
    return await command.ExecuteNonQueryAsync();
}

static async Task<long> AllocateUniversalId(SqliteConnection connection, SqliteTransaction transaction) =>
    await ScalarLongTxAsync(connection, transaction,
        "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();");

static async Task<TaskListInfo?> GetList(SqliteConnection connection, long id)
{
    using var command = Sql(connection, """
        SELECT l.id, l.name, l.description, l.created_at, COUNT(t.id)
        FROM lists l
        LEFT JOIN tasks t ON t.list_id = l.id
        WHERE l.id = $id
        GROUP BY l.id, l.name, l.description, l.created_at;
        """, ("$id", id));
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadList(reader) : null;
}

static TaskListInfo ReadList(SqliteDataReader reader) => new(
    reader.GetInt64(0), reader.GetString(1),
    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
    reader.GetString(3), reader.GetInt64(4));

static async Task<bool> ListNameExists(SqliteConnection connection, string name, long? excludingId)
{
    var sql = excludingId is null
        ? "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE);"
        : "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE AND id <> $id);";
    var result = excludingId is null
        ? await ScalarLongAsync(connection, sql, ("$name", name))
        : await ScalarLongAsync(connection, sql, ("$name", name), ("$id", excludingId.Value));
    return result != 0;
}

static async Task ApplyItemUpdate(SqliteConnection connection, string table, long id, UpdateValues values)
{
    if (table is not ("tasks" or "subtasks")) throw new ArgumentOutOfRangeException(nameof(table));
    await ExecuteAsync(connection, $"""
        UPDATE {table}
        SET title = $title, description = $description, status = $status,
            updated_at = $updatedAt, completed_at = $completedAt,
            cancelled_at = $cancelledAt, reopened_at = $reopenedAt
        WHERE id = $id;
        """,
        ("$title", values.Title), ("$description", values.Description), ("$status", values.Status),
        ("$updatedAt", values.UpdatedAt), ("$completedAt", values.CompletedAt),
        ("$cancelledAt", values.CancelledAt), ("$reopenedAt", values.ReopenedAt), ("$id", id));
}

static UpdateValues BuildUpdatedValues(
    string oldTitle, string oldDescription, string oldStatus, string? oldUpdatedAt,
    string? oldCompletedAt, string? oldCancelledAt, string? oldReopenedAt, UpdateTaskRequest request)
{
    var title = request.Title is null ? oldTitle : request.Title.Trim();
    if (string.IsNullOrWhiteSpace(title)) return UpdateValues.WithError("Task title cannot be empty.");

    var description = request.Description is null ? oldDescription : request.Description.Trim();
    var status = request.Status is null ? oldStatus : NormalizeStatus(request.Status);
    if (status is null) return UpdateValues.WithError("Status must be Open, Done, or Cancelled.");

    if (title == oldTitle && description == oldDescription && status == oldStatus)
        return new(title, description, status, oldUpdatedAt, oldCompletedAt, oldCancelledAt, oldReopenedAt, null);

    var now = DateTimeOffset.UtcNow.ToString("O");
    var completedAt = oldCompletedAt;
    var cancelledAt = oldCancelledAt;
    var reopenedAt = oldReopenedAt;
    if (status != oldStatus)
    {
        if (status == "Done") completedAt = now;
        else if (status == "Cancelled") cancelledAt = now;
        else reopenedAt = now;
    }

    return new(title, description, status, now, completedAt, cancelledAt, reopenedAt, null);
}

static string? NormalizeStatus(string status) => status.ToLowerInvariant() switch
{
    "open" => "Open",
    "done" => "Done",
    "cancelled" => "Cancelled",
    _ => null
};

static int IndentWidth(string indent) => indent.Sum(ch => ch == '\t' ? 4 : 1);

static TaskItem ReadTask(SqliteDataReader reader)
{
    var id = reader.GetInt64(0);
    var taskNumber = reader.GetInt32(3);
    return new TaskItem(
        id, reader.GetInt64(1), reader.GetInt64(2), taskNumber, taskNumber.ToString(), false,
        reader.GetString(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.GetString(6),
        reader.GetString(7), NullableString(reader, 8), NullableString(reader, 9),
        NullableString(reader, 10), NullableString(reader, 11), []);
}

static SubtaskItem ReadSubtask(SqliteDataReader reader)
{
    var parentNumber = reader.GetInt32(3);
    var subtaskNumber = reader.GetInt32(4);
    return new SubtaskItem(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), parentNumber, subtaskNumber,
        $"{parentNumber}.{subtaskNumber}", true, reader.GetString(5),
        reader.IsDBNull(6) ? string.Empty : reader.GetString(6), reader.GetString(7), reader.GetString(8),
        NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12));
}

static string? NullableString(SqliteDataReader reader, int index) =>
    reader.IsDBNull(index) ? null : reader.GetString(index);

static async Task<TaskItem?> GetTask(SqliteConnection connection, long id)
{
    using var command = Sql(connection, """
        SELECT id, universal_id, list_id, task_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM tasks WHERE id = $id;
        """, ("$id", id));
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadTask(reader) : null;
}

static async Task<SubtaskItem?> GetSubtask(SqliteConnection connection, long id)
{
    using var command = Sql(connection, """
        SELECT s.id, s.universal_id, s.parent_task_id, t.task_number, s.subtask_number,
               s.title, s.description, s.status, s.created_at, s.updated_at,
               s.completed_at, s.cancelled_at, s.reopened_at
        FROM subtasks s JOIN tasks t ON t.id = s.parent_task_id
        WHERE s.id = $id;
        """, ("$id", id));
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? ReadSubtask(reader) : null;
}

static string GenerateSetupToken()
{
    var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}";
}

static bool SetupTokenMatches(string expected, string? supplied)
{
    if (string.IsNullOrWhiteSpace(supplied)) return false;
    var expectedValue = expected.Replace("-", string.Empty, StringComparison.Ordinal);
    var suppliedValue = Regex.Replace(supplied, @"[\s-]", string.Empty);
    return string.Equals(expectedValue, suppliedValue, StringComparison.OrdinalIgnoreCase);
}

static bool PasswordConfigured(string authPath) => File.Exists(authPath);

static string? ValidateNewPassword(string? password, string? confirmation)
{
    if (string.IsNullOrEmpty(password) || password.Length < 8)
        return "Password must be at least 8 characters long.";
    if (password != confirmation)
        return "Passwords do not match.";
    return null;
}

static void SavePassword(string authPath, string password)
{
    const int iterations = 210_000;
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    var auth = new PasswordFile(1, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    File.WriteAllText(authPath, JsonSerializer.Serialize(auth));
}

static bool VerifyPassword(string authPath, string password)
{
    try
    {
        var auth = JsonSerializer.Deserialize<PasswordFile>(File.ReadAllText(authPath));
        if (auth is null || auth.Version != 1 || auth.Iterations < 1) return false;
        var salt = Convert.FromBase64String(auth.Salt);
        var expected = Convert.FromBase64String(auth.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, auth.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch
    {
        return false;
    }
}

static async Task SignInOwner(HttpContext context)
{
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        });
}

record ImportItem(string Title, string Status, string CreatedAt, object? CompletedAt, int Indent);
record ImportGroup(ImportItem Parent, List<ImportItem> Subtasks);
record TaskListInfo(long Id, string Name, string Description, string CreatedAt, long TaskCount);
record CreateListRequest(string? Name, string? Description);
record UpdateListRequest(string? Name, string? Description);
record LoginRequest(string? Password);
record SetupRequest(string? SetupToken, string? Password, string? ConfirmPassword);
record PasswordFile(int Version, int Iterations, string Salt, string Hash);
record CreateTaskRequest(string? Title, string? Description);
record UpdateTaskRequest(string? Title, string? Description, string? Status);

record TaskItem(
    long Id, long UniversalId, long ListId, int TaskNumber, string DisplayId, bool IsSubtask,
    string Title, string Description, string Status, string CreatedAt, string? UpdatedAt,
    string? CompletedAt, string? CancelledAt, string? ReopenedAt, List<SubtaskItem> Subtasks);

record SubtaskItem(
    long Id, long UniversalId, long ParentTaskId, int ParentTaskNumber, int SubtaskNumber,
    string DisplayId, bool IsSubtask, string Title, string Description, string Status,
    string CreatedAt, string? UpdatedAt, string? CompletedAt, string? CancelledAt, string? ReopenedAt);

record UpdateValues(
    string? Title, string? Description, string? Status, string? UpdatedAt,
    string? CompletedAt, string? CancelledAt, string? ReopenedAt, string? Error)
{
    public static UpdateValues WithError(string error) => new(null, null, null, null, null, null, null, error);
}
