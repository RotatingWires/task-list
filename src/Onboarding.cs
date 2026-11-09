/// <summary>
/// The "Restore a Backup Before Starting" prompt, the same as TaskList's: a brand-new database leaves a marker in the
/// data folder until the prompt is answered or a backup is restored.
/// </summary>
static class Onboarding
{
    private static string PendingPath(string dataDir) => Path.Combine(dataDir, "import-reminder.pending");

    public static void Initialize(string dataDir, string databasePath)
    {
        if (!File.Exists(databasePath))
            File.WriteAllText(PendingPath(dataDir), DateTimeOffset.UtcNow.ToString("O"));
    }

    public static bool Pending(string dataDir) => File.Exists(PendingPath(dataDir));

    public static void Acknowledge(string dataDir) => File.Delete(PendingPath(dataDir));
}
