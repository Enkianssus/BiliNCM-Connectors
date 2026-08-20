using QQMusicControlPoc;
using UnifiedPlayerControlPoc;

static void AssertTrack(
    string input,
    string expectedTitle,
    string expectedArtist)
{
    var actual = QQMusicWindowTitleParser.Parse(input)
        ?? throw new InvalidOperationException($"Expected a track for: {input}");
    if (actual.Title != expectedTitle || actual.Artist != expectedArtist)
    {
        throw new InvalidOperationException(
            $"Unexpected parse for '{input}': "
            + $"'{actual.Title}' / '{actual.Artist}'");
    }
}

AssertTrack(
    "D3 - 4 4 4 4 / h (Explicit) - VERT3X",
    "D3 - 4 4 4 4 / h (Explicit)",
    "VERT3X");
AssertTrack(
    "Shelter - Porter Robinson、Madeon",
    "Shelter",
    "Porter Robinson、Madeon");
AssertTrack(
    "Part One - Part Two - Artist",
    "Part One - Part Two",
    "Artist");

if (QQMusicWindowTitleParser.Parse("QQ音乐") is not null
    || QQMusicWindowTitleParser.Parse("No separator") is not null
    || QQMusicWindowTitleParser.Parse(null) is not null)
{
    throw new InvalidOperationException("Non-track captions must not parse.");
}

if (!QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Shelter",
        "Porter Robinson、Madeon",
        "Shelter",
        "Porter Robinson / Madeon")
    || QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Home",
        "Artist A",
        "Home",
        "Artist B")
    || QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Home",
        "Artist A",
        "Home",
        string.Empty))
{
    throw new InvalidOperationException(
        "Structured metadata confidence checks failed.");
}

var artworkCandidates = new[]
{
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Porter Robinson / Madeon",
        "Shelter: Complete Edition",
        "001GMevG3WoCdt"),
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Porter Robinson / Madeon",
        "Shelter",
        "0046VnUP3it5w8"),
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Different Artist",
        "Shelter",
        "wrong-picture")
};
var pictureFallback = QQMusicAlbumArtwork.SelectPictureId(
    string.Empty,
    "Shelter",
    "Porter Robinson\u3001Madeon",
    artworkCandidates);
if (pictureFallback != "0046VnUP3it5w8"
    || QQMusicAlbumArtwork.SelectPictureId(
        "0046VnUP3it5w8",
        "Shelter",
        "Porter Robinson / Madeon",
        artworkCandidates) != "0046VnUP3it5w8"
    || QQMusicAlbumArtwork.SelectPictureId(
        string.Empty,
        "Unmatched Song",
        "Unmatched Artist",
        artworkCandidates) != string.Empty
    || QQMusicAlbumArtwork.BuildCoverUrl(pictureFallback)
        != "https://y.gtimg.cn/music/photo_new/"
           + "T002R300x300M0000046VnUP3it5w8.jpg"
    || QQMusicAlbumArtwork.BuildCoverUrl(string.Empty) != string.Empty)
{
    throw new InvalidOperationException(
        "Album artwork fallback checks failed.");
}

if (!QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Inst.)",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (Instrumental)",
        "Sparky Deathcap",
        "September",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Live)",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Inst.)",
        "Other Artist")
    || !QQMusicTrackMatchPolicy.TracksRepresentSameSong(
        "395562465",
        "September (Inst.)",
        "Sparky Deathcap",
        "395562465",
        "September (Live)",
        "Other Artist")
    || QQMusicTrackMatchPolicy.TracksRepresentSameSong(
        "111",
        "September (Inst.)",
        "Sparky Deathcap",
        "222",
        "September (纯音乐)",
        "Sparky Deathcap"))
{
    throw new InvalidOperationException(
        "QQ instrumental alias matching checks failed.");
}

Console.WriteLine("QQ Music metadata policy tests passed.");
