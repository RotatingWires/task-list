using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "todo.db");
var connectionString = $"Data Source={dbPath}";

InitializeDatabase(connectionString);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/tasks", async () =>
{
    var tasks = new List<TaskItem>();
    var taskById = new Dictionary<long, TaskItem>();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var taskCommand = connection.CreateCommand();
    taskCommand.CommandText = """
        SELECT id, title, description, status, created_at, updated_at,
               completed_at, cancelled_at, reopened_at
        FROM tasks
        ORDER BY
            CASE status
                WHEN 'Open' THEN 0
                WHEN 'Cancelled' THEN 1
                WHEN 'Done' THEN 2
                ELSE 3
            END,
            id DESC;
        """;

    await using (var reader = await taskCommand.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var task = ReadTask(reader);
            tasks.Add(task);
            taskById[task.Id] = task;
        }
    }

    var subtaskCommand = connection.CreateCommand();
    subtaskCommand.CommandText = """
        SELECT id, parent_task_id, subtask_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM subtasks
        ORDER BY parent_task_id, subtask_number;
        """;

    await using (var reader = await subtaskCommand.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var subtask = ReadSubtask(reader);
            if (taskById.TryGetValue(subtask.ParentTaskId, out var parent))
                parent.Subtasks.Add(subtask);
        }
    }

    return Results.Ok(tasks);
});

app.MapPost("/api/tasks", async (CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Task title is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO tasks
            (title, description, status, completed, created_at, updated_at,
             completed_at, cancelled_at, reopened_at)
        VALUES
            ($title, $description, 'Open', 0, $createdAt, NULL,
             NULL, NULL, NULL);
        SELECT last_insert_rowid();
        """;
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$createdAt", now);

    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);
    return Results.Created($"/api/tasks/{id}", new TaskItem(
        id, id.ToString(), false, title, description, "Open", now, null,
        null, null, null, new List<SubtaskItem>()));
});

app.MapPost("/api/tasks/{parentId:long}/subtasks", async (long parentId, CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Subtask title is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    if (await GetTask(connection, parentId) is null)
        return Results.NotFound(new { error = $"Task #{parentId} was not found." });

    await using var transaction = await connection.BeginTransactionAsync();

    var numberCommand = connection.CreateCommand();
    numberCommand.Transaction = (SqliteTransaction)transaction;
    numberCommand.CommandText = "SELECT next_subtask_number FROM tasks WHERE id = $parentId;";
    numberCommand.Parameters.AddWithValue("$parentId", parentId);
    var subtaskNumber = Convert.ToInt32(await numberCommand.ExecuteScalarAsync());

    var insert = connection.CreateCommand();
    insert.Transaction = (SqliteTransaction)transaction;
    insert.CommandText = """
        INSERT INTO subtasks
            (parent_task_id, subtask_number, title, description, status, completed,
             created_at, updated_at, completed_at, cancelled_at, reopened_at)
        VALUES
            ($parentId, $subtaskNumber, $title, $description, 'Open', 0,
             $createdAt, NULL, NULL, NULL, NULL);
        SELECT last_insert_rowid();
        """;
    insert.Parameters.AddWithValue("$parentId", parentId);
    insert.Parameters.AddWithValue("$subtaskNumber", subtaskNumber);
    insert.Parameters.AddWithValue("$title", title);
    insert.Parameters.AddWithValue("$description", description);
    insert.Parameters.AddWithValue("$createdAt", now);

    var id = (long)(await insert.ExecuteScalarAsync() ?? 0L);

    var advanceNumber = connection.CreateCommand();
    advanceNumber.Transaction = (SqliteTransaction)transaction;
    advanceNumber.CommandText = "UPDATE tasks SET next_subtask_number = $nextNumber WHERE id = $parentId;";
    advanceNumber.Parameters.AddWithValue("$nextNumber", subtaskNumber + 1);
    advanceNumber.Parameters.AddWithValue("$parentId", parentId);
    await advanceNumber.ExecuteNonQueryAsync();

    await transaction.CommitAsync();

    var subtask = new SubtaskItem(
        id, parentId, subtaskNumber, $"{parentId}.{subtaskNumber}", true,
        title, description, "Open", now, null, null, null, null);

    return Results.Created($"/api/subtasks/{id}", subtask);
});

app.MapPatch("/api/tasks/{id:long}", async (long id, UpdateTaskRequest request) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var existing = await GetTask(connection, id);
    if (existing is null)
        return Results.NotFound();

    var result = BuildUpdatedValues(existing.Title, existing.Description, existing.Status,
        existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);

    if (result.Error is not null)
        return Results.BadRequest(new { error = result.Error });

    var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE tasks
        SET title = $title,
            description = $description,
            status = $status,
            completed = $completed,
            updated_at = $updatedAt,
            completed_at = $completedAt,
            cancelled_at = $cancelledAt,
            reopened_at = $reopenedAt
        WHERE id = $id;
        """;
    AddUpdateParameters(command, result, id);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new TaskItem(
        id, id.ToString(), false, result.Title!, result.Description!, result.Status!, existing.CreatedAt,
        result.UpdatedAt, result.CompletedAt, result.CancelledAt, result.ReopenedAt, existing.Subtasks));
});

app.MapPatch("/api/subtasks/{id:long}", async (long id, UpdateTaskRequest request) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var existing = await GetSubtask(connection, id);
    if (existing is null)
        return Results.NotFound();

    var result = BuildUpdatedValues(existing.Title, existing.Description, existing.Status,
        existing.UpdatedAt, existing.CompletedAt, existing.CancelledAt, existing.ReopenedAt, request);

    if (result.Error is not null)
        return Results.BadRequest(new { error = result.Error });

    var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE subtasks
        SET title = $title,
            description = $description,
            status = $status,
            completed = $completed,
            updated_at = $updatedAt,
            completed_at = $completedAt,
            cancelled_at = $cancelledAt,
            reopened_at = $reopenedAt
        WHERE id = $id;
        """;
    AddUpdateParameters(command, result, id);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(existing with
    {
        Title = result.Title!,
        Description = result.Description!,
        Status = result.Status!,
        UpdatedAt = result.UpdatedAt,
        CompletedAt = result.CompletedAt,
        CancelledAt = result.CancelledAt,
        ReopenedAt = result.ReopenedAt
    });
});

app.MapDelete("/api/tasks/{id:long}", async (long id) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var deleteSubtasks = connection.CreateCommand();
    deleteSubtasks.Transaction = (SqliteTransaction)transaction;
    deleteSubtasks.CommandText = "DELETE FROM subtasks WHERE parent_task_id = $id;";
    deleteSubtasks.Parameters.AddWithValue("$id", id);
    await deleteSubtasks.ExecuteNonQueryAsync();

    var command = connection.CreateCommand();
    command.Transaction = (SqliteTransaction)transaction;
    command.CommandText = "DELETE FROM tasks WHERE id = $id;";
    command.Parameters.AddWithValue("$id", id);

    var changed = await command.ExecuteNonQueryAsync();
    if (changed == 0)
    {
        await transaction.RollbackAsync();
        return Results.NotFound();
    }

    await transaction.CommitAsync();
    return Results.NoContent();
});

app.MapDelete("/api/subtasks/{id:long}", async (long id) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM subtasks WHERE id = $id;";
    command.Parameters.AddWithValue("$id", id);

    var changed = await command.ExecuteNonQueryAsync();
    return changed == 0 ? Results.NotFound() : Results.NoContent();
});

app.MapPost("/api/import/markdown", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var markdown = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(markdown))
        return Results.BadRequest(new { error = "Markdown file is empty." });

    var checklistRegex = new Regex(
        @"^(?<indent>[ \t]*)[-*+]\s+\[(?<state>[ xX])\]\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled);

    var tasksImported = 0;
    var subtasksImported = 0;
    long? currentParentId = null;
    int currentParentIndent = 0;
    int nextSubtaskNumber = 0;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
    {
        var match = checklistRegex.Match(line);
        if (!match.Success)
            continue;

        var title = match.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            continue;

        var indent = IndentWidth(match.Groups["indent"].Value);
        var done = !string.Equals(match.Groups["state"].Value, " ", StringComparison.Ordinal);
        var status = done ? "Done" : "Open";
        var now = DateTimeOffset.UtcNow.ToString("O");

        if (currentParentId is null || indent <= currentParentIndent)
        {
            var parentCommand = connection.CreateCommand();
            parentCommand.Transaction = (SqliteTransaction)transaction;
            parentCommand.CommandText = """
                INSERT INTO tasks
                    (title, description, status, completed, created_at, updated_at,
                     completed_at, cancelled_at, reopened_at)
                VALUES
                    ($title, '', $status, $completed, $createdAt, NULL,
                     $completedAt, NULL, NULL);
                SELECT last_insert_rowid();
                """;
            parentCommand.Parameters.AddWithValue("$title", title);
            parentCommand.Parameters.AddWithValue("$status", status);
            parentCommand.Parameters.AddWithValue("$completed", done ? 1 : 0);
            parentCommand.Parameters.AddWithValue("$createdAt", now);
            parentCommand.Parameters.AddWithValue("$completedAt", done ? now : DBNull.Value);

            currentParentId = (long)(await parentCommand.ExecuteScalarAsync() ?? 0L);
            currentParentIndent = indent;
            nextSubtaskNumber = 0;
            tasksImported++;
        }
        else
        {
            nextSubtaskNumber++;

            var subtaskCommand = connection.CreateCommand();
            subtaskCommand.Transaction = (SqliteTransaction)transaction;
            subtaskCommand.CommandText = """
                INSERT INTO subtasks
                    (parent_task_id, subtask_number, title, description, status, completed,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at)
                VALUES
                    ($parentId, $subtaskNumber, $title, '', $status, $completed,
                     $createdAt, NULL, $completedAt, NULL, NULL);
                """;
            subtaskCommand.Parameters.AddWithValue("$parentId", currentParentId.Value);
            subtaskCommand.Parameters.AddWithValue("$subtaskNumber", nextSubtaskNumber);
            subtaskCommand.Parameters.AddWithValue("$title", title);
            subtaskCommand.Parameters.AddWithValue("$status", status);
            subtaskCommand.Parameters.AddWithValue("$completed", done ? 1 : 0);
            subtaskCommand.Parameters.AddWithValue("$createdAt", now);
            subtaskCommand.Parameters.AddWithValue("$completedAt", done ? now : DBNull.Value);
            await subtaskCommand.ExecuteNonQueryAsync();
            subtasksImported++;
        }
    }

    var syncSubtaskNumbers = connection.CreateCommand();
    syncSubtaskNumbers.Transaction = (SqliteTransaction)transaction;
    syncSubtaskNumbers.CommandText = """
        UPDATE tasks
        SET next_subtask_number = MAX(
            next_subtask_number,
            COALESCE((
                SELECT MAX(subtask_number) + 1
                FROM subtasks
                WHERE parent_task_id = tasks.id
            ), 1)
        );
        """;
    await syncSubtaskNumbers.ExecuteNonQueryAsync();

    await transaction.CommitAsync();
    return Results.Ok(new
    {
        imported = tasksImported + subtasksImported,
        tasksImported,
        subtasksImported
    });
});

app.MapFallbackToFile("index.html");
app.Run();

static void InitializeDatabase(string connectionString)
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    var create = connection.CreateCommand();
    create.CommandText = """
        CREATE TABLE IF NOT EXISTS tasks (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            title        TEXT NOT NULL,
            description  TEXT NOT NULL DEFAULT '',
            status       TEXT NOT NULL DEFAULT 'Open',
            completed    INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
            created_at   TEXT NOT NULL,
            updated_at   TEXT NULL,
            completed_at TEXT NULL,
            cancelled_at TEXT NULL,
            reopened_at  TEXT NULL,
            next_subtask_number INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS subtasks (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            parent_task_id  INTEGER NOT NULL,
            subtask_number  INTEGER NOT NULL,
            title           TEXT NOT NULL,
            description     TEXT NOT NULL DEFAULT '',
            status          TEXT NOT NULL DEFAULT 'Open',
            completed       INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
            created_at      TEXT NOT NULL,
            updated_at      TEXT NULL,
            completed_at    TEXT NULL,
            cancelled_at    TEXT NULL,
            reopened_at     TEXT NULL,
            UNIQUE(parent_task_id, subtask_number)
        );

        CREATE INDEX IF NOT EXISTS idx_subtasks_parent
        ON subtasks(parent_task_id, subtask_number);
        """;
    create.ExecuteNonQuery();

    // Migrate older databases in place. Existing task IDs are preserved.
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pragma = connection.CreateCommand();
    pragma.CommandText = "PRAGMA table_info(tasks);";
    using (var reader = pragma.ExecuteReader())
    {
        while (reader.Read())
            columns.Add(reader.GetString(1));
    }

    AddColumnIfMissing(connection, columns, "description", "TEXT NOT NULL DEFAULT ''");
    var statusWasMissing = !columns.Contains("status");
    AddColumnIfMissing(connection, columns, "status", "TEXT NOT NULL DEFAULT 'Open'");
    AddColumnIfMissing(connection, columns, "updated_at", "TEXT NULL");
    AddColumnIfMissing(connection, columns, "cancelled_at", "TEXT NULL");
    AddColumnIfMissing(connection, columns, "reopened_at", "TEXT NULL");
    AddColumnIfMissing(connection, columns, "next_subtask_number", "INTEGER NOT NULL DEFAULT 1");

    if (statusWasMissing && columns.Contains("completed"))
    {
        var migrateStatus = connection.CreateCommand();
        migrateStatus.CommandText = """
            UPDATE tasks
            SET status = CASE WHEN completed = 1 THEN 'Done' ELSE 'Open' END;
            """;
        migrateStatus.ExecuteNonQuery();
    }

    // v0.1-v0.3 wrote the creation time into Last updated even when nothing had changed.
    // Exact matches are untouched tasks, so clear those redundant values during migration.
    var clearRedundantUpdated = connection.CreateCommand();
    clearRedundantUpdated.CommandText = "UPDATE tasks SET updated_at = NULL WHERE updated_at = created_at;";
    clearRedundantUpdated.ExecuteNonQuery();

    // Never reuse a visible subtask ID, even if the highest-numbered subtask was deleted.
    var syncSubtaskNumbers = connection.CreateCommand();
    syncSubtaskNumbers.CommandText = """
        UPDATE tasks
        SET next_subtask_number = MAX(
            next_subtask_number,
            COALESCE((
                SELECT MAX(subtask_number) + 1
                FROM subtasks
                WHERE parent_task_id = tasks.id
            ), 1)
        );
        """;
    syncSubtaskNumbers.ExecuteNonQuery();
}

static void AddColumnIfMissing(SqliteConnection connection, HashSet<string> columns, string name, string definition)
{
    if (columns.Contains(name))
        return;

    var command = connection.CreateCommand();
    command.CommandText = $"ALTER TABLE tasks ADD COLUMN {name} {definition};";
    command.ExecuteNonQuery();
    columns.Add(name);
}

static string? NormalizeStatus(string status)
{
    if (status.Equals("Open", StringComparison.OrdinalIgnoreCase)) return "Open";
    if (status.Equals("Done", StringComparison.OrdinalIgnoreCase)) return "Done";
    if (status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
    return null;
}

static UpdateValues BuildUpdatedValues(
    string existingTitle,
    string existingDescription,
    string existingStatus,
    string? existingUpdatedAt,
    string? existingCompletedAt,
    string? existingCancelledAt,
    string? existingReopenedAt,
    UpdateTaskRequest request)
{
    var title = request.Title is null ? existingTitle : request.Title.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return UpdateValues.WithError("Task title cannot be empty.");

    var description = request.Description is null ? existingDescription : request.Description.Trim();
    var status = existingStatus;

    if (request.Status is not null)
    {
        status = NormalizeStatus(request.Status);
        if (status is null)
            return UpdateValues.WithError("Status must be Open, Done, or Cancelled.");
    }

    var changed = !string.Equals(title, existingTitle, StringComparison.Ordinal) ||
                  !string.Equals(description, existingDescription, StringComparison.Ordinal) ||
                  !string.Equals(status, existingStatus, StringComparison.Ordinal);

    var updatedAt = existingUpdatedAt;
    var completedAt = existingCompletedAt;
    var cancelledAt = existingCancelledAt;
    var reopenedAt = existingReopenedAt;

    if (changed)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        updatedAt = now;

        if (!string.Equals(status, existingStatus, StringComparison.Ordinal))
        {
            if (status == "Done")
                completedAt = now;
            else if (status == "Cancelled")
                cancelledAt = now;
            else if (status == "Open")
                reopenedAt = now;
        }
    }

    return new UpdateValues(title, description, status, updatedAt, completedAt, cancelledAt, reopenedAt, null);
}

static void AddUpdateParameters(SqliteCommand command, UpdateValues values, long id)
{
    command.Parameters.AddWithValue("$title", values.Title!);
    command.Parameters.AddWithValue("$description", values.Description!);
    command.Parameters.AddWithValue("$status", values.Status!);
    command.Parameters.AddWithValue("$completed", values.Status == "Done" ? 1 : 0);
    command.Parameters.AddWithValue("$updatedAt", (object?)values.UpdatedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$completedAt", (object?)values.CompletedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$cancelledAt", (object?)values.CancelledAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$reopenedAt", (object?)values.ReopenedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$id", id);
}

static int IndentWidth(string indent)
{
    var width = 0;
    foreach (var ch in indent)
        width += ch == '\t' ? 4 : 1;
    return width;
}

static TaskItem ReadTask(SqliteDataReader reader) => new(
    reader.GetInt64(0),
    reader.GetInt64(0).ToString(),
    false,
    reader.GetString(1),
    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
    reader.GetString(3),
    reader.GetString(4),
    reader.IsDBNull(5) ? null : reader.GetString(5),
    reader.IsDBNull(6) ? null : reader.GetString(6),
    reader.IsDBNull(7) ? null : reader.GetString(7),
    reader.IsDBNull(8) ? null : reader.GetString(8),
    new List<SubtaskItem>()
);

static SubtaskItem ReadSubtask(SqliteDataReader reader)
{
    var id = reader.GetInt64(0);
    var parentId = reader.GetInt64(1);
    var number = reader.GetInt32(2);

    return new SubtaskItem(
        id,
        parentId,
        number,
        $"{parentId}.{number}",
        true,
        reader.GetString(3),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10)
    );
}

static async Task<TaskItem?> GetTask(SqliteConnection connection, long id)
{
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, title, description, status, created_at, updated_at,
               completed_at, cancelled_at, reopened_at
        FROM tasks
        WHERE id = $id;
        """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return ReadTask(reader);
}

static async Task<SubtaskItem?> GetSubtask(SqliteConnection connection, long id)
{
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, parent_task_id, subtask_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM subtasks
        WHERE id = $id;
        """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return ReadSubtask(reader);
}

record TaskItem(
    long Id,
    string DisplayId,
    bool IsSubtask,
    string Title,
    string Description,
    string Status,
    string CreatedAt,
    string? UpdatedAt,
    string? CompletedAt,
    string? CancelledAt,
    string? ReopenedAt,
    List<SubtaskItem> Subtasks);

record SubtaskItem(
    long Id,
    long ParentTaskId,
    int SubtaskNumber,
    string DisplayId,
    bool IsSubtask,
    string Title,
    string Description,
    string Status,
    string CreatedAt,
    string? UpdatedAt,
    string? CompletedAt,
    string? CancelledAt,
    string? ReopenedAt);

record CreateTaskRequest(string? Title, string? Description);
record UpdateTaskRequest(string? Title, string? Description, string? Status);
record UpdateValues(
    string? Title,
    string? Description,
    string? Status,
    string? UpdatedAt,
    string? CompletedAt,
    string? CancelledAt,
    string? ReopenedAt,
    string? Error)
{
    public static UpdateValues WithError(string error) =>
        new(null, null, null, null, null, null, null, error);
}
