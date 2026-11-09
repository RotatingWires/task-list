using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>
/// The database: schema and migrations, connections, SQL helpers, and the list and task queries the endpoints in
/// Program.cs use, like SpendTracker's and NewsReview's Store.cs. Program.cs imports these with `using static Store;`.
/// </summary>
static class Store
{
    // Enforced here as well as by the page's maxlength, like SpendTracker.
    internal const int MaxListNameLength = 100, MaxListDescriptionLength = 1000, MaxTaskTitleLength = 500, MaxTaskDescriptionLength = 4000;

    internal static string? TooLong(string label, string value, int maxLength) =>
        value.Length > maxLength ? $"{label} must be {maxLength} characters or fewer." : null;

    internal static void InitializeDatabase(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        // PRAGMA user_version numbers the schema, like NewsReview and SpendTracker. Version 1 is everything up to v1.7.23,
        // brought up to date by the checks below; later changes add numbered steps after them.
        int schemaVersion;
        using (var versionCommand = Sql(connection, "PRAGMA user_version;"))
            schemaVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion > DatabaseSchema.Version)
            throw new InvalidOperationException($"tasklist.db uses schema version {schemaVersion}, but this TaskList build only understands version {DatabaseSchema.Version}. Update TaskList before opening this database.");
        using (var command = Sql(connection, """
            CREATE TABLE IF NOT EXISTS lists (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                description TEXT NOT NULL DEFAULT '',
                next_task_number INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS universal_ids (id INTEGER PRIMARY KEY AUTOINCREMENT);
            CREATE TABLE IF NOT EXISTS task_history_state (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                revision TEXT NOT NULL
            );
            INSERT OR IGNORE INTO task_history_state (id, revision) VALUES (1, lower(hex(randomblob(16))));
            CREATE TABLE IF NOT EXISTS deleted_lists (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS trg_lists_preserve_deleted_name
            BEFORE DELETE ON lists
            BEGIN
                INSERT OR REPLACE INTO deleted_lists (id, name) VALUES (OLD.id, OLD.name);
            END;
            CREATE TABLE IF NOT EXISTS items (
                universal_id INTEGER NOT NULL UNIQUE,
                list_id INTEGER NOT NULL,
                display_id TEXT NOT NULL,
                parent_display_id TEXT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'Open' CHECK(status IN ('Open', 'Done', 'Cancelled')),
                created_at TEXT NOT NULL,
                updated_at TEXT NULL,
                completed_at TEXT NULL,
                cancelled_at TEXT NULL,
                reopened_at TEXT NULL,
                next_child_number INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (list_id, display_id),
                FOREIGN KEY (list_id) REFERENCES lists(id) ON DELETE CASCADE,
                FOREIGN KEY (list_id, parent_display_id) REFERENCES items(list_id, display_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_items_list_parent ON items(list_id, parent_display_id, display_id);

            CREATE TABLE IF NOT EXISTS task_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                universal_id INTEGER NOT NULL,
                event_type TEXT NOT NULL CHECK(event_type IN ('Created', 'Completed', 'Cancelled', 'Reopened', 'Deleted')),
                event_at TEXT NOT NULL,
                from_status TEXT NULL,
                to_status TEXT NULL,
                list_id INTEGER NOT NULL,
                display_id TEXT NOT NULL,
                parent_display_id TEXT NULL,
                title TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'live' CHECK(source IN ('live', 'legacy'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_task_events_unique ON task_events(universal_id, event_type, event_at) WHERE event_type = 'Created';
            CREATE INDEX IF NOT EXISTS idx_task_events_time ON task_events(event_at, id);
            CREATE INDEX IF NOT EXISTS idx_task_events_uid ON task_events(universal_id, event_at, id);
            CREATE INDEX IF NOT EXISTS idx_task_events_list ON task_events(list_id, event_at, id);

            """)) command.ExecuteNonQuery();

        EnsureTaskEventSchema(connection);

        var hasArchivedColumn = false;
        using (var columnCommand = Sql(connection, "PRAGMA table_info(lists);"))
        using (var reader = columnCommand.ExecuteReader())
        {
            while (reader.Read()) if (string.Equals(reader.GetString(1), "archived", StringComparison.OrdinalIgnoreCase)) { hasArchivedColumn = true; break; }
        }
        if (!hasArchivedColumn)
        {
            using var addArchived = Sql(connection, "ALTER TABLE lists ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;");
            addArchived.ExecuteNonQuery();
        }

        TaskEventBackfill.Run(connection, null);

        if (schemaVersion < DatabaseSchema.Version)
        {
            using var stamp = Sql(connection, $"PRAGMA user_version = {DatabaseSchema.Version};");
            stamp.ExecuteNonQuery();
        }

        using var ensureDefault = Sql(connection, """
            INSERT INTO lists (name, description, next_task_number, created_at)
            SELECT 'Tasks', '', 1, $createdAt WHERE NOT EXISTS (SELECT 1 FROM lists);
            """, ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));
        ensureDefault.ExecuteNonQuery();
    }

    internal static void EnsureTaskEventSchema(SqliteConnection connection)
    {
        string tableSql;
        using (var command = Sql(connection, "SELECT COALESCE(sql, '') FROM sqlite_master WHERE type = 'table' AND name = 'task_events';"))
            tableSql = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;

        if (!tableSql.Contains("'Deleted'", StringComparison.Ordinal))
        {
            using var migrate = Sql(connection, """
                DROP TRIGGER IF EXISTS trg_items_event_insert;
                DROP TRIGGER IF EXISTS trg_items_event_status;
                DROP TRIGGER IF EXISTS trg_task_events_milestone_notification;

                ALTER TABLE task_events RENAME TO task_events_before_deleted;

                CREATE TABLE task_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    universal_id INTEGER NOT NULL,
                    event_type TEXT NOT NULL CHECK(event_type IN ('Created', 'Completed', 'Cancelled', 'Reopened', 'Deleted')),
                    event_at TEXT NOT NULL,
                    from_status TEXT NULL,
                    to_status TEXT NULL,
                    list_id INTEGER NOT NULL,
                    display_id TEXT NOT NULL,
                    parent_display_id TEXT NULL,
                    title TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'live' CHECK(source IN ('live', 'legacy'))
                );

                INSERT INTO task_events
                    (id, universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                SELECT id, universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source
                FROM task_events_before_deleted
                ORDER BY id;

                DROP TABLE task_events_before_deleted;

                CREATE UNIQUE INDEX idx_task_events_unique ON task_events(universal_id, event_type, event_at) WHERE event_type = 'Created';
                CREATE INDEX idx_task_events_time ON task_events(event_at, id);
                CREATE INDEX idx_task_events_uid ON task_events(universal_id, event_at, id);
                CREATE INDEX idx_task_events_list ON task_events(list_id, event_at, id);
                """);
            migrate.ExecuteNonQuery();
        }

        using var triggers = Sql(connection, """
            DROP INDEX IF EXISTS idx_task_events_unique;
            CREATE UNIQUE INDEX idx_task_events_unique ON task_events(universal_id, event_type, event_at)
                WHERE event_type = 'Created';

            DROP TRIGGER IF EXISTS trg_items_event_insert;
            DROP TRIGGER IF EXISTS trg_items_event_status;

            CREATE TRIGGER trg_items_event_insert
            AFTER INSERT ON items
            BEGIN
                INSERT OR IGNORE INTO task_events
                    (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                VALUES
                    (NEW.universal_id, 'Created', NEW.created_at, NULL, 'Open', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                     CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END);

                INSERT OR IGNORE INTO task_events
                    (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                SELECT NEW.universal_id, 'Completed', NEW.completed_at, 'Open', 'Done', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                       CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
                WHERE NEW.status = 'Done' AND NEW.completed_at IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.universal_id = NEW.universal_id
                                  AND e.event_type = 'Completed' AND e.event_at = NEW.completed_at);

                INSERT OR IGNORE INTO task_events
                    (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                SELECT NEW.universal_id, 'Cancelled', NEW.cancelled_at, 'Open', 'Cancelled', NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title,
                       CASE WHEN NEW.updated_at = 'Unknown' THEN 'legacy' ELSE 'live' END
                WHERE NEW.status = 'Cancelled' AND NEW.cancelled_at IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.universal_id = NEW.universal_id
                                  AND e.event_type = 'Cancelled' AND e.event_at = NEW.cancelled_at);
            END;

            CREATE TRIGGER trg_items_event_status
            AFTER UPDATE OF status ON items
            WHEN OLD.status <> NEW.status
            BEGIN
                INSERT INTO task_events
                    (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                VALUES
                    (NEW.universal_id,
                     CASE NEW.status WHEN 'Done' THEN 'Completed' WHEN 'Cancelled' THEN 'Cancelled' ELSE 'Reopened' END,
                     CASE NEW.status WHEN 'Done' THEN COALESCE(NEW.completed_at, NEW.updated_at)
                                     WHEN 'Cancelled' THEN COALESCE(NEW.cancelled_at, NEW.updated_at)
                                     ELSE COALESCE(NEW.reopened_at, NEW.updated_at) END,
                     OLD.status, NEW.status, NEW.list_id, NEW.display_id, NEW.parent_display_id, NEW.title, 'live');
            END;
            """);
        triggers.ExecuteNonQuery();
    }

    internal static SqliteConnection OpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    internal static string ConfigureDatabaseJournal(string connectionString, string databasePath)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);

        if (!CanUseWal(databasePath))
        {
            using var current = Sql(connection, "PRAGMA journal_mode;");
            return $"{current.ExecuteScalar()?.ToString() ?? "unknown"} (WAL skipped: network/unknown drive)";
        }

        using var wal = Sql(connection, "PRAGMA journal_mode=WAL;");
        return wal.ExecuteScalar()?.ToString() ?? "unknown";
    }

    internal static bool CanUseWal(string databasePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(databasePath);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
                return false;

            if (!OperatingSystem.IsWindows()) return true;

            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType is not DriveType.Network and not DriveType.Unknown;
        }
        catch
        {
            return false;
        }
    }

    internal static SqliteCommand ConnectionPragmaCommand(SqliteConnection connection) =>
        Sql(connection, "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");

    internal static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        using var command = ConnectionPragmaCommand(connection);
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand Sql(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.CommandText = text; AddParameters(command, parameters); return command; }
    internal static SqliteCommand SqlTx(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = text; AddParameters(command, parameters); return command; }
    internal static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters) { foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
    internal static object? Scalar(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { using var command = Sql(connection, text, parameters); return command.ExecuteScalar(); }
    internal static object? ScalarTx(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { using var command = SqlTx(connection, transaction, text, parameters); return command.ExecuteScalar(); }
    internal static long ScalarLong(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) => Convert.ToInt64(Scalar(connection, text, parameters));
    internal static int Execute(SqliteConnection connection, string text, params (string Name, object? Value)[] parameters) { using var command = Sql(connection, text, parameters); return command.ExecuteNonQuery(); }
    internal static int ExecuteTx(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Name, object? Value)[] parameters) { using var command = SqlTx(connection, transaction, text, parameters); return command.ExecuteNonQuery(); }
    internal static long AllocateUniversalId(SqliteConnection connection, SqliteTransaction transaction) => Convert.ToInt64(ScalarTx(connection, transaction, "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();"));

    internal static TaskListInfo? GetList(SqliteConnection connection, long id)
    {
        using var command = Sql(connection, """
            SELECT l.id, l.name, l.description, l.created_at,
                   (SELECT COUNT(*) FROM items i WHERE i.list_id = l.id AND i.parent_display_id IS NULL) AS task_count,
                   l.archived FROM lists l WHERE l.id = $id;
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadList(reader) : null;
    }
    internal static TaskListInfo ReadList(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5) != 0);
    internal static bool ListNameExists(SqliteConnection connection, string name, long? excludingId)
    {
        var sql = excludingId is null ? "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE);" : "SELECT EXISTS(SELECT 1 FROM lists WHERE name = $name COLLATE NOCASE AND id <> $id);";
        var result = excludingId is null ? ScalarLong(connection, sql, ("$name", name)) : ScalarLong(connection, sql, ("$name", name), ("$id", excludingId.Value));
        return result != 0;
    }

    internal static void ApplyItemUpdate(SqliteConnection connection, long listId, string displayId, UpdateValues values)
    {
        Execute(connection, """
            UPDATE items SET title = $title, description = $description, status = $status,
                updated_at = $updatedAt, completed_at = $completedAt,
                cancelled_at = $cancelledAt, reopened_at = $reopenedAt
            WHERE list_id = $listId AND display_id = $displayId;
            """,
            ("$title", values.Title), ("$description", values.Description), ("$status", values.Status), ("$updatedAt", values.UpdatedAt), ("$completedAt", values.CompletedAt), ("$cancelledAt", values.CancelledAt), ("$reopenedAt", values.ReopenedAt), ("$listId", listId), ("$displayId", displayId));
    }
    internal static UpdateValues BuildUpdatedValues(string oldTitle, string oldDescription, string oldStatus, string? oldUpdatedAt, string? oldCompletedAt, string? oldCancelledAt, string? oldReopenedAt, UpdateTaskRequest request)
    {
        var title = request.Title is null ? oldTitle : request.Title.Trim();
        if (string.IsNullOrWhiteSpace(title)) return UpdateValues.WithError("Task title cannot be empty.");
        var description = request.Description is null ? oldDescription : request.Description.Trim();
        // Only values being changed are checked, so older long text never blocks a status change.
        if ((TooLong("Task title", request.Title is null ? "" : title, MaxTaskTitleLength)
            ?? TooLong("Description", request.Description is null ? "" : description, MaxTaskDescriptionLength)) is { } lengthError)
            return UpdateValues.WithError(lengthError);
        var status = request.Status is null ? oldStatus : NormalizeStatus(request.Status);
        if (status is null) return UpdateValues.WithError("Status must be Open, Done, or Cancelled.");
        if (title == oldTitle && description == oldDescription && status == oldStatus) return new(title, description, status, oldUpdatedAt, oldCompletedAt, oldCancelledAt, oldReopenedAt, null);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var completedAt = oldCompletedAt; var cancelledAt = oldCancelledAt; var reopenedAt = oldReopenedAt;
        if (status != oldStatus)
        {
            if (status == "Done") completedAt = now;
            else if (status == "Cancelled") cancelledAt = now;
            else reopenedAt = now;
        }
        return new(title, description, status, now, completedAt, cancelledAt, reopenedAt, null);
    }
    internal static string? NormalizeStatus(string status) => status.ToLowerInvariant() switch { "open" => "Open", "done" => "Done", "cancelled" => "Cancelled", _ => null };
    internal static int IndentWidth(string indent) => indent.Sum(ch => ch == '\t' ? 4 : 1);
    internal static TaskItem ReadItem(SqliteDataReader reader)
    {
        var parentDisplayId = NullableString(reader, 3);
        return new TaskItem(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), parentDisplayId, parentDisplayId is not null, reader.GetString(4), reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.GetString(6), reader.GetString(7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), []);
    }
    internal static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    internal static TaskItem? GetItem(SqliteConnection connection, long listId, string displayId)
    {
        using var command = Sql(connection, """
            SELECT universal_id, list_id, display_id, parent_display_id, title, description, status,
                   created_at, updated_at, completed_at, cancelled_at, reopened_at
            FROM items WHERE list_id = $listId AND display_id = $displayId;
            """, ("$listId", listId), ("$displayId", displayId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadItem(reader) : null;
    }
    internal static string RemapMovedDisplayId(string oldRootDisplayId, string newRootDisplayId, string oldDisplayId) => oldDisplayId == oldRootDisplayId ? newRootDisplayId : $"{newRootDisplayId}.{oldDisplayId[(oldRootDisplayId.Length + 1)..]}";
    internal static long DisplaySegmentNumber(string displayId) { var separator = displayId.LastIndexOf('.'); var segment = separator < 0 ? displayId : displayId[(separator + 1)..]; return long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0; }
    internal static bool IsValidDisplayId(string displayId) => Regex.IsMatch(displayId, @"^\d+(?:\.\d+)*$", RegexOptions.CultureInvariant);
    internal static void SortChildren(TaskItem item) { item.Subtasks.Sort((a, b) => DisplaySegmentNumber(a.DisplayId).CompareTo(DisplaySegmentNumber(b.DisplayId))); foreach (var child in item.Subtasks) SortChildren(child); }

    internal static void InsertImportedItem(SqliteConnection connection, SqliteTransaction transaction, long listId, string displayId, string? parentDisplayId, long universalId, ImportNode node)
    {
        ExecuteTx(connection, transaction, """
            INSERT INTO items
                (universal_id, list_id, display_id, parent_display_id, title, description, status,
                 created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number)
            VALUES
                ($uid, $listId, $displayId, $parentDisplayId, $title, '', $status,
                 $createdAt, 'Unknown', $completedAt, NULL, NULL, $nextChildNumber);
            """,
            ("$uid", universalId), ("$listId", listId), ("$displayId", displayId), ("$parentDisplayId", parentDisplayId), ("$title", node.Item.Title), ("$status", node.Item.Status), ("$createdAt", node.Item.CreatedAt), ("$completedAt", node.Item.CompletedAt), ("$nextChildNumber", node.Children.Count + 1));
    }
    internal static int InsertImportedChildren(SqliteConnection connection, SqliteTransaction transaction, long listId, string parentDisplayId, List<ImportNode> children)
    {
        var inserted = 0;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var childNumber = index + 1;
            var displayId = $"{parentDisplayId}.{childNumber.ToString(CultureInfo.InvariantCulture)}";
            var universalId = AllocateUniversalId(connection, transaction);
            InsertImportedItem(connection, transaction, listId, displayId, parentDisplayId, universalId, child);
            inserted++;
            inserted += InsertImportedChildren(connection, transaction, listId, displayId, child.Children);
        }
        return inserted;
    }
}

static class DatabaseSchema
{
    public const int Version = 1;
}
