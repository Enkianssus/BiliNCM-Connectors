namespace UnifiedPlayerControlPoc;

internal static class Program
{
    private static Task<int> Main()
    {
        return ConnectorRuntime.RunAsync(
            "kugou",
            new KugouPlayerAdapter());
    }
}
