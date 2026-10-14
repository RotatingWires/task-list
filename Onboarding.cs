static class Onboarding
{
    private static string PendingPath(string dataDir) => Path.Combine(dataDir, "import-reminder.pending");

    public static void Initialize(string dataDir, string databasePath)
    {
        if (!File.Exists(databasePath))
            File.WriteAllText(PendingPath(dataDir), DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void MapEndpoints(RouteGroupBuilder api, string dataDir)
    {
        api.MapGet("/onboarding", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { importReminderPending = File.Exists(PendingPath(dataDir)) });
        });
        api.MapPost("/onboarding/acknowledge", () =>
        {
            Acknowledge(dataDir);
            return Results.NoContent();
        });
    }

    public static void Acknowledge(string dataDir) => File.Delete(PendingPath(dataDir));
}
