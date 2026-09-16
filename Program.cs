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
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = """
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

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        tasks.Add(ReadTask(reader));

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
            ($title, $description, 'Open', 0, $createdAt, $updatedAt,
             NULL, NULL, NULL);
        SELECT last_insert_rowid();
        """;
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$createdAt", now);
    command.Parameters.AddWithValue("$updatedAt", now);

    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);
    return Results.Created($"/api/tasks/{id}", new TaskItem(
        id, title, description, "Open", now, now, null, null, null));
});

app.MapPatch("/api/tasks/{id:long}", async (long id, UpdateTaskRequest request) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var existing = await GetTask(connection, id);
    if (existing is null)
        return Results.NotFound();

    var title = request.Title is null ? existing.Title : request.Title.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Task title cannot be empty." });

    var description = request.Description is null ? existing.Description : request.Description.Trim();
    var status = existing.Status;

    if (request.Status is not null)
    {
        status = NormalizeStatus(request.Status);
        if (status is null)
            return Results.BadRequest(new { error = "Status must be Open, Done, or Cancelled." });
    }

    var now = DateTimeOffset.UtcNow.ToString("O");
    var completedAt = existing.CompletedAt;
    var cancelledAt = existing.CancelledAt;
    var reopenedAt = existing.ReopenedAt;

    if (!string.Equals(status, existing.Status, StringComparison.Ordinal))
    {
        if (status == "Done")
            completedAt = now;
        else if (status == "Cancelled")
            cancelledAt = now;
        else if (status == "Open")
            reopenedAt = now;
    }

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
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$description", description);
    command.Parameters.AddWithValue("$status", status);
    command.Parameters.AddWithValue("$completed", status == "Done" ? 1 : 0);
    command.Parameters.AddWithValue("$updatedAt", now);
    command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$cancelledAt", (object?)cancelledAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$reopenedAt", (object?)reopenedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$id", id);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new TaskItem(
        id, title, description, status, existing.CreatedAt, now,
        completedAt, cancelledAt, reopenedAt));
});

app.MapDelete("/api/tasks/{id:long}", async (long id) =>
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM tasks WHERE id = $id;";
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

    // First-pass importer. We can adapt this to your exact long-running Markdown format later.
    var taskRegex = new Regex(@"^\s*[-*+]\s+\[(?<state>[ xX])\]\s+(?<title>.+?)\s*$", RegexOptions.Compiled);
    var imported = 0;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
    {
        var match = taskRegex.Match(line);
        if (!match.Success)
            continue;

        var title = match.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(title))
            continue;

        var done = !string.Equals(match.Groups["state"].Value, " ", StringComparison.Ordinal);
        var status = done ? "Done" : "Open";
        var now = DateTimeOffset.UtcNow.ToString("O");

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO tasks
                (title, description, status, completed, created_at, updated_at,
                 completed_at, cancelled_at, reopened_at)
            VALUES
                ($title, '', $status, $completed, $createdAt, $updatedAt,
                 $completedAt, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completed", done ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$completedAt", done ? now : DBNull.Value);
        await command.ExecuteNonQueryAsync();
        imported++;
    }

    await transaction.CommitAsync();
    return Results.Ok(new { imported });
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
            reopened_at  TEXT NULL
        );
        """;
    create.ExecuteNonQuery();

    // Migrate a v0.1 database in place. Existing task IDs are preserved.
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

    if (statusWasMissing && columns.Contains("completed"))
    {
        var migrateStatus = connection.CreateCommand();
        migrateStatus.CommandText = """
            UPDATE tasks
            SET status = CASE WHEN completed = 1 THEN 'Done' ELSE 'Open' END;
            """;
        migrateStatus.ExecuteNonQuery();
    }

    var fillUpdated = connection.CreateCommand();
    fillUpdated.CommandText = "UPDATE tasks SET updated_at = created_at WHERE updated_at IS NULL;";
    fillUpdated.ExecuteNonQuery();
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

static TaskItem ReadTask(SqliteDataReader reader) => new(
    reader.GetInt64(0),
    reader.GetString(1),
    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
    reader.GetString(3),
    reader.GetString(4),
    reader.IsDBNull(5) ? reader.GetString(4) : reader.GetString(5),
    reader.IsDBNull(6) ? null : reader.GetString(6),
    reader.IsDBNull(7) ? null : reader.GetString(7),
    reader.IsDBNull(8) ? null : reader.GetString(8)
);

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

record TaskItem(
    long Id,
    string Title,
    string Description,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    string? CompletedAt,
    string? CancelledAt,
    string? ReopenedAt);

record CreateTaskRequest(string? Title, string? Description);
record UpdateTaskRequest(string? Title, string? Description, string? Status);
