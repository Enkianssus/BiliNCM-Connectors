using System.Text.Json;
using UnifiedPlayerControlPoc;

internal static class Program
{
    public static async Task Main()
    {
        var output = new StringWriter();
        var adapter = new BlockingDisposeAdapter();
        var input = new StringReader(
            "{\"id\":\"shutdown-1\",\"action\":\"shutdown\"}\n");
        var run = ConnectorRuntime.RunAsync("test", adapter, input, output);
        await adapter.DisposeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        if (run.IsCompleted || output.ToString().Contains("shutdown-1"))
        {
            throw new InvalidOperationException(
                "Shutdown was acknowledged before adapter cleanup drained.");
        }

        adapter.AllowDispose.TrySetResult();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(2));
        if (exitCode != 0 || !adapter.Disposed)
        {
            throw new InvalidOperationException(
                "Connector did not finish a clean adapter shutdown.");
        }

        using var response = JsonDocument.Parse(output.ToString().Trim());
        var root = response.RootElement;
        if (root.GetProperty("id").GetString() != "shutdown-1"
            || !root.GetProperty("ok").GetBoolean()
            || !root.GetProperty("result")
                .GetProperty("stopped").GetBoolean()
            || !root.GetProperty("result")
                .GetProperty("drained").GetBoolean())
        {
            throw new InvalidOperationException(
                "Shutdown response did not confirm a completed drain.");
        }

        Console.WriteLine("Connector runtime shutdown drain test passed.");
    }

    private sealed class BlockingDisposeAdapter : IPlayerAdapter
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public string Key => "test";

        public string DisplayName => "Test";

        public string TestedVersion => "1";

        public PlayerCapabilities Capabilities { get; } = new(
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            "none");

        public Task<PlayerSnapshot> ProbeAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PlayerTrack>> SearchAsync(
            string query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PlayerOperationResult> ExecuteAsync(
            PlayerCommand command,
            PlayerTrack? track,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await AllowDispose.Task.ConfigureAwait(false);
            Disposed = true;
        }
    }
}
