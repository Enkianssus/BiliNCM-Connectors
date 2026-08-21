using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedPlayerControlPoc;

internal static class ConnectorRuntime
{
    private const int ProtocolVersion = 1;
    private const int EventProtocolVersion = 1;
    private const string SnapshotEventsFeature = "snapshot-events-v1";
    private static readonly SemaphoreSlim OutputGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static async Task<int> RunAsync(
        string connectorId,
        IPlayerAdapter adapter)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return await RunAsync(
            connectorId,
            adapter,
            Console.In,
            Console.Out);
    }

    internal static async Task<int> RunAsync(
        string connectorId,
        IPlayerAdapter adapter,
        TextReader input,
        TextWriter output)
    {
        using var lifetimeCancellation = new CancellationTokenSource();
        Task? eventPump = null;
        var eventSource = adapter as IPlayerSnapshotEventSource;
        string? shutdownRequestId = null;
        Exception? cleanupError = null;
        try
        {
            string? line;
            while ((line = await input.ReadLineAsync()) is not null)
            {
                line = line.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ConnectorRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<ConnectorRequest>(
                        line,
                        JsonOptions);
                    if (request is null
                        || string.IsNullOrWhiteSpace(request.Id)
                        || string.IsNullOrWhiteSpace(request.Action))
                    {
                        throw new InvalidOperationException(
                            "Request id and action are required.");
                    }

                    if (request.Action.Equals(
                            "shutdown",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Do not acknowledge shutdown until every adapter task
                        // has drained. Some adapters temporarily modify player
                        // process state and restore it from their finally blocks.
                        shutdownRequestId = request.Id;
                        break;
                    }

                    if (request.Action.Equals(
                            "ping",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(output, new ConnectorResponse(
                            request.Id,
                            true,
                            new
                            {
                                protocolVersion = ProtocolVersion,
                                eventProtocolVersion = eventSource is null
                                    ? (int?)null
                                    : EventProtocolVersion,
                                connectorId,
                                connectorVersion = GetVersion(),
                                capabilities = adapter.Capabilities,
                                features = eventSource is null
                                    ? Array.Empty<string>()
                                    : new[] { SnapshotEventsFeature }
                            },
                            null));
                        continue;
                    }

                    if (request.Action.Equals(
                            "subscribe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (eventSource is null)
                        {
                            await WriteResponseAsync(output, new ConnectorResponse(
                                request.Id,
                                true,
                                new
                                {
                                    subscribed = false,
                                    reason = "snapshot-events-unsupported"
                                },
                                null));
                            continue;
                        }
                        if (request.EventProtocolVersion is not null
                            && request.EventProtocolVersion
                                != EventProtocolVersion)
                        {
                            throw new InvalidOperationException(
                                "Unsupported event protocol version: "
                                + request.EventProtocolVersion);
                        }

                        await WriteResponseAsync(output, new ConnectorResponse(
                            request.Id,
                            true,
                            new
                            {
                                subscribed = true,
                                eventProtocolVersion = EventProtocolVersion,
                                feature = SnapshotEventsFeature
                            },
                            null));
                        eventPump ??= PumpSnapshotEventsAsync(
                            connectorId,
                            eventSource,
                            output,
                            lifetimeCancellation.Token);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(request.Player)
                        && !request.Player.Equals(
                            connectorId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"This connector only supports {connectorId}.");
                    }

                    using var timeout = new CancellationTokenSource(
                        GetTimeout(request.Action));
                    var result = await ExecuteAsync(
                        adapter,
                        request,
                        timeout.Token);
                    await WriteResponseAsync(output, new ConnectorResponse(
                        request.Id,
                        true,
                        result,
                        null));
                }
                catch (Exception exception)
                {
                    await WriteResponseAsync(output, new ConnectorResponse(
                        request?.Id ?? string.Empty,
                        false,
                        null,
                        $"{exception.GetType().Name}: {exception.Message}"));
                }
            }
        }
        finally
        {
            lifetimeCancellation.Cancel();
            if (eventPump is not null)
            {
                try
                {
                    await eventPump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal connector shutdown.
                }
                catch (Exception exception)
                {
                    cleanupError = exception;
                }
            }
            try
            {
                await adapter.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }

            if (shutdownRequestId is not null)
            {
                await WriteResponseAsync(output, new ConnectorResponse(
                    shutdownRequestId,
                    cleanupError is null,
                    cleanupError is null
                        ? new { stopped = true, drained = true }
                        : null,
                    cleanupError is null
                        ? null
                        : $"{cleanupError.GetType().Name}: "
                            + cleanupError.Message));
            }
        }

        return cleanupError is null ? 0 : 1;
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static TimeSpan GetTimeout(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "probe" => TimeSpan.FromSeconds(6),
            "search" => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromSeconds(20)
        };
    }

    private static async Task<object?> ExecuteAsync(
        IPlayerAdapter adapter,
        ConnectorRequest request,
        CancellationToken cancellationToken)
    {
        return request.Action.ToLowerInvariant() switch
        {
            "probe" => await adapter.ProbeAsync(cancellationToken),
            "search" => await SearchAsync(
                adapter,
                request.Query,
                cancellationToken),
            "execute" => await ExecuteCommandAsync(
                adapter,
                request,
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unknown action: {request.Action}")
        };
    }

    private static async Task<object?> SearchAsync(
        IPlayerAdapter adapter,
        string? query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException(
                "Search query is required.");
        }

        return await adapter.SearchAsync(query, cancellationToken);
    }

    private static async Task<object?> ExecuteCommandAsync(
        IPlayerAdapter adapter,
        ConnectorRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PlayerCommand>(
                request.Command,
                true,
                out var command))
        {
            throw new InvalidOperationException(
                $"Unknown command: {request.Command}");
        }

        return await adapter.ExecuteAsync(
            command,
            request.Track,
            cancellationToken);
    }

    private static async Task WriteResponseAsync(
        TextWriter output,
        ConnectorResponse response)
    {
        await WriteEnvelopeAsync(output, response, CancellationToken.None);
    }

    private static async Task PumpSnapshotEventsAsync(
        string connectorId,
        IPlayerSnapshotEventSource eventSource,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        long sequence = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in eventSource
                                   .WatchSnapshotsAsync(cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await WriteEnvelopeAsync(
                        output,
                        new ConnectorEvent(
                            "event",
                            "snapshot",
                            connectorId,
                            ++sequence,
                            snapshot),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await Console.Error.WriteLineAsync(
                    $"snapshot event source restarting: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }

            await Task.Delay(1000, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteEnvelopeAsync(
        TextWriter output,
        object envelope,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await OutputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(json).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            OutputGate.Release();
        }
    }

    private sealed record ConnectorRequest(
        string Id,
        string Action,
        string? Player,
        string? Query,
        string? Command,
        PlayerTrack? Track,
        int? EventProtocolVersion);

    private sealed record ConnectorResponse(
        string Id,
        bool Ok,
        object? Result,
        string? Error);

    private sealed record ConnectorEvent(
        string Type,
        string Event,
        string Player,
        long Sequence,
        PlayerSnapshot Snapshot);
}
