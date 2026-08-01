namespace UnifiedPlayerControlPoc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains(
                "--diagnose-next-guard",
                StringComparer.OrdinalIgnoreCase))
        {
            using var audio = QQMusicAudioMuteScope.Capture();
            var timeline = await QQMusicTimelineProbe.TryCreateAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    audio.CapturedSessionCount,
                    audio.CaptureError,
                    Timeline = timeline?.ReadSnapshot()
                },
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            return 0;
        }

        return await ConnectorRuntime.RunAsync(
            "qqmusic",
            new QQMusicPlayerAdapter
            {
                // The transport still refuses every unknown build. This flag
                // merely enables the hash-locked compatibility profiles.
                AllowUnsafeNativeNext = true
            });
    }
}
