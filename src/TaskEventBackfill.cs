using Microsoft.Data.Sqlite;

/// <summary>
/// Records legacy Created/Completed/Cancelled/Reopened events for current items that have none. Startup runs it so
/// pre-v1.5 databases gain history; Fresh start runs it after clearing history so a restart adds nothing new.
/// </summary>
static class TaskEventBackfill
{
    public static void Run(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT universal_id, 'Created', created_at, NULL, 'Open', list_id, display_id, parent_display_id, title, 'legacy'
            FROM items;

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT universal_id, 'Completed', completed_at, NULL, 'Done', list_id, display_id, parent_display_id, title, 'legacy'
            FROM items WHERE completed_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.universal_id = items.universal_id
                              AND e.event_type = 'Completed' AND e.event_at = items.completed_at);

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT universal_id, 'Cancelled', cancelled_at, NULL, 'Cancelled', list_id, display_id, parent_display_id, title, 'legacy'
            FROM items WHERE cancelled_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.universal_id = items.universal_id
                              AND e.event_type = 'Cancelled' AND e.event_at = items.cancelled_at);

            INSERT OR IGNORE INTO task_events
                (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
            SELECT universal_id, 'Reopened', reopened_at, NULL, 'Open', list_id, display_id, parent_display_id, title, 'legacy'
            FROM items WHERE reopened_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM task_events e WHERE e.universal_id = items.universal_id
                              AND e.event_type = 'Reopened' AND e.event_at = items.reopened_at);
            """;
        command.ExecuteNonQuery();
    }
}
