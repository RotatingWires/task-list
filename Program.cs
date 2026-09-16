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
        SELECT id, title, completed, created_at, completed_at
        FROM tasks
        ORDER BY completed ASC, id DESC;
        """;

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        tasks.Add(new TaskItem(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2) == 1,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4)
        ));
    }

    return Results.Ok(tasks);
});

app.MapPost("/api/tasks", async (CreateTaskRequest request) =>
{
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Task title is required." });

    var now = DateTimeOffset.UtcNow.ToString("O");

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO tasks (title, completed, created_at, completed_at)
        VALUES ($title, 0, $createdAt, NULL);
        SELECT last_insert_rowid();
        """;
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$createdAt", now);

    var id = (long)(await command.ExecuteScalarAsync() ?? 0L);
    return Results.Created($"/api/tasks/{id}", new TaskItem(id, title, false, now, null));
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

    var completed = request.Completed ?? existing.Completed;
    var completedAt = completed
        ? existing.CompletedAt ?? DateTimeOffset.UtcNow.ToString("O")
        : null;

    var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE tasks
        SET title = $title,
            completed = $completed,
            completed_at = $completedAt
        WHERE id = $id;
        """;
    command.Parameters.AddWithValue("$title", title);
    command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
    command.Parameters.AddWithValue("$completedAt", (object?)completedAt ?? DBNull.Value);
    command.Parameters.AddWithValue("$id", id);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new TaskItem(id, title, completed, existing.CreatedAt, completedAt));
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

        var completed = !string.Equals(match.Groups["state"].Value, " ", StringComparison.Ordinal);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO tasks (title, completed, created_at, completed_at)
            VALUES ($title, $completed, $createdAt, $completedAt);
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$completedAt", completed ? now : DBNull.Value);
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

    var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS tasks (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            title        TEXT NOT NULL,
            completed    INTEGER NOT NULL DEFAULT 0 CHECK (completed IN (0, 1)),
            created_at   TEXT NOT NULL,
            completed_at TEXT NULL
        );
        """;
    command.ExecuteNonQuery();
}

static async Task<TaskItem?> GetTask(SqliteConnection connection, long id)
{
    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT id, title, completed, created_at, completed_at
        FROM tasks
        WHERE id = $id;
        """;
    command.Parameters.AddWithValue("$id", id);

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return new TaskItem(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt64(2) == 1,
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4)
    );
}

record TaskItem(long Id, string Title, bool Completed, string CreatedAt, string? CompletedAt);
record CreateTaskRequest(string? Title);
record UpdateTaskRequest(string? Title, bool? Completed);
