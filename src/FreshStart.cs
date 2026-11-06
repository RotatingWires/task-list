using Microsoft.Data.Sqlite;

/// <summary>
/// Fresh start clears only the categories the user selects, like NewsReview's selective reset, and then compacts the
/// database so the freed space goes back to the disk. The Universal-ID ledger and ID counters are kept, so later tasks
/// never reuse an ID. Clearing history also restarts milestone progress, because milestone counts follow recorded history.
/// </summary>
static class FreshStart
{
    private const string Confirmation = "RESET SELECTED";
    private static readonly string[] Options = ["finishedTasks", "archivedLists", "everything", "deletedTaskHistory", "allHistory", "recoveryBackups"];

    public static void MapEndpoints(RouteGroupBuilder api, string connectionString, string dataDir, ILogger logger)
    {
        api.MapGet("/fresh-start", () => Results.Ok(ReadSummary(connectionString, dataDir)));
        api.MapPost("/fresh-start", (FreshStartRequest request) => Reset(request, connectionString, dataDir, logger));
    }

    private static FreshStartSummary ReadSummary(string connectionString, string dataDir)
    {
        using var connection = Open(connectionString);
        using var command = Command(connection, """
            SELECT
                (SELECT COUNT(*) FROM items i WHERE i.status IN ('Done', 'Cancelled')
                    AND NOT EXISTS (SELECT 1 FROM items d WHERE d.list_id = i.list_id
                                    AND d.display_id LIKE i.display_id || '.%' AND d.status = 'Open')),
                (SELECT COUNT(*) FROM lists WHERE archived = 1),
                (SELECT COUNT(*) FROM items WHERE list_id IN (SELECT id FROM lists WHERE archived = 1)),
                (SELECT COUNT(*) FROM lists),
                (SELECT COUNT(*) FROM items),
                (SELECT COUNT(*) FROM task_events WHERE universal_id NOT IN (SELECT universal_id FROM items)),
                (SELECT COUNT(*) FROM task_events);
            """);
        using var reader = command.ExecuteReader();
        reader.Read();
        var backups = RecoveryBackups(dataDir);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), backups.Length, backups.Sum(file => file.Length), DatabaseBytes(connectionString));
    }

    private static IResult Reset(FreshStartRequest request, string connectionString, string dataDir, ILogger logger)
    {
        if (request.Confirmation != Confirmation) return Results.BadRequest(new { error = $"Type {Confirmation} to confirm." });
        var selections = request.Selections;
        if (selections is null || selections.Keys.Any(key => !Options.Contains(key)) || !selections.Values.Any(selected => selected))
            return Results.BadRequest(new { error = "Choose at least one valid Fresh start option." });
        bool Selected(string key) => selections.TryGetValue(key, out var selected) && selected;

        var bytesBefore = DatabaseBytes(connectionString) + RecoveryBackups(dataDir).Sum(file => file.Length);
        long removedTasks = 0, removedLists = 0, removedEvents = 0;
        try
        {
            using var connection = Open(connectionString);
            using var transaction = connection.BeginTransaction();
            // Deletions keep their usual Deleted events unless the same Fresh start clears that history anyway.
            var recordDeletions = !Selected("deletedTaskHistory") && !Selected("allHistory");
            var deletedAt = DateTimeOffset.UtcNow.ToString("O");
            var eventsBefore = Count(connection, transaction, "SELECT COUNT(*) FROM task_events;");

            if (Selected("everything"))
            {
                removedTasks = Count(connection, transaction, "SELECT COUNT(*) FROM items;");
                removedLists = Count(connection, transaction, "SELECT COUNT(*) FROM lists;");
                if (recordDeletions) Execute(connection, transaction, """
                    INSERT INTO task_events
                        (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                    SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
                    FROM items;
                    """, ("$deletedAt", deletedAt));
                Execute(connection, transaction, """
                    DELETE FROM lists;
                    INSERT INTO lists (name, description, next_task_number, created_at) VALUES ('Tasks', '', 1, $createdAt);
                    """, ("$createdAt", deletedAt));
            }
            else
            {
                if (Selected("archivedLists"))
                {
                    removedTasks += Count(connection, transaction, "SELECT COUNT(*) FROM items WHERE list_id IN (SELECT id FROM lists WHERE archived = 1);");
                    if (recordDeletions) Execute(connection, transaction, """
                        INSERT INTO task_events
                            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                        SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
                        FROM items
                        WHERE list_id IN (SELECT id FROM lists WHERE archived = 1);
                        """, ("$deletedAt", deletedAt));
                    removedLists = Execute(connection, transaction, "DELETE FROM lists WHERE archived = 1;");
                }
                if (Selected("finishedTasks"))
                {
                    // A finished task goes only when none of its subtasks are still open, so its whole tree is finished.
                    Execute(connection, transaction, """
                        DROP TABLE IF EXISTS temp.fresh_start_items;
                        CREATE TEMP TABLE fresh_start_items AS
                        SELECT i.list_id, i.display_id FROM items i
                        WHERE i.status IN ('Done', 'Cancelled')
                          AND NOT EXISTS (SELECT 1 FROM items d WHERE d.list_id = i.list_id
                                          AND d.display_id LIKE i.display_id || '.%' AND d.status = 'Open');
                        """);
                    removedTasks += Count(connection, transaction, "SELECT COUNT(*) FROM temp.fresh_start_items;");
                    if (recordDeletions) Execute(connection, transaction, """
                        INSERT INTO task_events
                            (universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source)
                        SELECT universal_id, 'Deleted', $deletedAt, status, 'Deleted', list_id, display_id, parent_display_id, title, 'live'
                        FROM items
                        WHERE (list_id, display_id) IN (SELECT list_id, display_id FROM temp.fresh_start_items);
                        """, ("$deletedAt", deletedAt));
                    Execute(connection, transaction, """
                        DELETE FROM items WHERE (list_id, display_id) IN (SELECT list_id, display_id FROM temp.fresh_start_items);
                        DROP TABLE temp.fresh_start_items;
                        """);
                }
            }

            if (Selected("allHistory"))
            {
                Execute(connection, transaction, "DELETE FROM task_events;");
                // Startup would add legacy events for the remaining tasks anyway. Adding them now, with the milestone
                // trigger suspended, keeps them from queueing milestone notices.
                var trigger = Scalar(connection, transaction,
                    "SELECT sql FROM sqlite_schema WHERE type = 'trigger' AND name = 'trg_task_events_milestone_notification';") as string;
                if (trigger is not null) Execute(connection, transaction, "DROP TRIGGER trg_task_events_milestone_notification;");
                TaskEventBackfill.Run(connection, transaction);
                if (trigger is not null) Execute(connection, transaction, trigger);
            }
            else if (Selected("deletedTaskHistory"))
                Execute(connection, transaction, "DELETE FROM task_events WHERE universal_id NOT IN (SELECT universal_id FROM items);");

            if (Selected("allHistory") || Selected("deletedTaskHistory"))
            {
                // Saved names of deleted lists only label history, so they go once no history refers to them.
                Execute(connection, transaction, """
                    DELETE FROM deleted_lists WHERE id NOT IN (SELECT list_id FROM task_events);
                    UPDATE task_history_state SET revision = lower(hex(randomblob(16))) WHERE id = 1;
                    """);
                // Milestone counters always follow recorded history (startup rebuilds them too), so progress restarts
                // from the history that remains.
                MilestoneNotifications.RebuildCounters(connection, transaction);
                removedEvents = Math.Max(0, eventsBefore - Count(connection, transaction, "SELECT COUNT(*) FROM task_events;"));
            }
            transaction.Commit();
        }
        catch (SqliteException error)
        {
            logger.LogWarning(error, "Fresh start failed; uncommitted changes were rolled back.");
            var busy = error.SqliteErrorCode is 5 or 6;
            return Results.Json(new { error = busy ? "TaskList is busy. Try Fresh start again." : "Fresh start failed. Current data was not changed." },
                statusCode: busy ? 409 : 500);
        }

        var removedBackups = 0;
        string? warning = null;
        if (Selected("recoveryBackups"))
        {
            foreach (var file in RecoveryBackups(dataDir))
            {
                try { file.Delete(); removedBackups++; }
                catch (IOException) { warning = "Some recovery backups could not be deleted. Check data/backups on the server."; }
                catch (UnauthorizedAccessException) { warning = "Some recovery backups could not be deleted. Check data/backups on the server."; }
            }
        }

        var compacted = true;
        try
        {
            using var connection = Open(connectionString);
            Execute(connection, null, "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException error)
        {
            logger.LogWarning(error, "Fresh start finished, but database compaction failed.");
            compacted = false;
            warning ??= "Selected data was cleared, but the database could not be compacted. Freed space can still be reused; check available disk space.";
        }

        var freedBytes = Math.Max(0, bytesBefore - DatabaseBytes(connectionString) - RecoveryBackups(dataDir).Sum(file => file.Length));
        logger.LogInformation("Fresh start removed {Tasks} tasks, {Lists} lists, {Events} history events, and {Backups} recovery backups; freed {Bytes} bytes.",
            removedTasks, removedLists, removedEvents, removedBackups, freedBytes);
        return Results.Ok(new { removedTasks, removedLists, removedEvents, removedBackups, freedBytes, compacted, warning });
    }

    private static FileInfo[] RecoveryBackups(string dataDir)
    {
        var directory = new DirectoryInfo(Path.Combine(dataDir, "backups"));
        return directory.Exists ? directory.GetFiles("before-restore-*.zip") : [];
    }

    private static long DatabaseBytes(string connectionString)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return new[] { path, path + "-wal" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
    }

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, sql, transaction);
        return command.ExecuteScalar();
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string sql) => Convert.ToInt64(Scalar(connection, transaction, sql));

    private sealed record FreshStartSummary(long FinishedTasks, long ArchivedLists, long ArchivedListTasks, long Lists, long Tasks,
        long DeletedTaskEvents, long Events, int RecoveryBackups, long RecoveryBackupBytes, long DatabaseBytes);
}
