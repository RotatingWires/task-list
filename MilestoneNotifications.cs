using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

static class MilestoneNotifications
{
    private const int SchemaVersion = 2;

    private static readonly long[] GlobalEventBaseThresholds = [1, 100, 500, 1000, 2000, 3000, 5000, 10000];
    private static readonly long[] UniversalBaseThresholds = [1, 100, 500, 1000, 2000, 2500, 3000, 5000, 10000];
    private static readonly long[] ListBaseThresholds = [100, 500, 1000, 2000, 5000];
    private static readonly long[] YearBaseThresholds = [1, 100, 500, 1000, 2000];

    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        DropTriggers(connection);
        EnsureNotificationTable(connection);
        EnsureStateTable(connection);
        RebuildCounters(connection);

        if (GetStateVersion(connection) < SchemaVersion)
        {
            BaselineExistingMilestones(connection);
            SetStateVersion(connection, SchemaVersion);
        }

        CreateTriggers(connection);
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

    private static void DropTriggers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS trg_task_events_milestone_notification;
            DROP TRIGGER IF EXISTS trg_universal_ids_milestone_notification;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureNotificationTable(SqliteConnection connection)
    {
        if (!TableExists(connection, "milestone_notifications"))
        {
            CreateNotificationTable(connection);
            return;
        }

        if (!ColumnExists(connection, "milestone_notifications", "scope"))
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                ALTER TABLE milestone_notifications RENAME TO milestone_notifications_legacy;

                CREATE TABLE milestone_notifications (
                    key TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    threshold INTEGER NOT NULL,
                    scope TEXT NOT NULL CHECK(scope IN ('global', 'list', 'year')),
                    scope_value TEXT NULL,
                    scope_label TEXT NULL,
                    reached_at TEXT NOT NULL,
                    historical INTEGER NOT NULL DEFAULT 0,
                    viewed_at TEXT NULL,
                    viewed_by TEXT NULL
                );

                INSERT INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
                SELECT
                    CASE
                        WHEN kind = 'universal' THEN 'universal:' || threshold
                        ELSE 'global:' || kind || ':' || threshold
                    END,
                    kind,
                    threshold,
                    'global',
                    NULL,
                    NULL,
                    reached_at,
                    historical,
                    viewed_at,
                    viewed_by
                FROM milestone_notifications_legacy;

                DROP TABLE milestone_notifications_legacy;

                CREATE INDEX IF NOT EXISTS idx_milestone_notifications_pending
                    ON milestone_notifications(viewed_at, reached_at);
                """;
            migrate.ExecuteNonQuery();
            return;
        }

        if (!ColumnExists(connection, "milestone_notifications", "scope_value"))
            Execute(connection, "ALTER TABLE milestone_notifications ADD COLUMN scope_value TEXT NULL;");
        if (!ColumnExists(connection, "milestone_notifications", "scope_label"))
            Execute(connection, "ALTER TABLE milestone_notifications ADD COLUMN scope_label TEXT NULL;");

        Execute(connection, """
            CREATE INDEX IF NOT EXISTS idx_milestone_notifications_pending
                ON milestone_notifications(viewed_at, reached_at);
            """);
    }

    private static void CreateNotificationTable(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE milestone_notifications (
                key TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                threshold INTEGER NOT NULL,
                scope TEXT NOT NULL CHECK(scope IN ('global', 'list', 'year')),
                scope_value TEXT NULL,
                scope_label TEXT NULL,
                reached_at TEXT NOT NULL,
                historical INTEGER NOT NULL DEFAULT 0,
                viewed_at TEXT NULL,
                viewed_by TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_milestone_notifications_pending
                ON milestone_notifications(viewed_at, reached_at);
            """);
    }

    private static void EnsureStateTable(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS milestone_notification_state (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                initialized_at TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 2
            );
            """);

        if (!ColumnExists(connection, "milestone_notification_state", "version"))
            Execute(connection, "ALTER TABLE milestone_notification_state ADD COLUMN version INTEGER NOT NULL DEFAULT 1;");

        Execute(connection, """
            DROP TABLE IF EXISTS milestone_notification_counters;
            CREATE TABLE milestone_notification_counters (
                scope_key TEXT PRIMARY KEY,
                total INTEGER NOT NULL
            );
            """);
    }

    private static void RebuildCounters(SqliteConnection connection)
    {
        Execute(connection, """
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
            """);
    }

    private static int GetStateVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT version FROM milestone_notification_state WHERE id = 1), 0);";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetStateVersion(SqliteConnection connection, int version)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO milestone_notification_state (id, initialized_at, version)
            VALUES (1, $now, $version)
            ON CONFLICT(id) DO UPDATE SET version = excluded.version;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    private static void BaselineExistingMilestones(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var (kind, total) in QueryKindCounts(connection, """
            SELECT lower(event_type), COUNT(*)
            FROM task_events
            GROUP BY lower(event_type);
            """))
        {
            foreach (var threshold in ContinuingThresholds(GlobalEventBaseThresholds, total, 15000))
                InsertHistorical(connection, kind, threshold, "global", null, null, now);
        }

        var recorded = ScalarLong(connection, "SELECT COUNT(*) FROM task_events;");
        foreach (var threshold in ContinuingThresholds(GlobalEventBaseThresholds, recorded, 15000))
            InsertHistorical(connection, "recorded", threshold, "global", null, null, now);

        var highestUniversalId = ScalarLong(connection, "SELECT COALESCE(MAX(id), 0) FROM universal_ids;");
        foreach (var threshold in ContinuingThresholds(UniversalBaseThresholds, highestUniversalId, 15000))
            InsertHistorical(connection, "universal", threshold, "global", null, null, now);

        foreach (var row in QueryListCounts(connection))
        {
            foreach (var threshold in ContinuingThresholds(ListBaseThresholds, row.Total, 10000))
                InsertHistorical(connection, row.Kind, threshold, "list", row.ListId.ToString(), row.ListName, now);
        }

        foreach (var row in QueryYearCounts(connection))
        {
            foreach (var threshold in ContinuingThresholds(YearBaseThresholds, row.Total, 5000))
                InsertHistorical(connection, row.Kind, threshold, "year", row.Year, null, now);
        }
    }

    private static IEnumerable<long> ContinuingThresholds(long[] baseThresholds, long maximum, long continuationStart)
    {
        foreach (var threshold in baseThresholds)
            if (threshold <= maximum)
                yield return threshold;

        for (var threshold = continuationStart; threshold <= maximum; threshold += 5000)
            yield return threshold;
    }

    private static List<(string Kind, long Total)> QueryKindCounts(SqliteConnection connection, string sql)
    {
        var rows = new List<(string Kind, long Total)>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }

    private static List<(string Kind, long ListId, string ListName, long Total)> QueryListCounts(SqliteConnection connection)
    {
        var rows = new List<(string Kind, long ListId, string ListName, long Total)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lower(e.event_type), e.list_id, COALESCE(l.name, 'List ' || e.list_id), COUNT(*)
            FROM task_events e
            LEFT JOIN lists l ON l.id = e.list_id
            WHERE e.event_type IN ('Created', 'Completed')
            GROUP BY lower(e.event_type), e.list_id, l.name;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)));
        return rows;
    }

    private static List<(string Kind, string Year, long Total)> QueryYearCounts(SqliteConnection connection)
    {
        var rows = new List<(string Kind, string Year, long Total)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lower(event_type), substr(event_at, 1, 4), COUNT(*)
            FROM task_events
            WHERE event_type IN ('Created', 'Completed')
              AND substr(event_at, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
              AND substr(event_at, 5, 1) = '-'
            GROUP BY lower(event_type), substr(event_at, 1, 4);
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return rows;
    }

    private static void InsertHistorical(
        SqliteConnection connection,
        string kind,
        long threshold,
        string scope,
        string? scopeValue,
        string? scopeLabel,
        string now)
    {
        var key = NotificationKey(kind, threshold, scope, scopeValue);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO milestone_notifications
                (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
            VALUES ($key, $kind, $threshold, $scope, $scopeValue, $scopeLabel, $now, 1, $now, 'baseline');
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$scopeValue", (object?)scopeValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$scopeLabel", (object?)scopeLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static string NotificationKey(string kind, long threshold, string scope, string? scopeValue)
    {
        if (kind == "universal") return $"universal:{threshold}";
        return scope switch
        {
            "list" => $"list:{scopeValue}:{kind}:{threshold}",
            "year" => $"year:{scopeValue}:{kind}:{threshold}",
            _ => $"global:{kind}:{threshold}"
        };
    }

    private static void CreateTriggers(SqliteConnection connection)
    {
        using var triggers = connection.CreateCommand();
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
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
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
                    END,
                    0,
                    NULL,
                    NULL
                FROM milestone_notification_counters counters
                WHERE counters.scope_key = 'global:' || lower(NEW.event_type)
                  AND (
                    counters.total IN (1, 100, 500, 1000, 2000, 3000, 5000, 10000)
                    OR (counters.total > 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
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
                    END,
                    0,
                    NULL,
                    NULL
                FROM milestone_notification_counters counters
                WHERE counters.scope_key = 'global:recorded'
                  AND (
                    counters.total IN (1, 100, 500, 1000, 2000, 3000, 5000, 10000)
                    OR (counters.total > 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
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
                    END,
                    0,
                    NULL,
                    NULL
                FROM milestone_notification_counters counters
                WHERE NEW.event_type IN ('Created', 'Completed')
                  AND counters.scope_key = 'list:' || NEW.list_id || ':' || lower(NEW.event_type)
                  AND (
                    counters.total IN (100, 500, 1000, 2000, 5000)
                    OR (counters.total >= 10000 AND counters.total % 5000 = 0)
                  );

                INSERT OR IGNORE INTO milestone_notifications
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
                SELECT
                    'year:' || substr(NEW.event_at, 1, 4) || ':' || lower(NEW.event_type) || ':' || counters.total,
                    lower(NEW.event_type),
                    counters.total,
                    'year',
                    substr(NEW.event_at, 1, 4),
                    NULL,
                    NEW.event_at,
                    0,
                    NULL,
                    NULL
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
                    (key, kind, threshold, scope, scope_value, scope_label, reached_at, historical, viewed_at, viewed_by)
                VALUES (
                    'universal:' || NEW.id,
                    'universal',
                    NEW.id,
                    'global',
                    NULL,
                    NULL,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    0,
                    NULL,
                    NULL
                );
            END;
            """;
        triggers.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
            RETURNING kind, threshold, scope, scope_value, scope_label, reached_at, historical;
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
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6) != 0));
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
        string ReachedAt,
        bool Historical);
}
