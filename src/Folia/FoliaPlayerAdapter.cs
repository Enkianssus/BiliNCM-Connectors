using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiedPlayerControlPoc;

internal sealed partial class FoliaPlayerAdapter : IPlayerAdapter
{
    private const int StagePort = 32107;
    private const string TokenEnvironmentVariable = "BILINCM_FOLIA_TOKEN";

    private readonly string _token =
        Environment.GetEnvironmentVariable(TokenEnvironmentVariable)?.Trim()
        ?? string.Empty;
    private readonly HttpClient _stageClient = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{StagePort}"),
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly HttpClient _neteaseClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly ConcurrentDictionary<string, PlayerTrack> _knownTracks =
        new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private Task? _receiveTask;
    private PlayerSnapshot _snapshot = CreateSnapshot(
        false,
        "Folia 连接器尚未连接 Stage API",
        null);

    public FoliaPlayerAdapter()
    {
        _neteaseClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 BiliNCM-Folia-Connector/1.0");
        _neteaseClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _neteaseClient.DefaultRequestHeaders.Add("Cookie", "os=pc; appver=3.1.37;");
        _neteaseClient.DefaultRequestHeaders.Add("X-Real-IP", "118.88.88.88");
        _neteaseClient.DefaultRequestHeaders.Add(
            "X-Forwarded-For",
            "118.88.88.88");
    }

    public string Key => "folia";

    public string DisplayName => "Folia";

    public string TestedVersion => "Stage API";

    public PlayerCapabilities Capabilities { get; } = new(
        Search: true,
        PlaySelected: true,
        Previous: true,
        Pause: true,
        Resume: true,
        Toggle: false,
        Next: true,
        InsertNext: true,
        InsertNextLevel: "由 Folia Stage 队列管理");

    public async Task<PlayerSnapshot> ProbeAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return UpdateSnapshot(
                false,
                "未配置 Folia Stage Token",
                null);
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return ReadSnapshot();
    }

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        EnsureToken();

        var trimmed = query.Trim();
        var idMatch = NeteaseIdPattern().Match(trimmed);
        if (!idMatch.Success)
        {
            return await SearchStageAsync(trimmed, cancellationToken)
                .ConfigureAwait(false);
        }

        var songId = idMatch.Groups[1].Value;
        var exactTask = TryLookupNeteaseTrackAsync(
            songId,
            cancellationToken);
        var searchTask = TrySearchStageAsync(songId, cancellationToken);
        await Task.WhenAll(exactTask, searchTask).ConfigureAwait(false);

        var exact = await exactTask.ConfigureAwait(false);
        if (exact is not null)
        {
            Remember(exact);
            return [exact];
        }

        return await searchTask.ConfigureAwait(false);
    }

    public async Task<PlayerOperationResult> ExecuteAsync(
        PlayerCommand command,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        EnsureToken();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (command is PlayerCommand.InsertNext
                or PlayerCommand.PlaySelected)
            {
                if (track is null
                    || !long.TryParse(track.Id, out var songId)
                    || songId <= 0)
                {
                    return Result(
                        OperationOutcome.Rejected,
                        "Folia 需要有效的网易云歌曲 ID。");
                }

                Remember(track);
                using var insertResponse = await PostAsync(
                    "/stage/player/queue",
                    new { action = "insert-next", songId },
                    cancellationToken).ConfigureAwait(false);
                if (!insertResponse.IsSuccessStatusCode)
                {
                    return Result(
                        OperationOutcome.Rejected,
                        $"Folia 拒绝插入下一首（HTTP {(int)insertResponse.StatusCode}）。");
                }

                if (command is PlayerCommand.InsertNext)
                {
                    return Result(
                        OperationOutcome.Accepted,
                        $"Folia 已接收下一首：{track.DisplayName}");
                }

                using var nextResponse = await PostAsync(
                    "/stage/player/control",
                    new { action = "next" },
                    cancellationToken).ConfigureAwait(false);
                return nextResponse.IsSuccessStatusCode
                    ? Result(
                        OperationOutcome.Accepted,
                        $"Folia 已接收立即播放：{track.DisplayName}")
                    : Result(
                        OperationOutcome.Rejected,
                        $"Folia 拒绝切到目标歌曲（HTTP {(int)nextResponse.StatusCode}）。");
            }

            var action = command switch
            {
                PlayerCommand.Previous => "previous",
                PlayerCommand.Pause => "pause",
                PlayerCommand.Resume => "play",
                PlayerCommand.Next => "next",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(action))
            {
                return Result(
                    OperationOutcome.Unsupported,
                    $"Folia 暂不支持 {command} 指令。");
            }

            using var response = await PostAsync(
                "/stage/player/control",
                new { action },
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Result(
                    OperationOutcome.Accepted,
                    $"Folia 已接收 {action} 指令。")
                : Result(
                    OperationOutcome.Rejected,
                    $"Folia 拒绝 {action}（HTTP {(int)response.StatusCode}）。");
        }
        catch (HttpRequestException exception)
        {
            return Result(
                OperationOutcome.Rejected,
                $"Folia Stage 通信失败：{exception.Message}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try
            {
                if (socket.State is WebSocketState.Open
                    or WebSocketState.CloseReceived)
                {
                    using var closeTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "connector shutdown",
                        closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Process shutdown must not wait on a broken local socket.
            }
            socket.Dispose();
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // Receive failures are reflected in the snapshot.
            }
        }

        _stageClient.Dispose();
        _neteaseClient.Dispose();
        _connectionGate.Dispose();
        _operationGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        var current = _socket;
        if (current?.State is WebSocketState.Open)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = _socket;
            if (current?.State is WebSocketState.Open)
            {
                return;
            }

            current?.Dispose();
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(
                "Authorization",
                $"Bearer {_token}");
            var uri = new Uri(
                $"ws://127.0.0.1:{StagePort}/stage/player/ws"
                + $"?token={Uri.EscapeDataString(_token)}");
            try
            {
                using var connectTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _lifetime.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                await socket.ConnectAsync(uri, connectTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or WebSocketException
                    or OperationCanceledException)
            {
                socket.Dispose();
                UpdateSnapshot(
                    false,
                    exception is OperationCanceledException
                        && !cancellationToken.IsCancellationRequested
                        ? "Folia Stage WebSocket 连接超时"
                        : $"Folia Stage WebSocket 连接失败：{exception.Message}",
                    null);
                return;
            }

            _socket = socket;
            UpdateSnapshot(
                true,
                "Folia Stage WebSocket 已连接",
                ReadSnapshot().Current);
            _receiveTask = ReceiveLoopAsync(socket, _lifetime.Token);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State is WebSocketState.Open
                && !cancellationToken.IsCancellationRequested)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(
                        buffer,
                        cancellationToken).ConfigureAwait(false);
                    if (result.MessageType is WebSocketMessageType.Close)
                    {
                        return;
                    }
                    payload.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType is not WebSocketMessageType.Text)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(payload.ToArray());
                ConsumeStageEvent(json);
            }
        }
        catch (Exception exception)
            when (exception is WebSocketException
                or OperationCanceledException
                or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                UpdateSnapshot(
                    false,
                    $"Folia Stage WebSocket 已断开：{exception.Message}",
                    null);
            }
        }
        finally
        {
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
                if (!cancellationToken.IsCancellationRequested)
                {
                    UpdateSnapshot(false, "Folia Stage WebSocket 已断开", null);
                }
            }
            socket.Dispose();
        }
    }

    private void ConsumeStageEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventName = GetString(root, "event")
            ?? GetString(root, "type")
            ?? string.Empty;
        if (!eventName.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
            && !eventName.Equals(
                "TRACK_CHANGED",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var track = ParseStageTrack(root);
        UpdateSnapshot(
            true,
            $"Folia {eventName}",
            track);
    }

    private async Task<IReadOnlyList<PlayerTrack>> SearchStageAsync(
        string query,
        CancellationToken cancellationToken)
    {
        using var response = await PostAsync(
            "/stage/player/search",
            new { query, limit = 20 },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Folia 搜索失败（HTTP {(int)response.StatusCode}）。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var songs = FindSongs(document.RootElement);
        if (songs is null)
        {
            return [];
        }

        var tracks = songs.Value
            .EnumerateArray()
            .Select(ParseStageTrack)
            .Where(track => track is not null)
            .Cast<PlayerTrack>()
            .ToArray();
        foreach (var track in tracks)
        {
            Remember(track);
        }
        return tracks;
    }

    private async Task<IReadOnlyList<PlayerTrack>> TrySearchStageAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SearchStageAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private async Task<PlayerTrack?> TryLookupNeteaseTrackAsync(
        string songId,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri =
                "https://music.163.com/api/song/detail/"
                + $"?id={Uri.EscapeDataString(songId)}"
                + $"&ids=%5B{Uri.EscapeDataString(songId)}%5D";
            using var response = await _neteaseClient.GetAsync(
                uri,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!TryGetProperty(
                    document.RootElement,
                    "songs",
                    out var songs)
                || songs.ValueKind is not JsonValueKind.Array
                || songs.GetArrayLength() == 0)
            {
                return null;
            }

            var song = songs[0];
            var resolvedId = GetScalarString(song, "id");
            if (!string.Equals(resolvedId, songId, StringComparison.Ordinal))
            {
                return null;
            }

            return ParseNeteaseTrack(song);
        }
        catch
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        string route,
        object payload,
        CancellationToken cancellationToken)
    {
        EnsureToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        return await _stageClient.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private PlayerTrack? ParseStageTrack(JsonElement payload)
    {
        var track = payload;
        if (TryGetProperty(payload, "track", out var directTrack))
        {
            track = directTrack;
        }
        else if (TryGetProperty(payload, "current", out var current))
        {
            track = current;
        }
        else if (TryGetProperty(payload, "data", out var data)
            && TryGetProperty(data, "track", out var dataTrack))
        {
            track = dataTrack;
        }

        if (track.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        var id = GetScalarString(track, "id")
            ?? GetScalarString(track, "songId")
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = GetString(track, "title")
            ?? GetString(track, "name")
            ?? $"歌曲 {id}";
        var artist = ParseArtists(track);
        var album = ParseAlbum(track);
        var cover = ParseCover(track);
        var parsed = new PlayerTrack(
            id,
            title,
            artist,
            album,
            "",
            cover);

        if (_knownTracks.TryGetValue(id, out var known))
        {
            parsed = new PlayerTrack(
                id,
                string.IsNullOrWhiteSpace(title) ? known.Title : title,
                string.IsNullOrWhiteSpace(artist) ? known.Artist : artist,
                string.IsNullOrWhiteSpace(album) ? known.Album : album,
                known.NativeData,
                string.IsNullOrWhiteSpace(cover) ? known.CoverUrl : cover);
        }
        Remember(parsed);
        return parsed;
    }

    private static PlayerTrack? ParseNeteaseTrack(JsonElement song)
    {
        var id = GetScalarString(song, "id") ?? string.Empty;
        var title = GetString(song, "name") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artist = ParseArtists(song);
        var album = ParseAlbum(song);
        var cover = ParseCover(song);
        return new PlayerTrack(id, title, artist, album, "", cover);
    }

    private static JsonElement? FindSongs(JsonElement root)
    {
        if (TryGetProperty(root, "songs", out var songs)
            && songs.ValueKind is JsonValueKind.Array)
        {
            return songs;
        }
        if (TryGetProperty(root, "data", out var data)
            && TryGetProperty(data, "songs", out songs)
            && songs.ValueKind is JsonValueKind.Array)
        {
            return songs;
        }
        if (TryGetProperty(root, "result", out var result)
            && TryGetProperty(result, "songs", out songs)
            && songs.ValueKind is JsonValueKind.Array)
        {
            return songs;
        }
        return null;
    }

    private static string ParseArtists(JsonElement track)
    {
        if (TryGetProperty(track, "artists", out var artists)
            || TryGetProperty(track, "ar", out artists))
        {
            if (artists.ValueKind is JsonValueKind.Array)
            {
                return string.Join(
                    "/",
                    artists.EnumerateArray()
                        .Select(artist => artist.ValueKind is JsonValueKind.String
                            ? artist.GetString()
                            : GetString(artist, "name"))
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
            }
        }

        if (TryGetProperty(track, "artist", out var artistValue))
        {
            return artistValue.ValueKind is JsonValueKind.String
                ? artistValue.GetString() ?? string.Empty
                : GetString(artistValue, "name") ?? string.Empty;
        }
        return string.Empty;
    }

    private static string ParseAlbum(JsonElement track)
    {
        if (!TryGetProperty(track, "album", out var album)
            && !TryGetProperty(track, "al", out album))
        {
            return string.Empty;
        }
        return album.ValueKind is JsonValueKind.String
            ? album.GetString() ?? string.Empty
            : GetString(album, "name") ?? string.Empty;
    }

    private static string ParseCover(JsonElement track)
    {
        if (TryGetProperty(track, "album", out var album)
            || TryGetProperty(track, "al", out album))
        {
            if (album.ValueKind is JsonValueKind.Object)
            {
                var albumCover = GetString(album, "picUrl")
                    ?? GetString(album, "coverUrl");
                if (!string.IsNullOrWhiteSpace(albumCover))
                {
                    return albumCover;
                }
            }
        }

        return GetString(track, "coverUrl")
            ?? GetString(track, "cover")
            ?? GetString(track, "picUrl")
            ?? string.Empty;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value)
            && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? GetScalarString(
        JsonElement element,
        string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private void EnsureToken()
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException(
                "未配置 Folia Stage Token。");
        }
    }

    private void Remember(PlayerTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            _knownTracks[track.Id] = track;
        }
    }

    private PlayerSnapshot ReadSnapshot()
    {
        lock (_stateSync)
        {
            return _snapshot;
        }
    }

    private PlayerSnapshot UpdateSnapshot(
        bool connected,
        string status,
        PlayerTrack? current)
    {
        lock (_stateSync)
        {
            _snapshot = CreateSnapshot(connected, status, current);
            return _snapshot;
        }
    }

    private static PlayerSnapshot CreateSnapshot(
        bool connected,
        string status,
        PlayerTrack? current)
    {
        return new PlayerSnapshot(
            connected,
            "Folia",
            null,
            "Stage API",
            status,
            current,
            DateTimeOffset.UtcNow);
    }

    private PlayerOperationResult Result(
        OperationOutcome outcome,
        string message)
    {
        return new PlayerOperationResult(
            outcome,
            message,
            ReadSnapshot());
    }

    [GeneratedRegex(
        @"^(?:id\s*=\s*)?(\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeteaseIdPattern();
}
