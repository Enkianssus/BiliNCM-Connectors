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
    private const string AllowHttpSearchFallbackEnvironmentVariable =
        "BILINCM_KUGOU_ALLOW_HTTP_SEARCH_FALLBACK";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly bool _allowHttpSearchFallback;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly KugouEventMonitor _eventMonitor = new();
    private readonly KugouGuardedNextMonitor _nextGuard;
    private readonly object _pendingNextSync = new();
    private readonly object _trackSync = new();
    private readonly Dictionary<string, PlayerTrack> _knownTracks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artworkLookups =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _artworkRetryAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _artworkLookupOrder = new();
    private readonly List<Task> _artworkTasks = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private PendingKugouNext? _pendingNext;
    private DateTimeOffset? _pendingTargetObservedAt;
    private volatile bool _httpSearchFallbackUsed;
    private volatile string _httpSearchStatus =
        "搜索仅使用 HTTPS；明文 HTTP 回退未启用";
    private volatile string _anchorResetStatus = string.Empty;

    public KugouPlayerAdapter()
    {
        _nextGuard = new KugouGuardedNextMonitor(
            _eventMonitor,
            () => _eventMonitor.NotifySnapshotInvalidated());
        _allowHttpSearchFallback = IsEnvironmentVariableEnabled(
            AllowHttpSearchFallbackEnvironmentVariable);
        _httpSearchStatus = _allowHttpSearchFallback
            ? $"搜索优先使用 HTTPS；已显式启用明文 HTTP 兼容回退（{AllowHttpSearchFallbackEnvironmentVariable}=1）"
            : $"搜索仅使用 HTTPS；明文 HTTP 回退已禁用（设置 {AllowHttpSearchFallbackEnvironmentVariable}=1 可显式启用）";
    }

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
        InsertNextLevel: "原生插入 + 上一首重置锚点的有界兜底");

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
                "未连接：没有发现酷狗主窗口",
                null,
                DateTimeOffset.Now);
        }

        var endpoint = FindValidatedIpcEndpoint();
        var state =
            await KugouNativeController.ReadPlaybackStateWithIdentityAsync(
                cancellationToken).ConfigureAwait(false);
        var current = ResolveCurrentTrack(state);
        ClearPendingNextIfPlaying(current);
        var pendingNext = GetPendingNextTrack();
        return new PlayerSnapshot(
            true,
            DisplayName,
            target.Value.ProcessId,
            target.Value.Version,
            endpoint is null
                ? "控制窗口已连接；在线点歌 IPC 未通过进程/窗口类校验"
                : $"控制及在线点歌 IPC 已连接（{state.Source}）"
                  + (_httpSearchFallbackUsed
                      ? $"；搜索使用明文 HTTP 兼容回退（{AllowHttpSearchFallbackEnvironmentVariable}=1）"
                      : $"；{_httpSearchStatus}")
                   + (string.IsNullOrWhiteSpace(_nextGuard.Status)
                       ? string.Empty
                       : $"；{_nextGuard.Status}")
                   + (string.IsNullOrWhiteSpace(_anchorResetStatus)
                       ? string.Empty
                       : $"；{_anchorResetStatus}"),
            current,
            DateTimeOffset.Now,
            pendingNext,
            pendingNext is null
                ? string.Empty
                : "KuGou native InsertNext transaction");
    }

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var classified = SongQueryPolicy.ParseKugou(query);
        var keywordTask = SearchByKeywordAsync(
            classified.Value,
            cancellationToken);
        Task<IReadOnlyList<PlayerTrack>>? exactTask = classified.Kind switch
        {
            KugouSongQueryKind.Chain => TryResolvePermanentShareChainAsync(
                classified.Value,
                cancellationToken),
            KugouSongQueryKind.ShareCode => TryResolveKugouCodeAsync(
                classified.Value,
                cancellationToken),
            _ => null
        };

        if (exactTask is null)
        {
            return await keywordTask.ConfigureAwait(false);
        }

        var exactResults = await exactTask.ConfigureAwait(false);
        if (exactResults.Count > 0)
        {
            _ = ObserveBackgroundTaskAsync(keywordTask);
            return exactResults;
        }

        return await keywordTask.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PlayerTrack>>
        TryResolvePermanentShareChainAsync(
            string chain,
            CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://m.kugou.com/share/song.html?chain="
                + Uri.EscapeDataString(chain));
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 "
                + "Chrome/122.0 Mobile Safari/537.36");
            request.Headers.Referrer = new Uri("https://m.kugou.com/");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            var phpParamMatch = Regex.Match(
                html,
                @"var\s+phpParam\s*=\s*(\{.*?\})\s*;",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!phpParamMatch.Success)
            {
                return [];
            }

            using var document = JsonDocument.Parse(
                phpParamMatch.Groups[1].Value);
            var root = document.RootElement;
            var song = root;
            if (root.TryGetProperty("song_info", out var songInfo)
                && songInfo.ValueKind == JsonValueKind.Object
                && songInfo.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                song = data;
            }

            var track = CreateMixedSearchTrack(song);
            if (track is null)
            {
                return [];
            }

            RememberTrack(
                track,
                ReadJsonTextAny(song, "FileHash", "Hash", "hash"));
            return [track];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
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
        var secure = await TryGetSecureSearchResponseAsync(
            queryString,
            cancellationToken).ConfigureAwait(false);
        var secureFailure = secure.Failure;
        Exception? secureException = secure.Exception;
        if (secure.Response is not null)
        {
            using var response = secure.Response;
            try
            {
                var parsed = await ParseMobileSearchResponseAsync(
                    response,
                    cancellationToken).ConfigureAwait(false);
                if (parsed.Recognized)
                {
                    _httpSearchFallbackUsed = false;
                    _httpSearchStatus = "搜索使用 HTTPS";
                    return parsed.Results;
                }

                secureFailure = "HTTPS 响应缺少可解析搜索结果";
            }
            catch (JsonException exception)
            {
                secureFailure = $"HTTPS 响应解析失败（{exception.Message}）";
                secureException = exception;
            }
        }

        var mixed = await TrySearchByMixedAsync(
            query,
            cancellationToken).ConfigureAwait(false);
        if (mixed.Succeeded)
        {
            _httpSearchFallbackUsed = false;
            _httpSearchStatus = "搜索使用 gateway.kugou.com HTTPS mixed 兼容路径";
            return mixed.Results;
        }

        var mixedFailure = string.IsNullOrWhiteSpace(mixed.Failure)
            ? "gateway HTTPS mixed 失败"
            : mixed.Failure;
        var fallbackException = secureException ?? mixed.Exception;
        if (!_allowHttpSearchFallback)
        {
            _httpSearchFallbackUsed = false;
            _httpSearchStatus = $"{secureFailure}；{mixedFailure}；"
                + "已拒绝明文 HTTP 回退（设置 "
                + $"{AllowHttpSearchFallbackEnvironmentVariable}=1 可显式启用）";
            throw new HttpRequestException(
                $"酷狗搜索的 HTTPS 接口不可用：{secureFailure}；{mixedFailure}。"
                + "为保护搜索词，已拒绝明文 HTTP 回退。"
                + $"如明确接受风险，请设置 {AllowHttpSearchFallbackEnvironmentVariable}=1 后重试。",
                fallbackException);
        }

        _httpSearchFallbackUsed = true;
        _httpSearchStatus = $"{secureFailure}；{mixedFailure}；正在使用已显式启用的明文 HTTP 兼容回退";
        try
        {
            using var response = await _httpClient.GetAsync(
                $"http://mobilecdn.kugou.com{queryString}",
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var parsed = await ParseMobileSearchResponseAsync(
                response,
                cancellationToken).ConfigureAwait(false);
            return parsed.Recognized ? parsed.Results : [];
        }
        catch (HttpRequestException exception)
        {
            _httpSearchStatus = $"{secureFailure}；{mixedFailure}；"
                + $"明文 HTTP 兼容回退失败（{exception.Message}）";
            throw;
        }
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
                        "recommend",
                        StringComparison.OrdinalIgnoreCase)
                    || !group.TryGetProperty("lists", out var recommendations)
                    || recommendations.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var results = recommendations
                    .EnumerateArray()
                    .Select(CreateCodeRecommendationTrack)
                    .Where(track => track is not null)
                    .Cast<PlayerTrack>()
                    .ToArray();
                foreach (var track in results)
                {
                    using var nativeData = JsonDocument.Parse(track.NativeData);
                    RememberTrack(
                        track,
                        ReadJsonText(nativeData.RootElement, "hash"));
                }

                if (results.Length > 0)
                {
                    return results;
                }
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

    private static PlayerTrack? CreateCodeRecommendationTrack(
        JsonElement recommendation)
    {
        if (!recommendation.TryGetProperty("list", out var song)
            || song.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hash = ReadJsonTextAny(song, "FileHash", "Hash", "hash")
            .ToUpperInvariant();
        var albumAudioId = ReadJsonLongAny(
            song,
            "MixSongID",
            "mixsongid",
            "album_audio_id");
        var filename = StripSearchMarkup(
            ReadJsonTextAny(song, "FileName", "filename", "fileName"));
        var title = StripSearchMarkup(
            ReadJsonTextAny(recommendation, "title", "name"));
        var artist = string.Empty;
        if (recommendation.TryGetProperty("info", out var info)
            && info.ValueKind == JsonValueKind.Object)
        {
            artist = StripSearchMarkup(
                ReadJsonTextAny(info, "username", "author_name"));
        }

        if ((string.IsNullOrWhiteSpace(title)
             || string.IsNullOrWhiteSpace(artist))
            && !string.IsNullOrWhiteSpace(filename))
        {
            var separator = filename.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0)
            {
                if (string.IsNullOrWhiteSpace(artist))
                {
                    artist = filename[..separator].Trim();
                }
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = filename[(separator + 3)..].Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(hash)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var durationSeconds = ReadJsonLongAny(
            song,
            "Duration",
            "duration",
            "timeLength");
        var canonical = new Dictionary<string, object?>
        {
            ["filename"] = filename,
            ["hash"] = hash,
            ["filesize"] = ReadJsonLongAny(
                song,
                "FileSize",
                "filesize",
                "fileSize").ToString(CultureInfo.InvariantCulture),
            ["timelength"] = (durationSeconds * 1000).ToString(
                CultureInfo.InvariantCulture),
            ["duration"] = durationSeconds,
            ["bitrate"] = ReadJsonLongAny(
                song,
                "Bitrate",
                "bitrate",
                "bitRate").ToString(CultureInfo.InvariantCulture),
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
            ["mixsongid"] = albumAudioId > 0
                ? albumAudioId.ToString(CultureInfo.InvariantCulture)
                : "0",
            ["specialid"] = "0",
            ["songname"] = title,
            ["singername"] = artist,
            ["album_name"] = ReadJsonTextAny(
                song,
                "AlbumName",
                "album_name"),
            ["audio_id"] = ReadJsonLongAny(
                song,
                "Audioid",
                "audio_id",
                "Scid")
        };
        var cover = GetCoverUrl(song);
        if (string.IsNullOrWhiteSpace(cover))
        {
            cover = GetCoverUrl(recommendation);
        }

        return new PlayerTrack(
            albumAudioId > 0
                ? albumAudioId.ToString(CultureInfo.InvariantCulture)
                : hash,
            title,
            artist,
            ReadJsonTextAny(song, "AlbumName", "album_name"),
            JsonSerializer.Serialize(canonical),
            cover);
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
            ReadJsonTextAny(
                song,
                "OriSongName",
                "SongName",
                "songname",
                "songName"));
        var artist = StripSearchMarkup(
            ReadJsonTextAny(
                song,
                "SingerName",
                "singername",
                "author_name"));
        if (string.IsNullOrWhiteSpace(hash)
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var durationSeconds = ReadJsonLongAny(
            song,
            "Duration",
            "duration",
            "timeLength");
        var canonical = new Dictionary<string, object?>
        {
            ["filename"] = StripSearchMarkup(
                ReadJsonTextAny(song, "FileName", "filename", "fileName")),
            ["hash"] = hash,
            ["filesize"] = ReadJsonLongAny(
                song,
                "FileSize",
                "filesize",
                "fileSize").ToString(CultureInfo.InvariantCulture),
            ["timelength"] = (durationSeconds * 1000).ToString(
                CultureInfo.InvariantCulture),
            ["duration"] = durationSeconds,
            ["bitrate"] = ReadJsonLongAny(
                song,
                "Bitrate",
                "bitrate",
                "bitRate").ToString(CultureInfo.InvariantCulture),
            ["mvhash"] = ReadJsonTextAny(song, "MvHash", "mvhash"),
            ["isvip"] = ReadJsonLongAny(song, "IsVip", "isvip"),
            ["privilege"] = ReadJsonLongAny(
                song,
                "Privilege",
                "privilege"),
            ["album_id"] = ReadJsonTextAny(
                song,
                "AlbumID",
                "album_id",
                "albumid",
                "req_albumid"),
            ["mixsongid"] = ReadJsonTextAny(
                song,
                "MixSongID",
                "mixsongid",
                "album_audio_id") is { Length: > 0 } mixSongId
                ? mixSongId
                : "0",
            ["specialid"] = "0",
            ["songname"] = title,
            ["singername"] = artist,
            ["album_name"] = StripSearchMarkup(
                ReadJsonTextAny(song, "AlbumName", "album_name", "albumName")),
            ["audio_id"] = audioId
        };
        var track = new PlayerTrack(
            audioId > 0 ? audioId.ToString(CultureInfo.InvariantCulture) : hash,
            title,
            artist,
            StripSearchMarkup(
                ReadJsonTextAny(song, "AlbumName", "album_name", "albumName")),
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

    private async Task<(
        HttpResponseMessage? Response,
        string Failure,
        Exception? Exception)> TryGetSecureSearchResponseAsync(
        string queryString,
        CancellationToken cancellationToken)
    {
        var secureFailure = "HTTPS 请求未返回成功响应";
        try
        {
            var secureResponse = await _httpClient.GetAsync(
                $"https://mobilecdn.kugou.com{queryString}",
                cancellationToken).ConfigureAwait(false);
            if (secureResponse.IsSuccessStatusCode)
            {
                return (secureResponse, string.Empty, null);
            }

            secureFailure = $"HTTPS 返回 {(int)secureResponse.StatusCode}"
                + (string.IsNullOrWhiteSpace(secureResponse.ReasonPhrase)
                    ? string.Empty
                    : $" {secureResponse.ReasonPhrase}");
            secureResponse.Dispose();
            return (null, secureFailure, null);
        }
        catch (HttpRequestException exception)
        {
            secureFailure = $"HTTPS 请求失败（{exception.Message}）";
            return (null, secureFailure, exception);
        }
    }

    private async Task<(
        bool Recognized,
        IReadOnlyList<PlayerTrack> Results)> ParseMobileSearchResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Array)
        {
            return (false, Array.Empty<PlayerTrack>());
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

        return (true, results);
    }

    private async Task<(
        bool Succeeded,
        IReadOnlyList<PlayerTrack> Results,
        string Failure,
        Exception? Exception)> TrySearchByMixedAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendMixedSearchAsync(
                query,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = $"gateway HTTPS mixed 返回 {(int)response.StatusCode}"
                    + (string.IsNullOrWhiteSpace(response.ReasonPhrase)
                        ? string.Empty
                        : $" {response.ReasonPhrase}");
                return (
                    false,
                    Array.Empty<PlayerTrack>(),
                    failure,
                    null);
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
                return (
                    false,
                    Array.Empty<PlayerTrack>(),
                    "gateway HTTPS mixed 响应缺少可解析结果",
                    null);
            }

            return (
                true,
                ParseMixedKeywordTracks(groups),
                string.Empty,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return (
                false,
                Array.Empty<PlayerTrack>(),
                $"gateway HTTPS mixed 请求失败（{exception.Message}）",
                exception);
        }
        catch (JsonException exception)
        {
            return (
                false,
                Array.Empty<PlayerTrack>(),
                $"gateway HTTPS mixed 响应解析失败（{exception.Message}）",
                exception);
        }
        catch (Exception exception)
        {
            return (
                false,
                Array.Empty<PlayerTrack>(),
                $"gateway HTTPS mixed 失败（{exception.Message}）",
                exception);
        }
    }

    private IReadOnlyList<PlayerTrack> ParseMixedKeywordTracks(
        JsonElement groups)
    {
        var results = new List<PlayerTrack>();
        foreach (var group in groups.EnumerateArray())
        {
            if (!ReadJsonText(group, "type").Equals(
                    "song",
                    StringComparison.OrdinalIgnoreCase)
                || !group.TryGetProperty("lists", out var songs)
                || songs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

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
        }

        if (results.Count > 0)
        {
            return results;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!ReadJsonText(group, "type").Equals(
                    "recommend",
                    StringComparison.OrdinalIgnoreCase)
                || !group.TryGetProperty("lists", out var recommendations)
                || recommendations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var recommendation in recommendations.EnumerateArray())
            {
                var track = CreateCodeRecommendationTrack(recommendation);
                if (track is null)
                {
                    continue;
                }

                results.Add(track);
                using var nativeData = JsonDocument.Parse(track.NativeData);
                RememberTrack(
                    track,
                    ReadJsonText(nativeData.RootElement, "hash"));
            }
        }

        return results;
    }

    private static bool IsEnvironmentVariableEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(
        KugouCopyDataResult Delivery,
        string AnchorResetMessage)> SendInsertNextAsync(
        (nint Handle, int ProcessId) endpoint,
        string rawSongJson,
        int? currentProcessId,
        bool tryAnchorReset,
        CancellationToken cancellationToken)
    {
        var anchorResetMessage = string.Empty;
        if (tryAnchorReset)
        {
            var reset = await TryResetAnchorAsync(
                currentProcessId,
                cancellationToken).ConfigureAwait(false);
            anchorResetMessage = reset.Message;
        }

        var payload = BuildInsertNextPayload(rawSongJson);
        var delivery = await Task.Run(
            () => KugouCopyDataTransport.Send(
                endpoint.Handle,
                payload,
                data: 20),
            cancellationToken).ConfigureAwait(false);
        return (delivery, anchorResetMessage);
    }

    private async Task<KugouAnchorResetAttempt> TryResetAnchorAsync(
        int? currentProcessId,
        CancellationToken cancellationToken)
    {
        if (currentProcessId is null or <= 0)
        {
            var skipped = KugouAnchorResetAttempt.Skipped(
                "无法锁定酷狗目标进程 PID；已回退旧兼容插入逻辑。请更新酷狗连接器。");
            SetAnchorResetStatus(skipped.Message);
            return skipped;
        }

        KugouAnchorResetAttempt attempt;
        try
        {
            // The native validation is bounded by the resolver and reset
            // thread timeouts. It must not be allowed to cancel the caller
            // between allocation and the finally-based VirtualFreeEx.
            attempt = await Task.Run(
                () => KugouAnchorHistoryReset.TryReset(currentProcessId.Value),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            attempt = new KugouAnchorResetAttempt(
                false,
                false,
                KugouAnchorResetProfilePolicy.BuildFailurePrompt(
                    string.Empty,
                    exception.Message),
                string.Empty,
                string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetAnchorResetStatus(attempt.Message);
        return attempt;
    }

    private void SetAnchorResetStatus(string message)
    {
        if (string.Equals(
                _anchorResetStatus,
                message,
                StringComparison.Ordinal))
        {
            return;
        }

        _anchorResetStatus = message;
        _eventMonitor.NotifySnapshotInvalidated();
    }

    private static string AnchorResetSuffix(string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $" 锚点状态：{message}";
    }

    private static string AppendAnchorResetMessage(
        string message,
        string anchorResetMessage)
    {
        return message + AnchorResetSuffix(anchorResetMessage);
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

            if (command == PlayerCommand.ArmNextGuard && track is not null)
            {
                RememberTrack(track);
                var armed = _nextGuard.Arm(
                    before.Current,
                    track,
                    ReadCurrentForGuardAsync,
                    TakeOverGuardedNextAsync,
                    _lifetimeCancellation.Token,
                    out var guardMessage);
                return new PlayerOperationResult(
                    armed
                        ? OperationOutcome.Accepted
                        : OperationOutcome.Rejected,
                    armed
                        ? $"未重复插入酷狗队列；{guardMessage}"
                        : "当前歌曲不可识别，无法只更新下一首兜底守卫。",
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

            var advanceImmediately = command == PlayerCommand.PlaySelected;
            var displacedPending = advanceImmediately
                ? GetDifferentPendingNext(track)
                : null;
            var alreadyInserted = advanceImmediately && HasPendingNext(track);
            if (advanceImmediately)
            {
                _nextGuard.Cancel(
                    "下一首守卫已因立即播放其他歌曲而取消");

                // The known-good 1.5.x path never pauses or performs a
                // Next -> Previous round trip before insertion. Those extra
                // transitions expose KuGou's transient queue state to the host
                // and can consume the request that was already inserted next.
                if (TrackMatches(before.Current, track))
                {
                    ClearPendingNext(track);
                    RestorePendingNext(displacedPending);
                    return new PlayerOperationResult(
                        OperationOutcome.Verified,
                        $"目标已经是当前歌曲，未重复插入或切歌：{track.DisplayName}",
                        before);
                }
            }

            // KuGou's Play=1/Insert=0/Force=1 payload rebuilds or appends to
            // the player's queue. Always insert exactly one track after the
            // current item. "Play selected" is implemented by advancing to
            // that newly inserted item with KuGou's targeted Next command.
            var anchorResetMessage = string.Empty;
            if (!alreadyInserted)
            {
                var insertion = await SendInsertNextAsync(
                    endpoint.Value,
                    track.NativeData,
                    endpoint.Value.ProcessId,
                    tryAnchorReset: true,
                    cancellationToken).ConfigureAwait(false);
                var delivery = insertion.Delivery;
                anchorResetMessage = insertion.AnchorResetMessage;
                if (!delivery.Accepted)
                {
                    return new PlayerOperationResult(
                        OperationOutcome.Rejected,
                        AppendAnchorResetMessage(
                            delivery.Message,
                            anchorResetMessage),
                        await ProbeAsync(cancellationToken).ConfigureAwait(false));
                }

                RememberPendingNext(track);
            }

            if (!advanceImmediately)
            {
                // Match the observed-good 1.5.x timing. If KuGou applies the
                // asynchronous queue mutation late, the guard performs one
                // bounded takeover instead of pre-emptively changing tracks.
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                var armed = _nextGuard.Arm(
                    before.Current,
                    track,
                    ReadCurrentForGuardAsync,
                    TakeOverGuardedNextAsync,
                    _lifetimeCancellation.Token,
                    out var guardMessage);
                return new PlayerOperationResult(
                    armed
                        ? OperationOutcome.Accepted
                        : OperationOutcome.Indeterminate,
                    (alreadyInserted
                        ? "酷狗目标已存在于待切换事务中，本次没有重复插入。"
                        : "酷狗已将目标歌曲插入当前歌曲之后。")
                    + (armed
                        ? $" {guardMessage}"
                        : " 当前歌曲不可识别，守卫未启动。")
                    + AnchorResetSuffix(anchorResetMessage),
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            // Arm before the one and only Next command. This is the old stable
            // transaction: insert once -> arm once -> advance once. It avoids
            // the pause/round-trip sequence that produced transient track
            // events and removed a previously inserted request.
            var guardArmed = _nextGuard.Arm(
                before.Current,
                track,
                ReadCurrentForGuardAsync,
                TakeOverGuardedNextAsync,
                _lifetimeCancellation.Token,
                out _);
            await Task.Delay(
                alreadyInserted ? 20 : 60,
                cancellationToken).ConfigureAwait(false);
            var advance = await SendDirectCommandAsync(
                KugouAppCommand.NextTrack,
                TimeSpan.FromSeconds(6),
                cancellationToken).ConfigureAwait(false);
            if (!advance.Sent)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Indeterminate,
                    "酷狗已插入目标，但没有接受本次唯一的下一首命令；"
                    + (guardArmed
                        ? "守卫仍在等待自然切歌。"
                        : "当前歌曲不可识别，守卫未启动。")
                    + (string.IsNullOrWhiteSpace(advance.Error)
                        ? string.Empty
                        : $" {advance.Error}")
                    + AnchorResetSuffix(anchorResetMessage),
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            // KuGou's old insert path keeps the displaced native next row
            // behind the immediate request. Restore only connector bookkeeping;
            // never send a second WM_COPYDATA payload for the displaced row.
            if (displacedPending is not null)
            {
                RestorePendingNext(displacedPending);
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            var afterPlay = before;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                afterPlay = await ProbeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!TrackMatches(afterPlay.Current, track))
                {
                    continue;
                }

                if (displacedPending is null)
                {
                    ClearPendingNext(track);
                }
                _nextGuard.Cancel(
                    $"立即播放已正确命中：{track.DisplayName}");
                return new PlayerOperationResult(
                    OperationOutcome.Verified,
                    (displacedPending is null
                        ? $"酷狗已插入一次并切换到目标：{track.DisplayName}"
                        : $"酷狗已切换到 {track.DisplayName}；原来的下一首 {displacedPending.Target.DisplayName} 仍保留在其后。")
                    + AnchorResetSuffix(anchorResetMessage),
                    afterPlay);
            }

            return new PlayerOperationResult(
                OperationOutcome.Indeterminate,
                "酷狗已插入目标并只发送一次下一首，但未在等待窗口内确认命中；"
                + (guardArmed
                    ? "守卫会继续检查实际切歌结果。"
                    : "当前歌曲不可识别，守卫未启动。")
                + AnchorResetSuffix(anchorResetMessage),
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
        await _eventMonitor.DisposeAsync().ConfigureAwait(false);
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

    private async Task<bool> WaitForTrackStableAsync(
        PlayerTrack target,
        TimeSpan timeout,
        TimeSpan stableFor,
        CancellationToken cancellationToken)
    {
        await using var subscription = _eventMonitor.Subscribe();
        await _eventMonitor.EnsureStartedAsync().ConfigureAwait(false);
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var waitToken = timeoutCancellation.Token;
        var initialSettleReadPending = true;

        try
        {
            while (true)
            {
                var current = await ReadCurrentForGuardAsync(waitToken)
                    .ConfigureAwait(false);
                if (TrackMatches(current, target))
                {
                    await Task.Delay(stableFor, waitToken)
                        .ConfigureAwait(false);
                    current = await ReadCurrentForGuardAsync(waitToken)
                        .ConfigureAwait(false);
                    if (TrackMatches(current, target))
                    {
                        return true;
                    }
                }

                if (initialSettleReadPending)
                {
                    initialSettleReadPending = false;
                    await Task.Delay(50, waitToken).ConfigureAwait(false);
                    continue;
                }

                if (!await subscription.Reader.WaitToReadAsync(waitToken)
                        .ConfigureAwait(false))
                {
                    return false;
                }
                while (subscription.Reader.TryRead(out _))
                {
                    // Coalesce the current title/INI event burst.
                }
                await Task.Delay(25, waitToken).ConfigureAwait(false);
                while (subscription.Reader.TryRead(out _))
                {
                    // Drain writes raised while the INI state settled.
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<string> TakeOverGuardedNextAsync(
        PlayerTrack target,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecoverGuardedNextCoreAsync(
                target,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<string> AdvancePendingNextWithoutReinsertAsync(
        PlayerTrack target,
        CancellationToken cancellationToken)
    {
        if (!HasPendingNext(target))
        {
            return "没有可确认的原生下一首事务；为避免误切或重复插歌，已停止兜底。";
        }

        var paused = await SendDirectCommandAsync(
            KugouAppCommand.PlayPause,
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
        if (!paused.Sent)
        {
            return "无法先暂停酷狗；为避免连续切歌，已取消本次兜底。"
                + (paused.Error ?? string.Empty);
        }

        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentForGuardAsync(cancellationToken)
            .ConfigureAwait(false);
        if (TrackMatches(current, target))
        {
            ClearPendingNext(target);
            var resumed = await SendDirectCommandAsync(
                KugouAppCommand.PlayPause,
                TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false);
            return resumed.Sent
                ? $"暂停后确认目标已经命中，已恢复播放：{target.DisplayName}"
                : $"目标已经命中，但恢复播放命令未被接受：{target.DisplayName}";
        }

        // InsertNext was already accepted before the guard was armed. Send one
        // and only one Next command; never create another queue row here.
        var advance = await SendDirectCommandAsync(
            KugouAppCommand.NextTrack,
            TimeSpan.FromMilliseconds(800),
            cancellationToken).ConfigureAwait(false);
        if (!advance.Sent)
        {
            return "酷狗没有接受唯一的一次下一首命令；未重复插入，也未继续切歌。"
                + (advance.Error ?? string.Empty);
        }

        if (await WaitForTrackStableAsync(
                target,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(350),
                cancellationToken).ConfigureAwait(false))
        {
            ClearPendingNext(target);
            return $"已暂停错误歌曲并只切换一次到目标：{target.DisplayName}";
        }

        return $"已执行一次有序兜底但未确认目标；为避免连续向下切歌，已停止：{target.DisplayName}";
    }

    private async Task<string> RecoverGuardedNextCoreAsync(
        PlayerTrack target,
        CancellationToken cancellationToken)
    {
        if (!HasPendingNext(target))
        {
            return "没有可确认的待切换事务；为避免盲目连续切歌，已停止兜底。";
        }

        // The guard is entered only after KuGou has already changed to a wrong
        // song. Previous returns to the original song and, unlike Next or a
        // natural transition, resets KuGou's persistent insertion anchor.
        // The remaining transaction is deliberately bounded: pause once,
        // insert once, and advance once. There is no retry loop.
        var returned = await SendDirectCommandAsync(
            KugouAppCommand.PreviousTrack,
            TimeSpan.FromMilliseconds(900),
            cancellationToken).ConfigureAwait(false);
        if (!returned.Sent)
        {
            return "检测到错误下一首，但酷狗没有接受上一首重置锚点命令；"
                   + "为避免连续跳歌，已取消本次兜底。"
                   + (returned.Error ?? string.Empty);
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentForGuardAsync(cancellationToken)
            .ConfigureAwait(false);
        if (TrackMatches(current, target))
        {
            ClearPendingNext(target);
            return $"上一首已直接回到目标：{target.DisplayName}";
        }

        var stopped = await SendDirectCommandAsync(
            KugouAppCommand.PlayPause,
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
        if (!stopped.Sent)
        {
            return "已用上一首回到原曲并重置锚点，但无法暂停酷狗；"
                   + "为避免插入期间漏音，没有继续操作。"
                   + (stopped.Error ?? string.Empty);
        }

        await Task.Delay(140, cancellationToken).ConfigureAwait(false);

        var endpoint = FindValidatedIpcEndpoint();
        if (endpoint is null)
        {
            return "已经暂停错误歌曲，但酷狗点歌 IPC 当前不可用；没有继续切歌。";
        }

        // Previous above is the deliberately bounded legacy anchor reset.
        // Do not run the no-track profile a second time in this recovery path.
        var insertion = await SendInsertNextAsync(
            endpoint.Value,
            target.NativeData,
            currentProcessId: endpoint.Value.ProcessId,
            tryAnchorReset: false,
            cancellationToken).ConfigureAwait(false);
        var delivery = insertion.Delivery;
        if (!delivery.Accepted)
        {
            return "已经暂停错误歌曲，但重新插入目标失败；没有继续切歌。"
                   + delivery.Message
                   + AnchorResetSuffix(insertion.AnchorResetMessage);
        }

        RememberPendingNext(target);
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);

        var advance = await SendDirectCommandAsync(
            KugouAppCommand.NextTrack,
            TimeSpan.FromMilliseconds(800),
            cancellationToken).ConfigureAwait(false);
        if (!advance.Sent)
        {
            return "已经暂停并重新插入目标，但酷狗没有接受唯一一次下一首命令；"
                   + "为避免越过歌曲，没有重试。"
                   + (advance.Error ?? string.Empty);
        }

        if (await WaitForTrackStableAsync(
                target,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(350),
                cancellationToken).ConfigureAwait(false))
        {
            ClearPendingNext(target);
            return $"已按顺序上一首重置锚点、暂停、重新插入并只切换一次到目标：{target.DisplayName}";
        }

        return $"已完成上一首重置锚点的一次有序兜底，但仍未确认命中；"
               + $"为避免连续往下切，已停止重试：{target.DisplayName}";
    }

    private async Task<(bool Success, string Message)>
        ResetInsertionAnchorByRoundTripAsync(
            PlayerTrack? baseline,
            CancellationToken cancellationToken)
    {
        if (baseline is null)
        {
            return (
                false,
                "酷狗立即播放已暂停，但无法识别原曲；为避免无法返回原位置，没有执行锚点重置。"
            );
        }

        var forward = await SendDirectCommandAsync(
            KugouAppCommand.NextTrack,
            TimeSpan.FromMilliseconds(900),
            cancellationToken).ConfigureAwait(false);
        if (!forward.Sent)
        {
            _ = await SendDirectCommandAsync(
                KugouAppCommand.PlayPause,
                TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false);
            return (
                false,
                "酷狗已暂停，但没有接受用于锚点重置的下一首命令；"
                + "已尝试恢复播放，没有插入新目标。"
                + (forward.Error ?? string.Empty));
        }

        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        var backward = await SendDirectCommandAsync(
            KugouAppCommand.PreviousTrack,
            TimeSpan.FromMilliseconds(900),
            cancellationToken).ConfigureAwait(false);
        if (!backward.Sent)
        {
            return (
                false,
                "酷狗已在暂停事务中切到下一首，但没有接受返回原曲的上一首命令；"
                + "为避免在错误位置插歌，已停止事务。"
                + (backward.Error ?? string.Empty));
        }

        if (await WaitForTrackStableAsync(
                baseline,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(180),
                cancellationToken).ConfigureAwait(false))
        {
            return (
                true,
                $"已通过暂停中的下一首→上一首回到原曲并重置插入锚点：{baseline.DisplayName}");
        }

        return (
            false,
            $"酷狗接受了下一首→上一首，但 3 秒内未稳定返回原曲 {baseline.DisplayName}；"
            + "为避免在错误位置插歌，已停止事务。"
        );
    }

    private static Task<BackgroundControlResult> SendDirectCommandAsync(
        KugouAppCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => KugouNativeController.SendDirectKugouCommand(
                command,
                timeout),
            cancellationToken);
    }

    private (bool Success, string Message)
        PreserveDisplacedPendingAfterCurrent(
            PendingKugouNext? displacedPending)
    {
        if (displacedPending is null)
        {
            return (true, string.Empty);
        }

        // The paused Next -> Previous round trip does not remove the native
        // queue row. After inserting and advancing to the immediate request,
        // the displaced request is already its next item. Only restore our
        // bookkeeping; sending WM_COPYDATA again would create a duplicate.
        RestorePendingNext(displacedPending);
        return (
            true,
            $"旧下一首仍原样保留在其后，未重复插入：{displacedPending.Target.DisplayName}。"
        );
    }

    private async Task<(bool Success, string Message)>
        InsertDisplacedPendingAfterCurrentAsync(
            PendingKugouNext? displacedPending,
            CancellationToken cancellationToken)
    {
        if (displacedPending is null)
        {
            return (true, string.Empty);
        }

        var endpoint = FindValidatedIpcEndpoint();
        if (endpoint is null)
        {
            RestorePendingNext(null);
            return (
                false,
                $"旧下一首 {displacedPending.Target.DisplayName} 未能补回：酷狗点歌 IPC 不可用。");
        }

        var insertion = await SendInsertNextAsync(
            endpoint.Value,
            displacedPending.Target.NativeData,
            currentProcessId: endpoint.Value.ProcessId,
            tryAnchorReset: true,
            cancellationToken).ConfigureAwait(false);
        var delivery = insertion.Delivery;
        if (!delivery.Accepted)
        {
            RestorePendingNext(null);
            return (
                false,
                $"旧下一首 {displacedPending.Target.DisplayName} 未能补回："
                + delivery.Message
                + AnchorResetSuffix(insertion.AnchorResetMessage));
        }

        RememberPendingNext(displacedPending.Target);
        await Task.Delay(160, cancellationToken).ConfigureAwait(false);
        return (
            true,
            $"旧下一首已只补回一次：{displacedPending.Target.DisplayName}。");
    }

    private bool HasPendingNext(PlayerTrack target)
    {
        lock (_pendingNextSync)
        {
            if (_pendingNext is null)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - _pendingNext.InsertedAt
                > TimeSpan.FromMinutes(10))
            {
                _pendingNext = null;
                _pendingTargetObservedAt = null;
                return false;
            }

            return TrackMatches(_pendingNext.Target, target);
        }
    }

    private PendingKugouNext? GetDifferentPendingNext(PlayerTrack target)
    {
        lock (_pendingNextSync)
        {
            if (_pendingNext is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - _pendingNext.InsertedAt
                > TimeSpan.FromMinutes(10))
            {
                _pendingNext = null;
                _pendingTargetObservedAt = null;
                return null;
            }

            return TrackMatches(_pendingNext.Target, target)
                ? null
                : _pendingNext;
        }
    }

    private PlayerTrack? GetPendingNextTrack()
    {
        lock (_pendingNextSync)
        {
            if (_pendingNext is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - _pendingNext.InsertedAt
                > TimeSpan.FromMinutes(10))
            {
                _pendingNext = null;
                _pendingTargetObservedAt = null;
                return null;
            }

            return _pendingNext.Target;
        }
    }

    private void RestorePendingNext(PendingKugouNext? pending)
    {
        lock (_pendingNextSync)
        {
            _pendingNext = pending is null
                ? null
                : new PendingKugouNext(
                    pending.Target,
                    DateTimeOffset.UtcNow);
            _pendingTargetObservedAt = null;
        }
        _eventMonitor.NotifySnapshotInvalidated();
    }

    private void RememberPendingNext(PlayerTrack target)
    {
        lock (_pendingNextSync)
        {
            _pendingNext = new PendingKugouNext(
                target,
                DateTimeOffset.UtcNow);
            _pendingTargetObservedAt = null;
        }
        _eventMonitor.NotifySnapshotInvalidated();
    }

    private void ClearPendingNextIfPlaying(PlayerTrack? current)
    {
        PendingKugouNext? pendingToConfirm = null;
        DateTimeOffset? observedAt = null;
        lock (_pendingNextSync)
        {
            if (_pendingNext is null)
            {
                _pendingTargetObservedAt = null;
                return;
            }

            if (!TrackMatches(current, _pendingNext.Target))
            {
                _pendingTargetObservedAt = null;
                return;
            }

            if (_pendingTargetObservedAt is null)
            {
                _pendingTargetObservedAt = DateTimeOffset.UtcNow;
                pendingToConfirm = _pendingNext;
                observedAt = _pendingTargetObservedAt;
            }
        }

        if (pendingToConfirm is not null && observedAt is not null)
        {
            _ = ConfirmPendingTargetAfterDelayAsync(
                pendingToConfirm,
                observedAt.Value);
        }
    }

    private async Task ConfirmPendingTargetAfterDelayAsync(
        PendingKugouNext pending,
        DateTimeOffset observedAt)
    {
        try
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(350),
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var current = await ReadCurrentForGuardAsync(
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var cleared = false;
            lock (_pendingNextSync)
            {
                if (_pendingNext is not null
                    && _pendingNext.InsertedAt == pending.InsertedAt
                    && _pendingTargetObservedAt == observedAt
                    && TrackMatches(current, pending.Target))
                {
                    _pendingNext = null;
                    _pendingTargetObservedAt = null;
                    cleared = true;
                }
                else if (_pendingNext is not null
                         && _pendingNext.InsertedAt == pending.InsertedAt
                         && _pendingTargetObservedAt == observedAt)
                {
                    _pendingTargetObservedAt = null;
                }
            }

            if (cleared)
            {
                _eventMonitor.NotifySnapshotInvalidated();
            }
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown cancels the one-shot stability confirmation.
        }
        catch
        {
            lock (_pendingNextSync)
            {
                if (_pendingNext is not null
                    && _pendingNext.InsertedAt == pending.InsertedAt
                    && _pendingTargetObservedAt == observedAt)
                {
                    _pendingTargetObservedAt = null;
                }
            }
        }
    }

    private void ClearPendingNext(PlayerTrack target)
    {
        var cleared = false;
        lock (_pendingNextSync)
        {
            if (_pendingNext is not null
                && TrackMatches(_pendingNext.Target, target))
            {
                _pendingNext = null;
                _pendingTargetObservedAt = null;
                cleared = true;
            }
        }

        if (cleared)
        {
            _eventMonitor.NotifySnapshotInvalidated();
        }
    }

    private static (int ProcessId, string Version)? FindTarget()
    {
        var windows = KugouNativeController.InspectWindows();
        var main = windows
            .Where(window =>
                window.ParentHandle is null)
            .OrderByDescending(window => window.IsVisible)
            .ThenByDescending(window => window.ClassName.Equals(
                "kugou_ui",
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window =>
                window.Title.Contains(
                    "酷狗音乐",
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window =>
                (long)window.Width * window.Height)
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
        known ??= FindKnownTrackByMetadata(fallback);
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

    private PlayerTrack? FindKnownTrackByMetadata(PlayerTrack current)
    {
        lock (_trackSync)
        {
            return _knownTracks.Values
                .Distinct()
                .FirstOrDefault(candidate => TrackMatches(current, candidate));
        }
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
            while (_knownTracks.Count > 2048)
            {
                _knownTracks.Remove(_knownTracks.Keys.First());
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
            if (_artworkRetryAfter.TryGetValue(identity, out var retryAfter)
                && retryAfter > DateTimeOffset.UtcNow)
            {
                return;
            }

            _artworkRetryAfter.Remove(identity);
            if (!_artworkLookups.Add(identity))
            {
                return;
            }

            _artworkLookupOrder.Enqueue(identity);
            while (_artworkLookupOrder.Count > 512)
            {
                _artworkLookups.Remove(_artworkLookupOrder.Dequeue());
            }

            var task = ResolveArtworkAsync(
                identity,
                current,
                _lifetimeCancellation.Token);
            _artworkTasks.Add(task);
            _ = task.ContinueWith(
                completed =>
                {
                    var resolved = completed.IsCompletedSuccessfully
                        && completed.Result;
                    lock (_trackSync)
                    {
                        _artworkTasks.Remove(completed);
                        _artworkLookups.Remove(identity);
                        if (!resolved)
                        {
                            _artworkRetryAfter[identity] =
                                DateTimeOffset.UtcNow
                                + TimeSpan.FromSeconds(20);
                        }
                        else
                        {
                            _artworkRetryAfter.Remove(identity);
                        }
                    }
                    if (resolved)
                    {
                        _eventMonitor.NotifySnapshotInvalidated();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task<bool> ResolveArtworkAsync(
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
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown or a cancelled request does not affect playback.
            return false;
        }
        catch
        {
            // Missing artwork is non-fatal; playback state remains available.
            return false;
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

        var actualTitle = NormalizeTrackText(actual.Title);
        var expectedTitle = NormalizeTrackText(expected.Title);
        var titleMatches = actualTitle == expectedTitle
            || (expectedTitle.Length >= 4
                && actualTitle.StartsWith(
                    expectedTitle,
                    StringComparison.Ordinal));
        return titleMatches
            && (string.IsNullOrWhiteSpace(expected.Artist)
                || NormalizeTrackText(actual.Artist)
                    == NormalizeTrackText(expected.Artist));
    }

    private static string NormalizeTrackText(string value)
    {
        return string.Concat(
            value.Normalize(NormalizationForm.FormKC)
                .Where(char.IsLetterOrDigit))
            .ToUpperInvariant();
    }

    private static string BuildSnapshotFingerprint(PlayerSnapshot snapshot)
    {
        var current = snapshot.Current;
        var next = snapshot.Next;
        return string.Join(
            '\u001F',
            snapshot.Connected.ToString(),
            snapshot.ProcessId?.ToString(),
            snapshot.Version,
            snapshot.Status,
            current?.Id,
            current?.Title,
            current?.Artist,
            current?.Album,
            current?.CoverUrl,
            next?.Id,
            next?.Title,
            next?.Artist,
            next?.Album,
            next?.CoverUrl,
            snapshot.NextSource);
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
                "AlbumImage",
                "album_img");
        }
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            coverUrl = ReadJsonTextAny(
                song,
                "img",
                "Image",
                "imgUrl",
                "imgurl");
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

    private static string BuildInsertNextPayload(string rawSongJson)
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
                ["Play"] = "0",
                ["PlayAll"] = "0",
                ["Clear"] = "0",
                ["Insert"] = "1",
                ["Force"] = "0",
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

    private sealed record PendingKugouNext(
        PlayerTrack Target,
        DateTimeOffset InsertedAt);
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
