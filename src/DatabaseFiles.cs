using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

/// <summary>Where the database file lives, and the offline backup command, the same as NewsReview's.</summary>
static class DatabaseFiles
{
    /// <summary>
    /// Earlier releases used a hyphenated file name. Moves it (after emptying its WAL file) to the current name once.
    /// If the old file is in use, for example by another program on Windows, it keeps the old name for this run.
    /// </summary>
    public static string Locate(string dataDir, string fileName, string legacyFileName, ILogger logger)
    {
        var path = Path.Combine(dataDir, fileName);
        var legacyPath = Path.Combine(dataDir, legacyFileName);
        if (File.Exists(path) || !File.Exists(legacyPath)) return path;
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            File.Move(legacyPath, path);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(legacyPath + suffix)) File.Delete(legacyPath + suffix);
            logger.LogInformation("Renamed {Legacy} to {Current}.", legacyFileName, fileName);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            logger.LogWarning("Could not rename {Legacy} to {Current} ({Error}); using {Legacy} for now.", legacyFileName, fileName, error.Message, legacyFileName);
            return File.Exists(path) ? path : legacyPath;
        }
    }

    /// <summary><c>backup --output FILE [--database FILE]</c>: a consistent SQLite copy, checked before it is kept.</summary>
    public static int BackupCommand(string appName, string defaultDatabase, string[] args)
    {
        string database = defaultDatabase, output = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--database" && i + 1 < args.Length) database = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else return Usage(appName, $"unrecognized arguments: {args[i]}");
        }
        if (output.Length == 0) return Usage(appName, "the following arguments are required: --output");
        var source = Path.GetFullPath(database);
        var destination = Path.GetFullPath(output);
        if (!File.Exists(source)) return Usage(appName, "Source database does not exist");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { File.Open(destination, FileMode.CreateNew).Dispose(); }
        catch (IOException) when (File.Exists(destination)) { return Usage(appName, "Output already exists; choose a new filename"); }
        try
        {
            using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
            using var check = dst.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(check.ExecuteScalar());
            if (result != "ok") throw new InvalidOperationException($"Backup integrity check failed: {result}");
        }
        catch
        {
            // Only the new, exclusively created destination belongs to this operation.
            File.Delete(destination);
            throw;
        }
        Console.WriteLine($"Backup created: {destination}");
        return 0;
    }

    static int Usage(string appName, string message)
    {
        Console.Error.WriteLine($"usage: {appName} backup [--database DATABASE] --output OUTPUT");
        Console.Error.WriteLine($"{appName} backup: error: {message}");
        return 2;
    }
}
