using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var connectionString = $"Data Source={Path.Combine(dataDir, "task-list.db")};Foreign Keys=True";
InitializeDatabase(connectionString);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/lists", async () =>
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

app.MapGet("/api/stats", async () =>
{
    await using var connection = await OpenConnection(connectionString);
    var highestUniversalId = await ScalarLongAsync(connection, "SELECT COALESCE(MAX(id), 0) FROM universal_ids;");
    return Results.Ok(new { highestUniversalId });
});

app.MapPost("/api/lists", async (CreateListRequest request) =>
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

app.MapPatch("/api/lists/{id:long}", async (long id, UpdateListRequest request) =>
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

app.MapDelete("/api/lists/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM lists;") <= 1)
        return Results.BadRequest(new { error = "You cannot delete the only remaining list." });
    if (await GetList(connection, id) is null) return Results.NotFound();

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction, """
        DELETE FROM subtasks WHERE parent_task_id IN (SELECT id FROM tasks WHERE list_id = $listId);
        DELETE FROM tasks WHERE list_id = $listId;
        DELETE FROM lists WHERE id = $listId;
        """, ("$listId", id));
    await transaction.CommitAsync();
    return Results.NoContent();
});

app.MapGet("/api/lists/{listId:long}/tasks", async (long listId) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await GetList(connection, listId) is null)
        return Results.NotFound(new { error = "List not found." });

    var tasks = new List<TaskItem>();
    var taskById = new Dictionary<long, TaskItem>();
    using (var command = Sql(connection, """
        SELECT id, universal_id, list_id, task_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM tasks WHERE list_id = $listId ORDER BY task_number;
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
            ORDER BY t.task_number, s.subtask_number;
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

app.MapPost("/api/lists/{listId:long}/tasks", async (long listId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Task title is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");
    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    var taskNumberValue = await ScalarAsync(connection, transaction,
        "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (taskNumberValue is null)
        return Results.NotFound(new { error = "List not found." });

    var taskNumber = Convert.ToInt32(taskNumberValue);
    var universalId = await AllocateUniversalId(connection, transaction);
    var id = await ScalarLongAsync(connection, transaction, """
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

app.MapPost("/api/tasks/{parentId:long}/subtasks", async (long parentId, CreateTaskRequest request) =>
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
    var subtaskNumber = Convert.ToInt32(await ScalarAsync(connection, transaction,
        "SELECT next_subtask_number FROM tasks WHERE id = $id;", ("$id", parentId)));
    var universalId = await AllocateUniversalId(connection, transaction);
    var id = await ScalarLongAsync(connection, transaction, """
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

app.MapPatch("/api/tasks/{id:long}", async (long id, UpdateTaskRequest request) =>
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

app.MapPatch("/api/subtasks/{id:long}", async (long id, UpdateTaskRequest request) =>
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

app.MapDelete("/api/tasks/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    if (await GetTask(connection, id) is null) return Results.NotFound();

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, transaction, """
        DELETE FROM subtasks WHERE parent_task_id = $id;
        DELETE FROM tasks WHERE id = $id;
        """, ("$id", id));
    await transaction.CommitAsync();
    return Results.NoContent();
});

app.MapDelete("/api/subtasks/{id:long}", async (long id) =>
{
    await using var connection = await OpenConnection(connectionString);
    var changed = await ExecuteAsync(connection, "DELETE FROM subtasks WHERE id = $id;", ("$id", id));
    return changed == 0 ? Results.NotFound() : Results.NoContent();
});

app.MapPost("/api/lists/{listId:long}/import/markdown", async (long listId, HttpRequest request) =>
{
    using var body = new StreamReader(request.Body);
    var markdown = await body.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(markdown))
        return Results.BadRequest(new { error = "Markdown file is empty." });

    var checklist = new Regex(@"^(?<indent>[ \t]*)[-*+]\s+\[(?<state>[ xX])\]\s+(?<title>.+?)\s*$", RegexOptions.Compiled);
    var createdDate = new Regex(@"\s*➕\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    var completedDate = new Regex(@"\s*✅\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    await using var connection = await OpenConnection(connectionString);
    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
    var nextTaskValue = await ScalarAsync(connection, transaction,
        "SELECT next_task_number FROM lists WHERE id = $listId;", ("$listId", listId));
    if (nextTaskValue is null)
        return Results.NotFound(new { error = "List not found." });

    var nextTaskNumber = Convert.ToInt32(nextTaskValue);
    var tasksImported = 0;
    var subtasksImported = 0;
    long? parentId = null;
    var parentIndent = 0;
    var nextSubtaskNumber = 0;

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
        var createdAt = createdMatch.Success ? createdMatch.Groups["date"].Value : "Unknown";
        object? completedAt = done
            ? completedMatch.Success ? completedMatch.Groups["date"].Value : "Unknown"
            : null;
        var uid = await AllocateUniversalId(connection, transaction);

        if (parentId is null || indent <= parentIndent)
        {
            parentId = await ScalarLongAsync(connection, transaction, """
                INSERT INTO tasks
                    (universal_id, list_id, task_number, title, description, status,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at, next_subtask_number)
                VALUES
                    ($uid, $listId, $number, $title, '', $status,
                     $createdAt, 'Unknown', $completedAt, NULL, NULL, 1);
                SELECT last_insert_rowid();
                """,
                ("$uid", uid), ("$listId", listId), ("$number", nextTaskNumber),
                ("$title", title), ("$status", done ? "Done" : "Open"),
                ("$createdAt", createdAt), ("$completedAt", completedAt));
            parentIndent = indent;
            nextSubtaskNumber = 0;
            nextTaskNumber++;
            tasksImported++;
        }
        else
        {
            nextSubtaskNumber++;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO subtasks
                    (universal_id, parent_task_id, subtask_number, title, description, status,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at)
                VALUES
                    ($uid, $parentId, $number, $title, '', $status,
                     $createdAt, 'Unknown', $completedAt, NULL, NULL);
                """,
                ("$uid", uid), ("$parentId", parentId.Value), ("$number", nextSubtaskNumber),
                ("$title", title), ("$status", done ? "Done" : "Open"),
                ("$createdAt", createdAt), ("$completedAt", completedAt));
            subtasksImported++;
        }
    }

    await ExecuteAsync(connection, transaction, """
        UPDATE tasks
        SET next_subtask_number = MAX(next_subtask_number, COALESCE((
            SELECT MAX(subtask_number) + 1 FROM subtasks WHERE parent_task_id = tasks.id
        ), 1))
        WHERE list_id = $listId;
        UPDATE lists SET next_task_number = $nextNumber WHERE id = $listId;
        """, ("$listId", listId), ("$nextNumber", nextTaskNumber));

    await transaction.CommitAsync();
    return Results.Ok(new { imported = tasksImported + subtasksImported, tasksImported, subtasksImported });
});

app.MapFallbackToFile("index.html");
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

static SqliteCommand Sql(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) =>
    Sql(connection, text, null, parameters);

static SqliteCommand Sql(SqliteConnection connection, string text, SqliteTransaction? transaction,
    params (string Name, object? Value)[] parameters)
{
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = text;
    foreach (var (name, value) in parameters)
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    return command;
}

static async Task<object?> ScalarAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, parameters);
    return await command.ExecuteScalarAsync();
}

static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, transaction, parameters);
    return await command.ExecuteScalarAsync();
}

static async Task<long> ScalarLongAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters) =>
    Convert.ToInt64(await ScalarAsync(connection, text, parameters));

static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters) =>
    Convert.ToInt64(await ScalarAsync(connection, transaction, text, parameters));

static async Task<int> ExecuteAsync(SqliteConnection connection, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, parameters);
    return await command.ExecuteNonQueryAsync();
}

static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string text,
    params (string Name, object? Value)[] parameters)
{
    using var command = Sql(connection, text, transaction, parameters);
    return await command.ExecuteNonQueryAsync();
}

static async Task<long> AllocateUniversalId(SqliteConnection connection, SqliteTransaction transaction) =>
    await ScalarLongAsync(connection, transaction,
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

record TaskListInfo(long Id, string Name, string Description, string CreatedAt, long TaskCount);
record CreateListRequest(string? Name, string? Description);
record UpdateListRequest(string? Name, string? Description);
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
