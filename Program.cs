using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

// v0.5 renames the database file. Move the older file automatically so upgrades keep all data.
var dbPath = Path.Combine(dataDir, "task-list.db");
var legacyDbPath = Path.Combine(dataDir, "todo.db");
if (!File.Exists(dbPath) && File.Exists(legacyDbPath))
    File.Move(legacyDbPath, dbPath);

var connectionString = $"Data Source={dbPath};Foreign Keys=True";
InitializeDatabase(connectionString);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/lists", async () =>
{
    var lists = new List<TaskListInfo>();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT l.id, l.name, l.description, l.created_at,
               COUNT(t.id) AS task_count
        FROM lists l
        LEFT JOIN tasks t ON t.list_id = l.id
        GROUP BY l.id, l.name, l.description, l.created_at
        ORDER BY l.id;
        """;

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        lists.Add(new TaskListInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4)));
    }

    return Results.Ok(lists);
});

app.MapPost("/api/lists", async (CreateListRequest request) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "List name is required." });

    var description = request.Description?.Trim() ?? string.Empty;
    var now = DateTimeOffset.UtcNow.ToString("O");

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    if (await ListNameExists(connection, name, null))
        return Results.BadRequest(new { error = "A list with that name already exists." });

    var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO lists (name, description, next_task_number, created_at)
        VALUES ($name, $description, 1, $createdAt);
        SELECT last_insert_rowid();
        """;
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$createdAt", now);

    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);
    return Results.Created($"/api/lists/{id}", new TaskListInfo(id, name, description, now, 0));
});

app.MapPatch("/api/lists/{id:long}", async (long id, UpdateListRequest request) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var existing = await GetList(connection, id);
    if (existing is null)
        return Results.NotFound();

    var name = request.Name is null ? existing.Name : request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "List name cannot be empty." });

    var description = request.Description is null ? existing.Description : request.Description.Trim();
    if (await ListNameExists(connection, name, id))
        return Results.BadRequest(new { error = "A list with that name already exists." });

    var command = connection.CreateCommand();
    command.CommandText = "UPDATE lists SET name = $name, description = $description WHERE id = $id;";
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$id", id);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(existing with { Name = name, Description = description });
});

app.MapDelete("/api/lists/{id:long}", async (long id) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var countCommand = connection.CreateCommand();
    countCommand.CommandText = "SELECT COUNT(*) FROM lists;";
    var listCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync());
    if (listCount <= 1)
        return Results.BadRequest(new { error = "You cannot delete the only remaining list." });

    if (await GetList(connection, id) is null)
        return Results.NotFound();

    await using var transaction = await connection.BeginTransactionAsync();

    var deleteSubtasks = connection.CreateCommand();
    deleteSubtasks.Transaction = (SqliteTransaction)transaction;
    deleteSubtasks.CommandText = """
        DELETE FROM subtasks
        WHERE parent_task_id IN (SELECT id FROM tasks WHERE list_id = $listId);
        """;
    deleteSubtasks.Parameters.AddWithValue("$listId", id);
    await deleteSubtasks.ExecuteNonQueryAsync();

    var deleteTasks = connection.CreateCommand();
    deleteTasks.Transaction = (SqliteTransaction)transaction;
    deleteTasks.CommandText = "DELETE FROM tasks WHERE list_id = $listId;";
    deleteTasks.Parameters.AddWithValue("$listId", id);
    await deleteTasks.ExecuteNonQueryAsync();

    var deleteList = connection.CreateCommand();
    deleteList.Transaction = (SqliteTransaction)transaction;
    deleteList.CommandText = "DELETE FROM lists WHERE id = $listId;";
    deleteList.Parameters.AddWithValue("$listId", id);
    await deleteList.ExecuteNonQueryAsync();

    await transaction.CommitAsync();
    return Results.NoContent();
});

app.MapGet("/api/lists/{listId:long}/tasks", async (long listId) =>
{
    var tasks = new List<TaskItem>();
    var taskById = new Dictionary<long, TaskItem>();

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    if (await GetList(connection, listId) is null)
        return Results.NotFound(new { error = "List not found." });

    var taskCommand = connection.CreateCommand();
    taskCommand.CommandText = """
        SELECT id, universal_id, list_id, task_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
        FROM tasks
        WHERE list_id = $listId
        ORDER BY task_number ASC;
        """;
    taskCommand.Parameters.AddWithValue("$listId", listId);

    await using (var reader = await taskCommand.ExecuteReaderAsync())
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
        var subtaskCommand = connection.CreateCommand();
        subtaskCommand.CommandText = """
            SELECT s.id, s.universal_id, s.parent_task_id, t.task_number, s.subtask_number,
                   s.title, s.description, s.status, s.created_at, s.updated_at,
                   s.completed_at, s.cancelled_at, s.reopened_at
            FROM subtasks s
            JOIN tasks t ON t.id = s.parent_task_id
            WHERE t.list_id = $listId
            ORDER BY t.task_number, s.subtask_number;
            """;
        subtaskCommand.Parameters.AddWithValue("$listId", listId);

        await using var reader = await subtaskCommand.ExecuteReaderAsync();
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

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var listCommand = connection.CreateCommand();
    listCommand.Transaction = (SqliteTransaction)transaction;
    listCommand.CommandText = "SELECT next_task_number FROM lists WHERE id = $listId;";
    listCommand.Parameters.AddWithValue("$listId", listId);
    var taskNumberValue = await listCommand.ExecuteScalarAsync();
    if (taskNumberValue is null)
    {
        await transaction.RollbackAsync();
        return Results.NotFound(new { error = "List not found." });
    }

    var taskNumber = Convert.ToInt32(taskNumberValue);
    var universalId = await AllocateUniversalId(connection, (SqliteTransaction)transaction);

    var command = connection.CreateCommand();
    command.Transaction = (SqliteTransaction)transaction;
    command.CommandText = """
        INSERT INTO tasks
            (universal_id, list_id, task_number, title, description, status, completed,
             created_at, updated_at, completed_at, cancelled_at, reopened_at, next_subtask_number)
        VALUES
            ($universalId, $listId, $taskNumber, $title, $description, 'Open', 0,
             $createdAt, NULL, NULL, NULL, NULL, 1);
        SELECT last_insert_rowid();
        """;
    command.Parameters.AddWithValue("$universalId", universalId);
    command.Parameters.AddWithValue("$listId", listId);
    command.Parameters.AddWithValue("$taskNumber", taskNumber);
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$createdAt", now);
    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);

    var advance = connection.CreateCommand();
    advance.Transaction = (SqliteTransaction)transaction;
    advance.CommandText = "UPDATE lists SET next_task_number = $next WHERE id = $listId;";
    advance.Parameters.AddWithValue("$next", taskNumber + 1);
    advance.Parameters.AddWithValue("$listId", listId);
    await advance.ExecuteNonQueryAsync();

    await transaction.CommitAsync();

    return Results.Created($"/api/tasks/{id}", new TaskItem(
        id, universalId, listId, taskNumber, taskNumber.ToString(), false,
        title, description, "Open", now, null, null, null, null, new List<SubtaskItem>()));
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

    var parent = await GetTask(connection, parentId);
    if (parent is null)
        return Results.NotFound(new { error = "Parent task was not found." });

    await using var transaction = await connection.BeginTransactionAsync();

    var numberCommand = connection.CreateCommand();
    numberCommand.Transaction = (SqliteTransaction)transaction;
    numberCommand.CommandText = "SELECT next_subtask_number FROM tasks WHERE id = $parentId;";
    numberCommand.Parameters.AddWithValue("$parentId", parentId);
    var subtaskNumber = Convert.ToInt32(await numberCommand.ExecuteScalarAsync());
    var universalId = await AllocateUniversalId(connection, (SqliteTransaction)transaction);

    var insert = connection.CreateCommand();
    insert.Transaction = (SqliteTransaction)transaction;
    insert.CommandText = """
        INSERT INTO subtasks
            (universal_id, parent_task_id, subtask_number, title, description, status, completed,
             created_at, updated_at, completed_at, cancelled_at, reopened_at)
        VALUES
            ($universalId, $parentId, $subtaskNumber, $title, $description, 'Open', 0,
             $createdAt, NULL, NULL, NULL, NULL);
        SELECT last_insert_rowid();
        """;
    insert.Parameters.AddWithValue("$universalId", universalId);
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

    return Results.Created($"/api/subtasks/{id}", new SubtaskItem(
        id, universalId, parentId, parent.TaskNumber, subtaskNumber,
        $"{parent.TaskNumber}.{subtaskNumber}", true,
        title, description, "Open", now, null, null, null, null));
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

app.MapPost("/api/lists/{listId:long}/import/markdown", async (long listId, HttpRequest request) =>
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

    var listNumberCommand = connection.CreateCommand();
    listNumberCommand.Transaction = (SqliteTransaction)transaction;
    listNumberCommand.CommandText = "SELECT next_task_number FROM lists WHERE id = $listId;";
    listNumberCommand.Parameters.AddWithValue("$listId", listId);
    var nextTaskValue = await listNumberCommand.ExecuteScalarAsync();
    if (nextTaskValue is null)
    {
        await transaction.RollbackAsync();
        return Results.NotFound(new { error = "List not found." });
    }

    var nextTaskNumber = Convert.ToInt32(nextTaskValue);

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
        var universalId = await AllocateUniversalId(connection, (SqliteTransaction)transaction);

        if (currentParentId is null || indent <= currentParentIndent)
        {
            var parentCommand = connection.CreateCommand();
            parentCommand.Transaction = (SqliteTransaction)transaction;
            parentCommand.CommandText = """
                INSERT INTO tasks
                    (universal_id, list_id, task_number, title, description, status, completed,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at, next_subtask_number)
                VALUES
                    ($universalId, $listId, $taskNumber, $title, '', $status, $completed,
                     $createdAt, NULL, $completedAt, NULL, NULL, 1);
                SELECT last_insert_rowid();
                """;
            parentCommand.Parameters.AddWithValue("$universalId", universalId);
            parentCommand.Parameters.AddWithValue("$listId", listId);
            parentCommand.Parameters.AddWithValue("$taskNumber", nextTaskNumber);
            parentCommand.Parameters.AddWithValue("$title", title);
            parentCommand.Parameters.AddWithValue("$status", status);
            parentCommand.Parameters.AddWithValue("$completed", done ? 1 : 0);
            parentCommand.Parameters.AddWithValue("$createdAt", now);
            parentCommand.Parameters.AddWithValue("$completedAt", done ? now : DBNull.Value);

            currentParentId = (long)(await parentCommand.ExecuteScalarAsync() ?? 0L);
            currentParentIndent = indent;
            nextSubtaskNumber = 0;
            nextTaskNumber++;
            tasksImported++;
        }
        else
        {
            nextSubtaskNumber++;

            var subtaskCommand = connection.CreateCommand();
            subtaskCommand.Transaction = (SqliteTransaction)transaction;
            subtaskCommand.CommandText = """
                INSERT INTO subtasks
                    (universal_id, parent_task_id, subtask_number, title, description, status, completed,
                     created_at, updated_at, completed_at, cancelled_at, reopened_at)
                VALUES
                    ($universalId, $parentId, $subtaskNumber, $title, '', $status, $completed,
                     $createdAt, NULL, $completedAt, NULL, NULL);
                """;
            subtaskCommand.Parameters.AddWithValue("$universalId", universalId);
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
        )
        WHERE list_id = $listId;
        """;
    syncSubtaskNumbers.Parameters.AddWithValue("$listId", listId);
    await syncSubtaskNumbers.ExecuteNonQueryAsync();

    var updateListNumber = connection.CreateCommand();
    updateListNumber.Transaction = (SqliteTransaction)transaction;
    updateListNumber.CommandText = "UPDATE lists SET next_task_number = $next WHERE id = $listId;";
    updateListNumber.Parameters.AddWithValue("$next", nextTaskNumber);
    updateListNumber.Parameters.AddWithValue("$listId", listId);
    await updateListNumber.ExecuteNonQueryAsync();

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
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            universal_id         INTEGER NULL,
            list_id              INTEGER NOT NULL DEFAULT 1,
            task_number          INTEGER NOT NULL DEFAULT 0,
            title                TEXT NOT NULL,
            description          TEXT NOT NULL DEFAULT '',
            status               TEXT NOT NULL DEFAULT 'Open',
            completed            INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
            created_at           TEXT NOT NULL,
            updated_at           TEXT NULL,
            completed_at         TEXT NULL,
            cancelled_at         TEXT NULL,
            reopened_at          TEXT NULL,
            next_subtask_number  INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS subtasks (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            universal_id    INTEGER NULL,
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
        """;
    create.ExecuteNonQuery();

    var taskColumns = GetColumns(connection, "tasks");
    AddColumnIfMissing(connection, "tasks", taskColumns, "description", "TEXT NOT NULL DEFAULT ''");
    var statusWasMissing = !taskColumns.Contains("status");
    AddColumnIfMissing(connection, "tasks", taskColumns, "status", "TEXT NOT NULL DEFAULT 'Open'");
    AddColumnIfMissing(connection, "tasks", taskColumns, "updated_at", "TEXT NULL");
    AddColumnIfMissing(connection, "tasks", taskColumns, "cancelled_at", "TEXT NULL");
    AddColumnIfMissing(connection, "tasks", taskColumns, "reopened_at", "TEXT NULL");
    AddColumnIfMissing(connection, "tasks", taskColumns, "next_subtask_number", "INTEGER NOT NULL DEFAULT 1");
    AddColumnIfMissing(connection, "tasks", taskColumns, "universal_id", "INTEGER NULL");
    AddColumnIfMissing(connection, "tasks", taskColumns, "list_id", "INTEGER NOT NULL DEFAULT 1");
    AddColumnIfMissing(connection, "tasks", taskColumns, "task_number", "INTEGER NOT NULL DEFAULT 0");

    var subtaskColumns = GetColumns(connection, "subtasks");
    AddColumnIfMissing(connection, "subtasks", subtaskColumns, "universal_id", "INTEGER NULL");

    if (statusWasMissing && taskColumns.Contains("completed"))
    {
        var migrateStatus = connection.CreateCommand();
        migrateStatus.CommandText = "UPDATE tasks SET status = CASE WHEN completed = 1 THEN 'Done' ELSE 'Open' END;";
        migrateStatus.ExecuteNonQuery();
    }

    // Older versions filled Last updated with the creation timestamp. Clear those redundant values.
    var clearRedundantUpdates = connection.CreateCommand();
    clearRedundantUpdates.CommandText = "UPDATE tasks SET updated_at = NULL WHERE updated_at = created_at;";
    clearRedundantUpdates.ExecuteNonQuery();

    var clearSubtaskUpdates = connection.CreateCommand();
    clearSubtaskUpdates.CommandText = "UPDATE subtasks SET updated_at = NULL WHERE updated_at = created_at;";
    clearSubtaskUpdates.ExecuteNonQuery();

    var listCountCommand = connection.CreateCommand();
    listCountCommand.CommandText = "SELECT COUNT(*) FROM lists;";
    var listCount = Convert.ToInt64(listCountCommand.ExecuteScalar());
    if (listCount == 0)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var createDefaultList = connection.CreateCommand();
        createDefaultList.CommandText = """
            INSERT INTO lists (id, name, description, next_task_number, created_at)
            VALUES (1, 'Tasks', '', 1, $createdAt);
            """;
        createDefaultList.Parameters.AddWithValue("$createdAt", now);
        createDefaultList.ExecuteNonQuery();
    }

    // Existing visible IDs become task numbers in the default list, so upgrades look unchanged.
    var migrateTaskNumbers = connection.CreateCommand();
    migrateTaskNumbers.CommandText = """
        UPDATE tasks SET list_id = 1 WHERE list_id IS NULL OR list_id = 0;
        UPDATE tasks SET task_number = id WHERE task_number IS NULL OR task_number = 0;
        """;
    migrateTaskNumbers.ExecuteNonQuery();

    var syncDefaultList = connection.CreateCommand();
    syncDefaultList.CommandText = """
        UPDATE lists
        SET next_task_number = MAX(
            next_task_number,
            COALESCE((SELECT MAX(task_number) + 1 FROM tasks WHERE list_id = lists.id), 1)
        );
        """;
    syncDefaultList.ExecuteNonQuery();

    var syncSubtaskNumbers = connection.CreateCommand();
    syncSubtaskNumbers.CommandText = """
        UPDATE tasks
        SET next_subtask_number = MAX(
            next_subtask_number,
            COALESCE((SELECT MAX(subtask_number) + 1 FROM subtasks WHERE parent_task_id = tasks.id), 1)
        );
        """;
    syncSubtaskNumbers.ExecuteNonQuery();

    AssignMissingUniversalIds(connection);

    var indexes = connection.CreateCommand();
    indexes.CommandText = """
        CREATE UNIQUE INDEX IF NOT EXISTS idx_tasks_list_number
        ON tasks(list_id, task_number);

        CREATE UNIQUE INDEX IF NOT EXISTS idx_tasks_universal_id
        ON tasks(universal_id) WHERE universal_id IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS idx_subtasks_universal_id
        ON subtasks(universal_id) WHERE universal_id IS NOT NULL;

        CREATE INDEX IF NOT EXISTS idx_tasks_list
        ON tasks(list_id, task_number);

        CREATE INDEX IF NOT EXISTS idx_subtasks_parent
        ON subtasks(parent_task_id, subtask_number);
        """;
    indexes.ExecuteNonQuery();
}

static HashSet<string> GetColumns(SqliteConnection connection, string table)
{
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({table});";
    using var reader = command.ExecuteReader();
    while (reader.Read())
        columns.Add(reader.GetString(1));
    return columns;
}

static void AddColumnIfMissing(SqliteConnection connection, string table, HashSet<string> columns, string name, string definition)
{
    if (columns.Contains(name))
        return;

    var command = connection.CreateCommand();
    command.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition};";
    command.ExecuteNonQuery();
    columns.Add(name);
}

static void AssignMissingUniversalIds(SqliteConnection connection)
{
    // Ensure the allocator knows about any IDs that may already exist.
    var sync = connection.CreateCommand();
    sync.CommandText = """
        INSERT OR IGNORE INTO universal_ids(id)
        SELECT universal_id FROM tasks WHERE universal_id IS NOT NULL;
        INSERT OR IGNORE INTO universal_ids(id)
        SELECT universal_id FROM subtasks WHERE universal_id IS NOT NULL;
        """;
    sync.ExecuteNonQuery();

    var missing = new List<(string Kind, long Id, string CreatedAt)>();
    var read = connection.CreateCommand();
    read.CommandText = """
        SELECT 'task' AS kind, id, created_at FROM tasks WHERE universal_id IS NULL
        UNION ALL
        SELECT 'subtask' AS kind, id, created_at FROM subtasks WHERE universal_id IS NULL
        ORDER BY 3, 1, 2;
        """;
    using (var reader = read.ExecuteReader())
    {
        while (reader.Read())
            missing.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
    }

    using var transaction = connection.BeginTransaction();
    foreach (var item in missing)
    {
        var allocate = connection.CreateCommand();
        allocate.Transaction = transaction;
        allocate.CommandText = "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();";
        var universalId = Convert.ToInt64(allocate.ExecuteScalar());

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE {(item.Kind == "task" ? "tasks" : "subtasks")} SET universal_id = $uid WHERE id = $id;";
        update.Parameters.AddWithValue("$uid", universalId);
        update.Parameters.AddWithValue("$id", item.Id);
        update.ExecuteNonQuery();
    }
    transaction.Commit();
}

static async Task<long> AllocateUniversalId(SqliteConnection connection, SqliteTransaction transaction)
{
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();";
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<TaskListInfo?> GetList(SqliteConnection connection, long id)
{
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT l.id, l.name, l.description, l.created_at, COUNT(t.id)
        FROM lists l
        LEFT JOIN tasks t ON t.list_id = l.id
        WHERE l.id = $id
        GROUP BY l.id, l.name, l.description, l.created_at;
        """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return new TaskListInfo(
        reader.GetInt64(0), reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.GetString(3), reader.GetInt64(4));
}

static async Task<bool> ListNameExists(SqliteConnection connection, string name, long? excludingId)
{
    var command = connection.CreateCommand();
    command.CommandText = excludingId is null
        ? "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE);"
        : "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE AND id <> $id);";
    command.Parameters.AddWithValue("$name", name);
    if (excludingId is not null)
        command.Parameters.AddWithValue("$id", excludingId.Value);
    return Convert.ToInt32(await command.ExecuteScalarAsync()) != 0;
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
            if (status == "Done") completedAt = now;
            else if (status == "Cancelled") cancelledAt = now;
            else if (status == "Open") reopenedAt = now;
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

static TaskItem ReadTask(SqliteDataReader reader)
{
    var id = reader.GetInt64(0);
    var universalId = reader.GetInt64(1);
    var listId = reader.GetInt64(2);
    var taskNumber = reader.GetInt32(3);

    return new TaskItem(
        id, universalId, listId, taskNumber, taskNumber.ToString(), false,
        reader.GetString(4),
        reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        new List<SubtaskItem>());
}

static SubtaskItem ReadSubtask(SqliteDataReader reader)
{
    var id = reader.GetInt64(0);
    var universalId = reader.GetInt64(1);
    var parentId = reader.GetInt64(2);
    var parentTaskNumber = reader.GetInt32(3);
    var number = reader.GetInt32(4);

    return new SubtaskItem(
        id, universalId, parentId, parentTaskNumber, number,
        $"{parentTaskNumber}.{number}", true,
        reader.GetString(5),
        reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));
}

static async Task<TaskItem?> GetTask(SqliteConnection connection, long id)
{
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, universal_id, list_id, task_number, title, description, status,
               created_at, updated_at, completed_at, cancelled_at, reopened_at
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
        SELECT s.id, s.universal_id, s.parent_task_id, t.task_number, s.subtask_number,
               s.title, s.description, s.status, s.created_at, s.updated_at,
               s.completed_at, s.cancelled_at, s.reopened_at
        FROM subtasks s
        JOIN tasks t ON t.id = s.parent_task_id
        WHERE s.id = $id;
        """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;
    return ReadSubtask(reader);
}

record TaskListInfo(long Id, string Name, string Description, string CreatedAt, long TaskCount);
record CreateListRequest(string? Name, string? Description);
record UpdateListRequest(string? Name, string? Description);

record TaskItem(
    long Id,
    long UniversalId,
    long ListId,
    int TaskNumber,
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
    long UniversalId,
    long ParentTaskId,
    int ParentTaskNumber,
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
