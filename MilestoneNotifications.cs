using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

static class MilestoneNotifications
{
    private static readonly int[] EventThresholds = [100, 500, 1000, 2000, 3000, 5000, 10000];
    private static readonly int[] UniversalThresholds = [100, 500, 1000, 2000, 2500, 3000, 5000, 10000];

    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS milestone_notifications (
                    key TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('created', 'completed', 'universal')),
                    threshold INTEGER NOT NULL,
                    reached_at TEXT NOT NULL,
                    historical INTEGER NOT NULL DEFAULT 0,
                    viewed_at TEXT NULL,
                    viewed_by TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_milestone_notifications_pending
                    ON milestone_notifications(viewed_at, reached_at);

                CREATE TABLE IF NOT EXISTS milestone_notification_state (
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    initialized_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT EXISTS(SELECT 1 FROM milestone_notification_state WHERE id = 1);";
            var initialized = Convert.ToInt64(check.ExecuteScalar()) != 0;
            if (!initialized) SeedExistingMilestones(connection);
        }

        using var triggers = connection.CreateCommand();
        triggers.CommandText = """
            CREATE TRIGGER IF NOT EXISTS trg_task_events_milestone_notification
            AFTER INSERT ON task_events
            WHEN NEW.event_type IN ('Created', 'Completed')
            BEGIN
                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, reached_at, historical, viewed_at, viewed_by)
                SELECT
                    lower(NEW.event_type) || ':' || totals.total,
                    lower(NEW.event_type),
                    totals.total,
                    CASE
                        WHEN NEW.event_at IS NULL OR lower(NEW.event_at) = 'unknown'
                            THEN strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                        ELSE NEW.event_at
                    END,
                    0,
                    NULL,
                    NULL
                FROM (
                    SELECT COUNT(*) AS total
                    FROM task_events
                    WHERE event_type = NEW.event_type
                ) totals
                WHERE totals.total IN (100, 500, 1000, 2000, 3000, 5000, 10000);
            END;

            CREATE TRIGGER IF NOT EXISTS trg_universal_ids_milestone_notification
            AFTER INSERT ON universal_ids
            WHEN NEW.id IN (100, 500, 1000, 2000, 2500, 3000, 5000, 10000)
            BEGIN
                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, reached_at, historical, viewed_at, viewed_by)
                VALUES (
                    'universal:' || NEW.id,
                    'universal',
                    NEW.id,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    0,
                    NULL,
                    NULL
                );
            END;
            """;
        triggers.ExecuteNonQuery();
    }

    public static void MapEndpoints(RouteGroupBuilder api, string connectionString, string viewer)
    {
        api.MapPost("/milestones/claim", async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var notices = await ClaimPending(connection, viewer);
            return Results.Ok(notices);
        });
    }

    private static void SeedExistingMilestones(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var created = ScalarLong(connection, "SELECT COUNT(*) FROM task_events WHERE event_type = 'Created';");
        var completed = ScalarLong(connection, "SELECT COUNT(*) FROM task_events WHERE event_type = 'Completed';");
        var universal = ScalarLong(connection, "SELECT COALESCE(MAX(id), 0) FROM universal_ids;");

        foreach (var threshold in EventThresholds)
        {
            if (created >= threshold) InsertHistorical(connection, "created", threshold, now);
            if (completed >= threshold) InsertHistorical(connection, "completed", threshold, now);
        }
        foreach (var threshold in UniversalThresholds)
            if (universal >= threshold) InsertHistorical(connection, "universal", threshold, now);

        using var state = connection.CreateCommand();
        state.CommandText = "INSERT INTO milestone_notification_state (id, initialized_at) VALUES (1, $now);";
        state.Parameters.AddWithValue("$now", now);
        state.ExecuteNonQuery();
    }

    private static void InsertHistorical(SqliteConnection connection, string kind, int threshold, string now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO milestone_notifications
                (key, kind, threshold, reached_at, historical, viewed_at, viewed_by)
            VALUES ($key, $kind, $threshold, $now, 1, $now, 'baseline');
            """;
        command.Parameters.AddWithValue("$key", $"{kind}:{threshold}");
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<List<MilestoneNotice>> ClaimPending(SqliteConnection connection, string viewer)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var notices = new List<MilestoneNotice>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE milestone_notifications
            SET viewed_at = $now, viewed_by = $viewer
            WHERE viewed_at IS NULL
            RETURNING kind, threshold, reached_at, historical;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$viewer", viewer);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notices.Add(new MilestoneNotice(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0));
        }
        notices.Sort((a, b) => string.CompareOrdinal(a.ReachedAt, b.ReachedAt));
        return notices;
    }

    private sealed record MilestoneNotice(string Kind, long Threshold, string ReachedAt, bool Historical);
}
