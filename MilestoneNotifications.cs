using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

static class MilestoneNotifications
{
    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        DropTriggers(connection, transaction);
        EnsureNotificationTable(connection, transaction);
        // Existing counts are the baseline: only future inserts can reach new thresholds.
        RebuildCounters(connection, transaction);
        CreateTriggers(connection, transaction);

        transaction.Commit();
    }

    public static void MapEndpoints(RouteGroupBuilder api, string connectionString)
    {
        api.MapPost("/milestones/consume", async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var notices = await ConsumePending(connection);
            return Results.Ok(notices);
        });
    }

    private static void DropTriggers(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, """
            DROP TRIGGER IF EXISTS trg_task_events_milestone_notification;
            DROP TRIGGER IF EXISTS trg_universal_ids_milestone_notification;
            """, transaction);
    }

    private static void EnsureNotificationTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!TableExists(connection, "milestone_notifications", transaction))
        {
            CreateNotificationTable(connection, transaction);
            return;
        }

        if (!ColumnExists(connection, "milestone_notifications", "viewed_at", transaction)) return;

        // Support both the original global-only ledger and the later scoped ledger.
        // Keep genuinely pending notices; acknowledged/baseline rows no longer need storage.
        var scoped = ColumnExists(connection, "milestone_notifications", "scope", transaction);
        var key = scoped ? "key" : "CASE WHEN kind = 'universal' THEN 'universal:' || threshold ELSE 'global:' || kind || ':' || threshold END";
        var scope = scoped ? "scope" : "'global'";
        var scopeValue = ColumnExists(connection, "milestone_notifications", "scope_value", transaction) ? "scope_value" : "NULL";
        var scopeLabel = ColumnExists(connection, "milestone_notifications", "scope_label", transaction) ? "scope_label" : "NULL";

        Execute(connection, "ALTER TABLE milestone_notifications RENAME TO milestone_notifications_legacy;", transaction);
        CreateNotificationTable(connection, transaction);
        Execute(connection, $"""
            INSERT INTO milestone_notifications
                (key, kind, threshold, scope, scope_value, scope_label, reached_at)
            SELECT {key}, kind, threshold, {scope}, {scopeValue}, {scopeLabel}, reached_at
            FROM milestone_notifications_legacy
            WHERE viewed_at IS NULL;

            DROP TABLE milestone_notifications_legacy;
            """, transaction);
    }

    private static void CreateNotificationTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, """
            CREATE TABLE milestone_notifications (
                key TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                threshold INTEGER NOT NULL,
                scope TEXT NOT NULL CHECK(scope IN ('global', 'list', 'year')),
                scope_value TEXT NULL,
                scope_label TEXT NULL,
                reached_at TEXT NOT NULL
            );
            """, transaction);
    }

    private static void RebuildCounters(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, """
            DROP TABLE IF EXISTS milestone_notification_state;
            DROP TABLE IF EXISTS milestone_notification_counters;
            CREATE TABLE milestone_notification_counters (
                scope_key TEXT PRIMARY KEY,
                total INTEGER NOT NULL
            );

            INSERT INTO milestone_notification_counters (scope_key, total)
            SELECT 'global:' || lower(event_type), COUNT(*)
            FROM task_events
            GROUP BY lower(event_type);

            INSERT INTO milestone_notification_counters (scope_key, total)
            SELECT 'global:recorded', COUNT(*)
            FROM task_events;

            INSERT INTO milestone_notification_counters (scope_key, total)
            SELECT 'list:' || list_id || ':' || lower(event_type), COUNT(*)
            FROM task_events
            WHERE event_type IN ('Created', 'Completed')
            GROUP BY list_id, lower(event_type);

            INSERT INTO milestone_notification_counters (scope_key, total)
            SELECT 'year:' || substr(event_at, 1, 4) || ':' || lower(event_type), COUNT(*)
            FROM task_events
            WHERE event_type IN ('Created', 'Completed')
              AND substr(event_at, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
              AND substr(event_at, 5, 1) = '-'
            GROUP BY substr(event_at, 1, 4), lower(event_type);
            """, transaction);
    }

    private static void CreateTriggers(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var triggers = connection.CreateCommand();
        triggers.Transaction = transaction;
        triggers.CommandText = """
            CREATE TRIGGER trg_task_events_milestone_notification
            AFTER INSERT ON task_events
            BEGIN
                INSERT INTO milestone_notification_counters (scope_key, total)
                VALUES ('global:' || lower(NEW.event_type), 1)
                ON CONFLICT(scope_key) DO UPDATE SET total = total + 1;

                INSERT INTO milestone_notification_counters (scope_key, total)
                VALUES ('global:recorded', 1)
                ON CONFLICT(scope_key) DO UPDATE SET total = total + 1;

                INSERT INTO milestone_notification_counters (scope_key, total)
                SELECT 'list:' || NEW.list_id || ':' || lower(NEW.event_type), 1
                WHERE NEW.event_type IN ('Created', 'Completed')
                ON CONFLICT(scope_key) DO UPDATE SET total = total + 1;

                INSERT INTO milestone_notification_counters (scope_key, total)
                SELECT 'year:' || substr(NEW.event_at, 1, 4) || ':' || lower(NEW.event_type), 1
                WHERE NEW.event_type IN ('Created', 'Completed')
                  AND substr(NEW.event_at, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
                  AND substr(NEW.event_at, 5, 1) = '-'
                ON CONFLICT(scope_key) DO UPDATE SET total = total + 1;

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at)
                SELECT
                    'global:' || lower(NEW.event_type) || ':' || counters.total,
                    lower(NEW.event_type),
                    counters.total,
                    'global',
                    NULL,
                    NULL,
                    CASE
                        WHEN NEW.event_at IS NULL OR lower(NEW.event_at) = 'unknown'
                            THEN strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                        ELSE NEW.event_at
                    END
                FROM milestone_notification_counters counters
                WHERE counters.scope_key = 'global:' || lower(NEW.event_type)
                  AND (
                    counters.total IN (1, 100, 500, 1000, 2000, 3000, 5000, 10000)
                    OR (counters.total > 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at)
                SELECT
                    'global:recorded:' || counters.total,
                    'recorded',
                    counters.total,
                    'global',
                    NULL,
                    NULL,
                    CASE
                        WHEN NEW.event_at IS NULL OR lower(NEW.event_at) = 'unknown'
                            THEN strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                        ELSE NEW.event_at
                    END
                FROM milestone_notification_counters counters
                WHERE counters.scope_key = 'global:recorded'
                  AND (
                    counters.total IN (1, 100, 500, 1000, 2000, 3000, 5000, 10000)
                    OR (counters.total > 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at)
                SELECT
                    'list:' || NEW.list_id || ':' || lower(NEW.event_type) || ':' || counters.total,
                    lower(NEW.event_type),
                    counters.total,
                    'list',
                    CAST(NEW.list_id AS TEXT),
                    COALESCE((SELECT name FROM lists WHERE id = NEW.list_id), 'List ' || NEW.list_id),
                    CASE
                        WHEN NEW.event_at IS NULL OR lower(NEW.event_at) = 'unknown'
                            THEN strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                        ELSE NEW.event_at
                    END
                FROM milestone_notification_counters counters
                WHERE NEW.event_type IN ('Created', 'Completed')
                  AND counters.scope_key = 'list:' || NEW.list_id || ':' || lower(NEW.event_type)
                  AND (
                    counters.total IN (100, 500, 1000, 2000, 5000)
                    OR (counters.total >= 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at)
                SELECT
                    'year:' || substr(NEW.event_at, 1, 4) || ':' || lower(NEW.event_type) || ':' || counters.total,
                    lower(NEW.event_type),
                    counters.total,
                    'year',
                    substr(NEW.event_at, 1, 4),
                    NULL,
                    NEW.event_at
                FROM milestone_notification_counters counters
                WHERE NEW.event_type IN ('Created', 'Completed')
                  AND substr(NEW.event_at, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
                  AND substr(NEW.event_at, 5, 1) = '-'
                  AND counters.scope_key = 'year:' || substr(NEW.event_at, 1, 4) || ':' || lower(NEW.event_type)
                  AND (
                    counters.total IN (1, 100, 500, 1000, 2000)
                    OR (counters.total >= 5000 AND counters.total % 5000 = 0)
                  );
            END;

            CREATE TRIGGER trg_universal_ids_milestone_notification
            AFTER INSERT ON universal_ids
            WHEN NEW.id IN (1, 100, 500, 1000, 2000, 2500, 3000, 5000, 10000)
              OR (NEW.id > 10000 AND NEW.id % 5000 = 0)
            BEGIN
                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at)
                VALUES (
                    'universal:' || NEW.id,
                    'universal',
                    NEW.id,
                    'global',
                    NULL,
                    NULL,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                );
            END;
            """;
        triggers.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<List<MilestoneNotice>> ConsumePending(SqliteConnection connection)
    {
        var notices = new List<MilestoneNotice>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM milestone_notifications
            RETURNING kind, threshold, scope, scope_value, scope_label, reached_at;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notices.Add(new MilestoneNotice(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }
        notices.Sort((a, b) => string.CompareOrdinal(a.ReachedAt, b.ReachedAt));
        return notices;
    }

    private sealed record MilestoneNotice(
        string Kind,
        long Threshold,
        string Scope,
        string? ScopeValue,
        string? ScopeLabel,
        string ReachedAt);
}
