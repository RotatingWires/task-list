using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

static class DataTransfer
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxDatabaseBytes = 1024L * 1024 * 1024;
    private const int MaxManifestBytes = 16 * 1024;
    private const string Format = "tasklist-backup";
    private static readonly string AppVersion = typeof(DataTransfer).Assembly.GetName().Version!.ToString(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TableSpec[] Tables =
    [
        new("universal_ids", "id"),
        new("lists", "id, name, description, next_task_number, created_at, archived"),
        new("deleted_lists", "id, name"),
        new("items", "universal_id, list_id, display_id, parent_display_id, title, description, status, created_at, updated_at, completed_at, cancelled_at, reopened_at, next_child_number"),
        new("task_events", "id, universal_id, event_type, event_at, from_status, to_status, list_id, display_id, parent_display_id, title, source"),
        new("milestone_notifications", "key, kind, threshold, scope, scope_value, scope_label, reached_at"),
        new("milestone_notification_counters", "scope_key, total")
    ];

    public static void MapEndpoints(RouteGroupBuilder api, string connectionString, string dataDir, ILogger logger)
    {
        api.MapGet("/export", (HttpContext context) => Export(context, connectionString, logger));
        api.MapPost("/import/backup", (HttpContext context) => Restore(context, connectionString, dataDir, logger))
            .WithMetadata(new RequestSizeLimitAttribute(MaxArchiveBytes));
    }

    private static async Task Export(HttpContext context, string connectionString, ILogger logger)
    {
        var workDir = CreateWorkDirectory();
        try
        {
            var path = Path.Combine(workDir, "export.zip");
            await CreateArchive(connectionString, path, workDir, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            await Results.File(path, "application/zip", $"task-list-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip")
                .ExecuteAsync(context);
        }
        catch (SqliteException error) when (!context.Response.HasStarted)
        {
            logger.LogError(error, "TaskList export failed.");
            await Results.Json(new { error = "Could not create the export. Try again." }, statusCode: 503).ExecuteAsync(context);
        }
        finally { DeleteWorkDirectory(workDir, logger); }
    }

    private static async Task Restore(HttpContext context, string connectionString, string dataDir, ILogger logger)
    {
        var workDir = CreateWorkDirectory();
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.Request.ContentLength > MaxArchiveBytes)
                throw new InvalidDataException("Backup ZIP exceeds the 256 MiB import limit.");
            var uploadPath = Path.Combine(workDir, "upload.zip");
            await using (var output = File.Create(uploadPath))
                await CopyLimited(context.Request.Body, output, MaxArchiveBytes, context.RequestAborted);

            var databasePath = Path.Combine(workDir, "restore.db");
            var manifest = await ExtractArchive(uploadPath, databasePath, context.RequestAborted);
            if (manifest.Format != Format || manifest.FormatVersion != 1 || manifest.Totals is null ||
                !Version.TryParse(manifest.ApplicationVersion, out var version) || version > Version.Parse(AppVersion))
                throw new InvalidDataException("This is not a supported TaskList backup. Backups from newer TaskList versions require an update first.");

            using var source = OpenDatabase(databasePath, SqliteOpenMode.ReadOnly);
            ValidateDatabase(source);
            var totals = ReadTotals(source);

            // Keep a recovery copy before changing any live rows. It is private server data.
            var backupDir = Path.Combine(dataDir, "backups");
            CreatePrivateDirectory(backupDir);
            var recoveryName = $"before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip";
            await CreateArchive(connectionString, Path.Combine(backupDir, recoveryName), workDir, context.RequestAborted);
            context.RequestAborted.ThrowIfCancellationRequested();

            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString()))
            {
                destination.Open();
                using (var settings = Command(destination, "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;"))
                    settings.ExecuteNonQuery();
                using var transaction = destination.BeginTransaction();
                using (var defer = Command(destination, "PRAGMA defer_foreign_keys=ON;", transaction)) defer.ExecuteNonQuery();
                var sequences = ReadCounterValues(destination, transaction, "SELECT name, MAX(seq) FROM sqlite_sequence WHERE name IN ('lists','universal_ids','task_events') GROUP BY name;");
                var listNumbers = ReadCounterValues(destination, transaction, "SELECT CAST(id AS TEXT), next_task_number FROM lists;");
                var childNumbers = ReadCounterValues(destination, transaction, "SELECT CAST(universal_id AS TEXT), next_child_number FROM items;");
                var triggers = ReadLiveTriggers(destination, transaction);
                foreach (var trigger in triggers)
                {
                    using var drop = Command(destination, $"DROP TRIGGER {QuoteIdentifier(trigger.Name)};", transaction);
                    drop.ExecuteNonQuery();
                }
                foreach (var table in Tables.Reverse())
                {
                    if (table.Name == "universal_ids") continue; // This allocation ledger stays append-only, even across restores.
                    using var clear = Command(destination, $"DELETE FROM {table.Name};", transaction);
                    clear.ExecuteNonQuery();
                }
                foreach (var table in Tables) CopyTable(source, destination, transaction, table, context.RequestAborted);
                using (var clear = Command(destination, "DELETE FROM sqlite_sequence WHERE name IN ('lists', 'universal_ids', 'task_events');", transaction))
                    clear.ExecuteNonQuery();
                CopyTable(source, destination, transaction, new("sqlite_sequence", "name, seq"), context.RequestAborted,
                    " WHERE name IN ('lists', 'universal_ids', 'task_events')");
                foreach (var (name, value) in sequences)
                {
                    using var update = Command(destination, "UPDATE sqlite_sequence SET seq=MAX(seq,$value) WHERE name=$name;", transaction);
                    update.Parameters.AddWithValue("$name", name);
                    update.Parameters.AddWithValue("$value", value);
                    if (update.ExecuteNonQuery() == 0)
                    {
                        using var insert = Command(destination, "INSERT INTO sqlite_sequence(name,seq) VALUES ($name,$value);", transaction);
                        insert.Parameters.AddWithValue("$name", name);
                        insert.Parameters.AddWithValue("$value", value);
                        insert.ExecuteNonQuery();
                    }
                }
                KeepCounters(destination, transaction, listNumbers, "UPDATE lists SET next_task_number=MAX(next_task_number,$value) WHERE id=$id;");
                KeepCounters(destination, transaction, childNumbers, "UPDATE items SET next_child_number=MAX(next_child_number,$value) WHERE universal_id=$id;");
                using (var check = Command(destination, "PRAGMA foreign_key_check;", transaction))
                using (var reader = check.ExecuteReader())
                    if (reader.Read()) throw new InvalidDataException("Backup contains invalid task relationships.");
                foreach (var trigger in triggers)
                {
                    using var recreate = Command(destination, trigger.Sql, transaction);
                    recreate.ExecuteNonQuery();
                }
                context.RequestAborted.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            await Results.Ok(new { restored = true, totals, recoveryBackup = recoveryName }).ExecuteAsync(context);
        }
        catch (InvalidDataException error)
        {
            await Results.BadRequest(new { error = error.Message }).ExecuteAsync(context);
        }
        catch (JsonException)
        {
            await Results.BadRequest(new { error = "Backup manifest is invalid." }).ExecuteAsync(context);
        }
        catch (SqliteException error)
        {
            logger.LogWarning(error, "TaskList backup restore failed; uncommitted changes were rolled back.");
            var busy = error.SqliteErrorCode is 5 or 6;
            await Results.Json(new { error = busy ? "TaskList is busy. Try restoring again." : "Backup database is invalid or incompatible. Current data was not replaced." },
                statusCode: busy ? 409 : 400).ExecuteAsync(context);
        }
        finally { DeleteWorkDirectory(workDir, logger); }
    }

    private static async Task CreateArchive(string connectionString, string zipPath, string workDir, CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(workDir, $"snapshot-{Guid.NewGuid():N}.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var destination = OpenDatabase(snapshotPath, SqliteOpenMode.ReadWriteCreate))
        {
            source.Open();
            cancellationToken.ThrowIfCancellationRequested();
            source.BackupDatabase(destination);
            using var standalone = Command(destination, "PRAGMA journal_mode=DELETE;");
            standalone.ExecuteNonQuery();
        }
        BackupTotals totals;
        using (var snapshot = OpenDatabase(snapshotPath, SqliteOpenMode.ReadOnly)) totals = ReadTotals(snapshot);
        var manifest = new BackupManifest(Format, 1, AppVersion, DateTimeOffset.UtcNow, totals);
        try
        {
            await using var output = File.Create(zipPath);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            archive.CreateEntryFromFile(snapshotPath, "task-list.db", CompressionLevel.Optimal);
            await using var manifestOutput = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(manifestOutput, manifest, JsonOptions, cancellationToken);
        }
        catch { File.Delete(zipPath); throw; }
    }

    private static async Task<BackupManifest> ExtractArchive(string zipPath, string databasePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count != 2 || archive.Entries.Count(entry => entry.FullName == "manifest.json") != 1 ||
            archive.Entries.Count(entry => entry.FullName == "task-list.db") != 1)
            throw new InvalidDataException("Choose a TaskList export ZIP containing manifest.json and task-list.db.");
        var manifestEntry = archive.GetEntry("manifest.json")!;
        var databaseEntry = archive.GetEntry("task-list.db")!;
        if (manifestEntry.Length > MaxManifestBytes || databaseEntry.Length > MaxDatabaseBytes)
            throw new InvalidDataException("Backup contents exceed the import size limits (1 GiB database).");
        using var manifestBytes = new MemoryStream();
        await using (var input = manifestEntry.Open())
            await CopyLimited(input, manifestBytes, MaxManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Backup manifest is missing.");
        await using (var input = databaseEntry.Open())
        await using (var output = File.Create(databasePath))
            await CopyLimited(input, output, MaxDatabaseBytes, cancellationToken);
        return manifest;
    }

    private static void ValidateDatabase(SqliteConnection source)
    {
        using (var check = Command(source, "PRAGMA integrity_check;"))
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("Backup database failed its integrity check.");
        foreach (var table in Tables.Append(new("sqlite_sequence", "name, seq")))
        {
            using var schema = Command(source, "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;");
            schema.Parameters.AddWithValue("$name", table.Name);
            if (schema.ExecuteScalar() is not string sql || !sql.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup is missing the {table.Name} data table.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            using var columns = Command(source, $"PRAGMA table_xinfo({table.Name});");
            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(6) != 0) throw new InvalidDataException("Backup contains unsupported computed data columns.");
                names.Add(reader.GetString(1));
            }
            if (!names.SetEquals(table.Columns.Split(", ")))
                throw new InvalidDataException($"Backup has an incompatible {table.Name} table.");
        }
        using (var check = Command(source, "PRAGMA foreign_key_check;"))
        using (var reader = check.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("Backup contains invalid task relationships.");
        using (var active = Command(source, "SELECT COUNT(*) FROM lists WHERE archived=0;"))
            if (Convert.ToInt64(active.ExecuteScalar()) == 0) throw new InvalidDataException("Backup must contain an active list.");
        using (var items = Command(source, "SELECT display_id, parent_display_id FROM items;"))
        using (var reader = items.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var split = id.LastIndexOf('.');
                var parent = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!Regex.IsMatch(id, @"^[1-9]\d*(?:\.[1-9]\d*)*$", RegexOptions.CultureInvariant) ||
                    parent != (split < 0 ? null : id[..split]))
                    throw new InvalidDataException("Backup contains invalid task IDs or nesting.");
            }
        }
        using (var counters = Command(source, """
            SELECT
                EXISTS(SELECT 1 FROM sqlite_sequence WHERE name IN ('lists','universal_ids','task_events') AND (typeof(seq)<>'integer' OR seq<0))
                OR EXISTS(SELECT 1 FROM sqlite_sequence WHERE name IN ('lists','universal_ids','task_events') GROUP BY name HAVING COUNT(*)>1)
                OR EXISTS(SELECT 1 FROM lists WHERE archived NOT IN (0,1) OR next_task_number <=
                    COALESCE((SELECT MAX(CAST(display_id AS INTEGER)) FROM items WHERE list_id=lists.id AND parent_display_id IS NULL),0))
                OR EXISTS(SELECT 1 FROM items p WHERE next_child_number <=
                    COALESCE((SELECT MAX(CAST(substr(c.display_id,length(p.display_id)+2) AS INTEGER)) FROM items c
                              WHERE c.list_id=p.list_id AND c.parent_display_id=p.display_id),0))
                OR COALESCE((SELECT seq FROM sqlite_sequence WHERE name='lists'),0) <
                    (SELECT MAX(id) FROM (SELECT id FROM lists UNION ALL SELECT id FROM deleted_lists UNION ALL SELECT list_id FROM task_events))
                OR COALESCE((SELECT seq FROM sqlite_sequence WHERE name='universal_ids'),0) <
                    COALESCE((SELECT MAX(id) FROM (SELECT id FROM universal_ids UNION ALL SELECT universal_id FROM items UNION ALL SELECT universal_id FROM task_events)),0)
                OR COALESCE((SELECT seq FROM sqlite_sequence WHERE name='task_events'),0) < COALESCE((SELECT MAX(id) FROM task_events),0);
            """))
            if (Convert.ToInt64(counters.ExecuteScalar()) != 0) throw new InvalidDataException("Backup contains invalid ID counters.");
    }

    private static void CopyTable(SqliteConnection source, SqliteConnection destination, SqliteTransaction transaction,
        TableSpec table, CancellationToken cancellationToken, string filter = "")
    {
        using var read = Command(source, $"SELECT {table.Columns} FROM {table.Name}{filter};");
        using var reader = read.ExecuteReader();
        using var write = Command(destination,
            $"INSERT {(table.Name == "universal_ids" ? "OR IGNORE " : "")}INTO {table.Name} ({table.Columns}) VALUES ({string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => "$p" + i))});", transaction);
        for (var i = 0; i < reader.FieldCount; i++) write.Parameters.AddWithValue("$p" + i, DBNull.Value);
        write.Prepare();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < reader.FieldCount; i++) write.Parameters[i].Value = reader.GetValue(i);
            write.ExecuteNonQuery();
        }
    }

    private static List<(string Id, long Value)> ReadCounterValues(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var result = new List<(string, long)>();
        using var command = Command(connection, sql, transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt64(1)));
        return result;
    }

    private static void KeepCounters(SqliteConnection connection, SqliteTransaction transaction, List<(string Id, long Value)> counters, string sql)
    {
        using var command = Command(connection, sql, transaction);
        command.Parameters.AddWithValue("$id", "");
        command.Parameters.AddWithValue("$value", 0L);
        foreach (var (id, value) in counters)
        {
            command.Parameters["$id"].Value = id;
            command.Parameters["$value"].Value = value;
            command.ExecuteNonQuery();
        }
    }

    private static List<(string Name, string Sql)> ReadLiveTriggers(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new List<(string, string)>();
        var tableNames = string.Join(",", Tables.Select(table => "'" + table.Name + "'"));
        using var command = Command(connection, $"SELECT name, sql FROM sqlite_schema WHERE type='trigger' AND tbl_name IN ({tableNames});", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static BackupTotals ReadTotals(SqliteConnection connection)
    {
        using var command = Command(connection, """
            SELECT (SELECT COUNT(*) FROM lists), (SELECT COUNT(*) FROM lists WHERE archived=1),
                   (SELECT COUNT(*) FROM items WHERE parent_display_id IS NULL), (SELECT COUNT(*) FROM items WHERE parent_display_id IS NOT NULL),
                   (SELECT COUNT(*) FROM task_events);
            """);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var settings = Command(connection, "PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=5000;");
            settings.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "task-list-transfer-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(path);
        return path;
    }
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void DeleteWorkDirectory(string path, ILogger logger)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException error) { logger.LogWarning(error, "Could not clean up TaskList transfer temporary files."); }
        catch (UnauthorizedAccessException error) { logger.LogWarning(error, "Could not clean up TaskList transfer temporary files."); }
    }
    private static async Task CopyLimited(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Import exceeds its allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private sealed record TableSpec(string Name, string Columns);
    private sealed record BackupTotals(long Lists, long ArchivedLists, long Tasks, long Subtasks, long Events);
    private sealed record BackupManifest(string Format, int FormatVersion, string ApplicationVersion, DateTimeOffset ExportedAt, BackupTotals Totals);
}
