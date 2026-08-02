using UnifiedPlayerControlPoc;

static void Equal<T>(T expected, T actual, string label)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{label}: expected {expected}, actual {actual}");
    }
}

var explicitNetease = SongQueryPolicy.ParseNetease("id=1403356922");
Equal(NeteaseSongQueryKind.ExplicitId, explicitNetease.Kind, "NetEase explicit ID kind");
Equal("1403356922", explicitNetease.Value, "NetEase explicit ID value");

var suspectedNetease = SongQueryPolicy.ParseNetease("1403356922");
Equal(NeteaseSongQueryKind.SuspectedId, suspectedNetease.Kind, "NetEase suspected ID kind");
Equal("1403356922", suspectedNetease.Value, "NetEase suspected ID value");

Equal(
    NeteaseSongQueryKind.Keyword,
    SongQueryPolicy.ParseNetease("1026").Kind,
    "short numeric NetEase title");

foreach (var query in new[] { "17154574", "#17154574#" })
{
    var parsed = SongQueryPolicy.ParseKugou(query);
    Equal(KugouSongQueryKind.ShareCode, parsed.Kind, $"KuGou share code {query}");
    Equal("17154574", parsed.Value, $"KuGou normalized share code {query}");
}

foreach (var query in new[]
{
    "chain=ig6N5bG4V3",
    "ig6N5bG4V3",
    "https://m.kugou.com/share/song.html?chain=ig6N5bG4V3"
})
{
    var parsed = SongQueryPolicy.ParseKugou(query);
    Equal(KugouSongQueryKind.Chain, parsed.Kind, $"KuGou chain {query}");
    Equal("ig6N5bG4V3", parsed.Value, $"KuGou normalized chain {query}");
}

Equal(
    KugouSongQueryKind.Keyword,
    SongQueryPolicy.ParseKugou("Something Comforting").Kind,
    "ordinary KuGou keyword");
Equal(
    KugouSongQueryKind.Keyword,
    SongQueryPolicy.ParseKugou("https://evil.example/?chain=abc12345").Kind,
    "non-KuGou chain URL");
Equal(
    KugouSongQueryKind.Keyword,
    SongQueryPolicy.ParseKugou("abcde-123456").Kind,
    "punctuated bare KuGou keyword");

Console.WriteLine("SongQueryPolicyHarness passed.");
