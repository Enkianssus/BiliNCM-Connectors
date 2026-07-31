using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedPlayerControlPoc;

internal static class ConnectorRuntime
{
    private const int ProtocolVersion = 1;

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

        try
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
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
                        await WriteResponseAsync(new ConnectorResponse(
                            request.Id,
                            true,
                            new { stopped = true },
                            null));
                        break;
                    }

                    if (request.Action.Equals(
                            "ping",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(new ConnectorResponse(
                            request.Id,
                            true,
                            new
                            {
                                protocolVersion = ProtocolVersion,
                                connectorId,
                                connectorVersion = GetVersion(),
                                capabilities = adapter.Capabilities
                            },
                            null));
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
                    await WriteResponseAsync(new ConnectorResponse(
                        request.Id,
                        true,
                        result,
                        null));
                }
                catch (Exception exception)
                {
                    await WriteResponseAsync(new ConnectorResponse(
                        request?.Id ?? string.Empty,
                        false,
                        null,
                        $"{exception.GetType().Name}: {exception.Message}"));
                }
            }
        }
        finally
        {
            await adapter.DisposeAsync();
        }

        return 0;
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
        ConnectorResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.FlushAsync();
    }

    private sealed record ConnectorRequest(
        string Id,
        string Action,
        string? Player,
        string? Query,
        string? Command,
        PlayerTrack? Track);

    private sealed record ConnectorResponse(
        string Id,
        bool Ok,
        object? Result,
        string? Error);
}
