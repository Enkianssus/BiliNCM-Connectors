using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KugouControlPoc;

namespace UnifiedPlayerControlPoc;

internal sealed class KugouPlayerAdapter : IPlayerAdapter
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly GuardedNextMonitor _nextGuard = new();
    private readonly object _trackSync = new();
    private readonly Dictionary<string, PlayerTrack> _knownTracks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artworkLookups =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _artworkTasks = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private volatile bool _httpSearchFallbackUsed;

    public string Key => "kugou";

    public string DisplayName => "酷狗音乐";

    public string TestedVersion => "20.0.81.27563";

    public PlayerCapabilities Capabilities { get; } = new(
        Search: true,
        PlaySelected: true,
        Previous: true,
        Pause: false,
        Resume: false,
        Toggle: true,
        Next: true,
        InsertNext: true,
        InsertNextLevel: "原生插入 + 错误下一首停止接管守卫");

    public async Task<PlayerSnapshot> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var target = FindTarget();
        if (target is null)
        {
            return new PlayerSnapshot(
                false,
                DisplayName,
                null,
                string.Empty,
                "未连接：没有发现可见酷狗主窗口",
                null,
                DateTimeOffset.Now);
        }

        var endpoint = FindValidatedIpcEndpoint();
        var state =
            await KugouNativeController.ReadPlaybackStateWithIdentityAsync(
                cancellationToken).ConfigureAwait(false);
        var current = ResolveCurrentTrack(state);
        return new PlayerSnapshot(
            true,
            DisplayName,
            target.Value.ProcessId,
            target.Value.Version,
            endpoint is null
                ? "控制窗口已连接；在线点歌 IPC 未通过进程/窗口类校验"
                : $"控制及在线点歌 IPC 已连接（{state.Source}）"
                  + (_httpSearchFallbackUsed
                      ? "；搜索使用 HTTP 兼容回退"
                      : string.Empty)
                  + (string.IsNullOrWhiteSpace(_nextGuard.Status)
                      ? string.Empty
                      : $"；{_nextGuard.Status}"),
            current,
            DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var trimmedQuery = query.Trim();
        var keywordTask = SearchByKeywordAsync(
            trimmedQuery,
            cancellationToken);
        if (!trimmedQuery.All(char.IsAsciiDigit))
        {
            return await keywordTask.ConfigureAwait(false);
        }

        var codeResults = await TryResolveKugouCodeAsync(
            trimmedQuery,
            cancellationToken).ConfigureAwait(false);
        if (codeResults.Count > 0)
        {
            _ = ObserveBackgroundTaskAsync(keywordTask);
            return codeResults;
        }

        return await keywordTask.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PlayerTrack>> SearchByKeywordAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var queryString =
            "/api/v3/search/song"
            + "?format=json"
            + $"&keyword={Uri.EscapeDataString(query)}"
            + "&page=1&pagesize=20&showtype=1";
        using var response = await GetSearchResponseAsync(
            queryString,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<PlayerTrack>();
        foreach (var song in info.EnumerateArray())
        {
            var hash = ReadJsonText(song, "hash").ToUpperInvariant();
            var audioId = ReadJsonLong(song, "audio_id");
            var title = ReadJsonText(song, "songname");
            var artist = ReadJsonText(song, "singername");
            if (string.IsNullOrWhiteSpace(hash)
                || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var track = new PlayerTrack(
                audioId > 0 ? audioId.ToString() : hash,
                title,
                artist,
                ReadJsonText(song, "album_name"),
                song.GetRawText(),
                GetCoverUrl(song));
            results.Add(track);
            RememberTrack(track, hash);
        }

        return results;
    }

    private async Task<IReadOnlyList<PlayerTrack>> TryResolveKugouCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendMixedSearchAsync(
                code,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("lists", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (!ReadJsonText(group, "type").Equals(
                        "song",
                        StringComparison.OrdinalIgnoreCase)
                    || ReadJsonLong(group, "isshareresult") != 1
                    || !group.TryGetProperty("lists", out var songs)
                    || songs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var results = new List<PlayerTrack>();
                foreach (var song in songs.EnumerateArray())
                {
                    var track = CreateMixedSearchTrack(song);
                    if (track is null)
                    {
                        continue;
                    }

                    results.Add(track);
                    RememberTrack(
                        track,
                        ReadJsonTextAny(song, "FileHash", "Hash", "hash"));
                }

                return results;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The value may be a numeric song title, or the unsigned public
            // share endpoint may be temporarily unavailable. Keyword search
            // was already started in parallel and remains the safe fallback.
        }

        return [];
    }

    private async Task<HttpResponseMessage> SendMixedSearchAsync(
        string code,
        CancellationToken cancellationToken)
    {
        const string dfid = "-";
        const string signatureSalt =
            "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA";
        var now = DateTimeOffset.UtcNow;
        var milliseconds = now.ToUnixTimeMilliseconds();
        var clientTime = now.ToUnixTimeSeconds().ToString(
            CultureInfo.InvariantCulture);
        var mid = BigInteger.Parse(
            $"0{Md5Hex(dfid)}",
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture).ToString(
                CultureInfo.InvariantCulture);
        var uuid = Md5Hex($"{dfid}{mid}");
        var parameters = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["ab_tag"] = "0",
            ["ability"] = "511",
            ["albumhide"] = "0",
            ["apiver"] = "22",
            ["area_code"] = "1",
            ["clientver"] = "20125",
            ["cursor"] = "0",
            ["is_gpay"] = "0",
            ["iscorrection"] = "1",
            ["keyword"] = code,
            ["nocollect"] = "0",
            ["osversion"] = "16.5",
            ["platform"] = "IOSFilter",
            ["recver"] = "2",
            ["req_ai"] = "1",
            ["requestid"] =
                $"{Md5Hex($"bdaa53d04e7475feb9024164a47032f9{milliseconds}")}_0",
            ["search_ability"] = "3",
            ["sec_aggre"] = "1",
            ["sec_aggre_bitmap"] = "0",
            ["style_type"] = "3",
            ["tag"] = "em",
            ["appid"] = "3116",
            ["dfid"] = dfid,
            ["mid"] = mid,
            ["uuid"] = uuid,
            ["userid"] = "0",
            ["clienttime"] = clientTime
        };
        var signatureInput = string.Concat(
            parameters
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        parameters["signature"] = Md5Hex(
            $"{signatureSalt}{signatureInput}{signatureSalt}");
        var queryString = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}="
                + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://gateway.kugou.com/v3/search/mixed?{queryString}");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi");
        request.Headers.TryAddWithoutValidation(
            "x-router",
            "complexsearch.kugou.com");
        request.Headers.TryAddWithoutValidation(
            "kg-clienttimems",
            milliseconds.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("dfid", dfid);
        request.Headers.TryAddWithoutValidation("mid", mid);
        request.Headers.TryAddWithoutValidation("clienttime", clientTime);
        request.Headers.TryAddWithoutValidation("kg-rc", "1");
        request.Headers.TryAddWithoutValidation("kg-thash", "5d816a0");
        request.Headers.TryAddWithoutValidation("kg-rec", "1");
        request.Headers.TryAddWithoutValidation(
            "kg-rf",
            "B9EDA08A64250DEFFBCADDEE00F8F25F");
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static PlayerTrack? CreateMixedSearchTrack(JsonElement song)
    {
        var hash = ReadJsonTextAny(song, "FileHash", "Hash", "hash")
            .ToUpperInvariant();
        var audioId = ReadJsonLongAny(
            song,
            "Audioid",
            "audio_id",
            "Scid");
        var title = StripSearchMarkup(
            ReadJsonTextAny(song, "OriSongName", "SongName", "songname"));
        var artist = StripSearchMarkup(
            ReadJsonTextAny(song, "SingerName", "singername"));
        if (string.IsNullOrWhiteSpace(hash)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var durationSeconds = ReadJsonLongAny(
            song,
            "Duration",
            "duration");
        var canonical = new Dictionary<string, object?>
        {
            ["filename"] = StripSearchMarkup(
                ReadJsonTextAny(song, "FileName", "filename")),
            ["hash"] = hash,
            ["filesize"] = ReadJsonLongAny(
                song,
                "FileSize",
                "filesize").ToString(CultureInfo.InvariantCulture),
            ["timelength"] = (durationSeconds * 1000).ToString(
                CultureInfo.InvariantCulture),
            ["duration"] = durationSeconds,
            ["bitrate"] = ReadJsonLongAny(
                song,
                "Bitrate",
                "bitrate").ToString(CultureInfo.InvariantCulture),
            ["mvhash"] = ReadJsonTextAny(song, "MvHash", "mvhash"),
            ["isvip"] = ReadJsonLongAny(song, "IsVip", "isvip"),
            ["privilege"] = ReadJsonLongAny(
                song,
                "Privilege",
                "privilege"),
            ["album_id"] = ReadJsonTextAny(
                song,
                "AlbumID",
                "album_id"),
            ["mixsongid"] = ReadJsonTextAny(
                song,
                "MixSongID",
                "mixsongid") is { Length: > 0 } mixSongId
                ? mixSongId
                : "0",
            ["specialid"] = "0",
            ["songname"] = title,
            ["singername"] = artist,
            ["album_name"] = StripSearchMarkup(
                ReadJsonTextAny(song, "AlbumName", "album_name")),
            ["audio_id"] = audioId
        };
        var track = new PlayerTrack(
            audioId > 0 ? audioId.ToString(CultureInfo.InvariantCulture) : hash,
            title,
            artist,
            StripSearchMarkup(
                ReadJsonTextAny(song, "AlbumName", "album_name")),
            JsonSerializer.Serialize(canonical),
            GetCoverUrl(song));
        return track;
    }

    private static async Task ObserveBackgroundTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The preferred exact-ID result has already completed.
        }
    }

    private static string Md5Hex(string value)
    {
        return Convert.ToHexString(
                MD5.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static string StripSearchMarkup(string value)
    {
        return System.Net.WebUtility.HtmlDecode(
            Regex.Replace(value, "<[^>]+>", string.Empty)).Trim();
    }

    private async Task<HttpResponseMessage> GetSearchResponseAsync(
        string queryString,
        CancellationToken cancellationToken)
    {
        try
        {
            var secureResponse = await _httpClient.GetAsync(
                $"https://mobilecdn.kugou.com{queryString}",
                cancellationToken).ConfigureAwait(false);
            if (secureResponse.IsSuccessStatusCode)
            {
                _httpSearchFallbackUsed = false;
                return secureResponse;
            }

            secureResponse.Dispose();
        }
        catch (HttpRequestException)
        {
            // The legacy mobilecdn endpoint does not provide working TLS on
            // every network. Fall back only for this public, credential-free
            // catalog query; player control itself remains local IPC.
        }

        _httpSearchFallbackUsed = true;
        return await _httpClient.GetAsync(
            $"http://mobilecdn.kugou.com{queryString}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlayerOperationResult> ExecuteAsync(
        PlayerCommand command,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!before.Connected)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "酷狗未连接。",
                    before);
            }

            if (command is PlayerCommand.Previous
                or PlayerCommand.Next
                or PlayerCommand.Toggle)
            {
                var nativeCommand = command switch
                {
                    PlayerCommand.Previous => KugouAppCommand.PreviousTrack,
                    PlayerCommand.Next => KugouAppCommand.NextTrack,
                    _ => KugouAppCommand.PlayPause
                };
                var result = await Task.Run(
                    () => KugouNativeController.SendDirectKugouCommand(
                        nativeCommand,
                        TimeSpan.FromSeconds(6)),
                    cancellationToken).ConfigureAwait(false);
                var after = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Sent)
                {
                    return new PlayerOperationResult(
                        OperationOutcome.Rejected,
                        result.Error ?? "酷狗没有接受内部控制消息。",
                        after);
                }

                if (command == PlayerCommand.Toggle)
                {
                    return new PlayerOperationResult(
                        OperationOutcome.Accepted,
                        "酷狗已接受播放/暂停切换。播放器没有提供可靠状态，未进行盲目 Stop 重试。",
                        after);
                }

                return new PlayerOperationResult(
                    result.TrackChanged
                        ? OperationOutcome.Applied
                        : OperationOutcome.Indeterminate,
                    result.TrackChanged
                        ? $"已观察到切歌：{after.Current?.DisplayName ?? "未知歌曲"}"
                        : result.Error ?? "消息已投递，但没有观察到切歌；未执行 Stop 重试。",
                    after);
            }

            if (command is PlayerCommand.Pause or PlayerCommand.Resume)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Unsupported,
                    "酷狗当前只能安全发送 Toggle，无法保证明确 Pause/Resume。",
                    before);
            }

            if (command is not (PlayerCommand.PlaySelected
                or PlayerCommand.InsertNext)
                || track is null
                || string.IsNullOrWhiteSpace(track.NativeData))
            {
                return new PlayerOperationResult(
                    OperationOutcome.Unsupported,
                    "酷狗适配器不支持该命令，或没有选中有效搜索结果。",
                    before);
            }

            RememberTrack(track);
            var endpoint = FindValidatedIpcEndpoint();
            if (endpoint is null)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "Local\\KuGouDataExchange 公布的窗口未通过 KuGou/TaskListener 校验。",
                    before);
            }

            var playImmediately = command == PlayerCommand.PlaySelected;
            if (playImmediately)
            {
                _nextGuard.Cancel(
                    "下一首守卫已因立即播放其他歌曲而取消");
            }

            var payload = BuildOnlinePayload(track.NativeData, playImmediately);
            var delivery = await Task.Run(
                () => KugouCopyDataTransport.Send(
                    endpoint.Value.Handle,
                    payload,
                    data: 20),
                cancellationToken).ConfigureAwait(false);
            if (!delivery.Accepted)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    delivery.Message,
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            if (!playImmediately)
            {
                var armed = _nextGuard.Arm(
                    before.Current,
                    track,
                    ReadCurrentForGuardAsync,
                    TakeOverGuardedNextAsync,
                    cancellationToken,
                    out var guardMessage);
                return new PlayerOperationResult(
                    armed
                        ? OperationOutcome.Accepted
                        : OperationOutcome.Indeterminate,
                    "酷狗已接受实验队列负载。"
                    + (armed
                        ? $" {guardMessage}"
                        : " 当前歌曲不可识别，守卫未启动。"),
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            var afterPlay = before;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                afterPlay = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (TrackMatches(afterPlay.Current, track))
                {
                    return new PlayerOperationResult(
                        OperationOutcome.Verified,
                        $"已观察到酷狗播放目标歌曲：{track.DisplayName}",
                        afterPlay);
                }
            }

            return new PlayerOperationResult(
                OperationOutcome.Indeterminate,
                "酷狗已接受点歌 IPC，但没有在等待窗口内观察到目标；未执行 Stop 或强制恢复。",
                afterPlay);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCancellation.Cancel();
        Task[] artworkTasks;
        lock (_trackSync)
        {
            artworkTasks = [.. _artworkTasks];
        }
        try
        {
            await Task.WhenAll(artworkTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown cancels any optional background artwork lookup.
        }
        catch
        {
            // Artwork is best-effort and must not block connector shutdown.
        }

        _nextGuard.Dispose();
        _lifetimeCancellation.Dispose();
        _httpClient.Dispose();
        _operationGate.Dispose();
    }

    private Task<PlayerTrack?> ReadCurrentForGuardAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ResolveCurrentTrack(
                KugouNativeController.ReadPlaybackState());
        }, cancellationToken);
    }

    private static async Task<string> TakeOverGuardedNextAsync(
        PlayerTrack target,
        CancellationToken cancellationToken)
    {
        var stopped = await Task.Run(
            () => KugouNativeController.SendDirectKugouCommand(
                KugouAppCommand.Stop,
                TimeSpan.Zero),
            cancellationToken).ConfigureAwait(false);
        if (!stopped.Sent)
        {
            return "下一首接管失败：酷狗没有接受停止命令；"
                   + (stopped.Error ?? "未知错误");
        }

        var endpoint = FindValidatedIpcEndpoint();
        if (endpoint is null)
        {
            return "已停止错误歌曲，但酷狗点歌 IPC 当前不可用。";
        }

        var payload = BuildOnlinePayload(
            target.NativeData,
            playImmediately: true);
        var delivery = await Task.Run(
            () => KugouCopyDataTransport.Send(
                endpoint.Value.Handle,
                payload,
                data: 20),
            cancellationToken).ConfigureAwait(false);
        return delivery.Accepted
            ? $"已停止错误歌曲并切换目标：{target.DisplayName}"
            : $"已停止错误歌曲，但目标播放失败：{delivery.Message}";
    }

    private static (int ProcessId, string Version)? FindTarget()
    {
        var windows = KugouNativeController.InspectWindows();
        var main = windows
            .Where(window =>
                window.ParentHandle is null
                && window.IsVisible
                && window.ClassName.Equals(
                    "kugou_ui",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(window =>
                window.Title.Contains(
                    "酷狗音乐",
                    StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (main is null)
        {
            return null;
        }

        return (main.ProcessId, TryGetVersion(main.ProcessId));
    }

    private static (nint Handle, int ProcessId)? FindValidatedIpcEndpoint()
    {
        var endpoint = KugouNativeController.InspectIpcEndpoint();
        if (endpoint is null
            || !endpoint.ClassName.Equals(
                "TaskListener",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(endpoint.ProcessId);
            return process.ProcessName.Equals(
                "KuGou",
                StringComparison.OrdinalIgnoreCase)
                ? ((nint)endpoint.Handle, endpoint.ProcessId)
                : null;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidOperationException)
        {
            return null;
        }
    }

    private static string TryGetVersion(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : FileVersionInfo.GetVersionInfo(path).FileVersion
                  ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private PlayerTrack? ResolveCurrentTrack(KugouPlaybackState state)
    {
        if (string.IsNullOrWhiteSpace(state.RawTitle))
        {
            return null;
        }

        var id = state.AudioId > 0
            ? state.AudioId.ToString()
            : state.Hash;
        var fallback = new PlayerTrack(
            id,
            state.Title,
            state.Artist,
            string.Empty,
            state.Hash);
        var known = FindKnownTrack(id, state.Hash);
        if (known is not null)
        {
            return known;
        }

        ScheduleArtworkLookup(fallback);
        return fallback;
    }

    private PlayerTrack? FindKnownTrack(params string[] identities)
    {
        lock (_trackSync)
        {
            foreach (var identity in identities)
            {
                if (!string.IsNullOrWhiteSpace(identity)
                    && _knownTracks.TryGetValue(identity, out var track))
                {
                    return track;
                }
            }
        }

        return null;
    }

    private void RememberTrack(PlayerTrack track, params string[] identities)
    {
        lock (_trackSync)
        {
            if (!string.IsNullOrWhiteSpace(track.Id))
            {
                _knownTracks[track.Id] = track;
            }
            foreach (var identity in identities)
            {
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    _knownTracks[identity] = track;
                }
            }
        }
    }

    private void ScheduleArtworkLookup(PlayerTrack current)
    {
        var identity = !string.IsNullOrWhiteSpace(current.Id)
            ? current.Id
            : $"{Normalize(current.Title)}|{Normalize(current.Artist)}";
        lock (_trackSync)
        {
            if (!_artworkLookups.Add(identity))
            {
                return;
            }

            _artworkTasks.Add(ResolveArtworkAsync(
                identity,
                current,
                _lifetimeCancellation.Token));
        }
    }

    private async Task ResolveArtworkAsync(
        string identity,
        PlayerTrack current,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(current.Artist)
                ? current.Title
                : $"{current.Title} {current.Artist}";
            var results = await SearchAsync(query, cancellationToken)
                .ConfigureAwait(false);
            var match = results.FirstOrDefault(candidate =>
                TrackMatches(candidate, current)
                && !string.IsNullOrWhiteSpace(candidate.CoverUrl));
            if (match is not null)
            {
                var enriched = match with { Id = current.Id };
                RememberTrack(enriched, identity);
            }
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown or a cancelled request does not affect playback.
        }
        catch
        {
            // Missing artwork is non-fatal; playback state remains available.
        }
    }

    private static bool TrackMatches(PlayerTrack? actual, PlayerTrack expected)
    {
        if (actual is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(actual.Id)
            && actual.Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Normalize(actual.Title) == Normalize(expected.Title)
            && (string.IsNullOrWhiteSpace(expected.Artist)
                || Normalize(actual.Artist) == Normalize(expected.Artist));
    }

    private static string Normalize(string value)
    {
        return value.Normalize(NormalizationForm.FormKC)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string GetCoverUrl(JsonElement song)
    {
        var coverUrl = string.Empty;
        if (TryGetJsonProperty(song, "trans_param", out var transParam)
            && transParam.ValueKind == JsonValueKind.Object)
        {
            coverUrl = ReadJsonTextAny(
                transParam,
                "union_cover",
                "UnionCover");
        }

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            coverUrl = ReadJsonTextAny(
                song,
                "album_cover",
                "AlbumImage");
        }
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            coverUrl = ReadJsonTextAny(song, "img", "Image");
        }
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return string.Empty;
        }

        coverUrl = coverUrl.Replace(
            "{size}",
            "400",
            StringComparison.OrdinalIgnoreCase);
        if (coverUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{coverUrl}";
        }
        return coverUrl.StartsWith(
            "http://",
            StringComparison.OrdinalIgnoreCase)
            ? $"https://{coverUrl[7..]}"
            : coverUrl;
    }

    private static string BuildOnlinePayload(
        string rawSongJson,
        bool playImmediately)
    {
        using var document = JsonDocument.Parse(rawSongJson);
        var song = document.RootElement;
        var filename = ReadJsonText(song, "filename");
        var songName = ReadJsonText(song, "songname");
        var singerName = ReadJsonText(song, "singername");
        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = string.IsNullOrWhiteSpace(singerName)
                ? songName
                : $"{singerName} - {songName}";
        }

        var duration = ReadJsonLong(song, "timelength");
        if (duration <= 0)
        {
            duration = ReadJsonLong(song, "duration") * 1000;
        }

        var file = new Dictionary<string, object?>
        {
            ["filename"] = filename,
            ["hash"] = ReadJsonText(song, "hash").ToUpperInvariant(),
            ["size"] = ReadJsonText(song, "filesize", "0"),
            ["duration"] = duration.ToString(),
            ["bitrate"] = ReadJsonText(song, "bitrate", "0"),
            ["isfilehead"] = "0",
            ["mvhash"] = ReadJsonText(song, "mvhash"),
            ["mvtrack"] = "0",
            ["mvstate"] = "0",
            ["ismvfilehead"] = "0",
            ["isvip"] = ReadJsonText(song, "isvip", "0"),
            ["privilege"] = ReadJsonText(song, "privilege", "0"),
            ["album_id"] = ReadJsonText(song, "album_id"),
            ["scid"] = "0",
            ["mixsongid"] = ReadJsonText(song, "mixsongid", "0"),
            ["special_id"] = ReadJsonText(song, "specialid", "0"),
            ["encrypt"] = "-1",
            ["songname"] = songName,
            ["singerinfo"] = Array.Empty<object>(),
            ["album_name"] = ReadJsonText(song, "album_name"),
            ["quality"] = "0",
            ["vip_icon"] = "0",
            ["songdescription"] = string.Empty
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Source"] = "UnifiedPlayerControlPoc",
            ["SourceFile"] = string.Empty,
            ["SourcePath"] = string.Empty,
            ["ChargePath"] = string.Empty,
            ["ClassName"] = string.Empty,
            ["Files"] = new[] { file },
            ["Count"] = "1",
            ["ListId"] = string.Empty,
            ["DownloadPath"] = string.Empty,
            ["Type"] = string.Empty,
            ["From"] = "UnifiedPlayerControlPoc",
            ["LocalListId"] = string.Empty,
            ["CloudListId"] = string.Empty,
            ["NoPlayAds"] = 1,
            ["QueueInfo"] = new Dictionary<string, string>
            {
                ["Play"] = playImmediately ? "1" : "0",
                ["PlayAll"] = "0",
                ["Clear"] = "0",
                ["Insert"] = playImmediately ? "0" : "1",
                ["Force"] = playImmediately ? "1" : "0",
                ["IsMV"] = "0",
                ["Index"] = "0",
                ["AddToDefaultList"] = "1",
                ["climax"] = "0"
            },
            ["QueueSource"] = string.Empty
        });
    }

    private static string ReadJsonText(
        JsonElement element,
        string name,
        string defaultValue = "")
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? defaultValue,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => defaultValue
        };
    }

    private static string ReadJsonTextAny(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetJsonProperty(element, name, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static long ReadJsonLongAny(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetJsonProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static bool TryGetJsonProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
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

    private static long ReadJsonLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.Number
                ? value.TryGetInt64(out var number)
                : long.TryParse(value.GetString(), out number))
            ? number
            : 0;
    }
}

internal sealed record KugouCopyDataResult(
    bool Accepted,
    nuint ReceiverResult,
    string Message);

internal static class KugouCopyDataTransport
{
    private const uint WmCopyData = 0x004A;

    public static KugouCopyDataResult Send(
        nint target,
        string payload,
        nuint data)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var dataPointer = Marshal.AllocHGlobal(bytes.Length);
        var structPointer = nint.Zero;
        try
        {
            Marshal.Copy(bytes, 0, dataPointer, bytes.Length);
            var copyData = new CopyDataStruct
            {
                Data = data,
                ByteCount = checked((uint)bytes.Length),
                DataPointer = dataPointer
            };
            structPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<CopyDataStruct>());
            Marshal.StructureToPtr(copyData, structPointer, false);
            var delivered = SendMessageTimeout(
                target,
                WmCopyData,
                nint.Zero,
                structPointer,
                SendMessageTimeoutFlags.Block
                | SendMessageTimeoutFlags.AbortIfHung,
                1500,
                out var receiverResult);
            var accepted = delivered != nint.Zero && receiverResult != 0;
            return new KugouCopyDataResult(
                accepted,
                receiverResult,
                accepted
                    ? $"酷狗 IPC 已接受负载（receiver={receiverResult}）。"
                    : $"酷狗 IPC 超时或拒绝负载（receiver={receiverResult}, Win32={Marshal.GetLastWin32Error()}）。");
        }
        finally
        {
            if (structPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(structPointer);
            }

            Marshal.FreeHGlobal(dataPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public nuint Data;
        public uint ByteCount;
        public nint DataPointer;
    }

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        Block = 0x0001,
        AbortIfHung = 0x0002
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        SendMessageTimeoutFlags flags,
        uint timeoutMilliseconds,
        out nuint result);
}
