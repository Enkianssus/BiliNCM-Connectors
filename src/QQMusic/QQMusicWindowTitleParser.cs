namespace QQMusicControlPoc;

internal sealed record QQMusicParsedWindowTrack(
    string Title,
    string Artist);

internal static class QQMusicWindowTitleParser
{
    private const string Separator = " - ";

    internal static QQMusicParsedWindowTrack? Parse(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)
            || windowTitle.Equals(
                "QQ\u97F3\u4E50",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // QQ formats the caption as "<song title> - <artist>". A song title
        // can itself contain the same delimiter, so the artist separator is
        // the final occurrence rather than the first one.
        var separator = windowTitle.LastIndexOf(
            Separator,
            StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var title = windowTitle[..separator].Trim();
        var artist = windowTitle[(separator + Separator.Length)..].Trim();
        return string.IsNullOrWhiteSpace(title)
            ? null
            : new QQMusicParsedWindowTrack(title, artist);
    }

    internal static bool MetadataRepresentsSameSong(
        string? firstTitle,
        string? firstArtist,
        string? secondTitle,
        string? secondArtist)
    {
        if (string.IsNullOrWhiteSpace(firstTitle)
            || string.IsNullOrWhiteSpace(secondTitle)
            || !firstTitle.Trim().Equals(
                secondTitle.Trim(),
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(firstArtist)
            || string.IsNullOrWhiteSpace(secondArtist))
        {
            return false;
        }

        return NormalizeArtistIdentity(firstArtist)
            == NormalizeArtistIdentity(secondArtist);
    }

    private static string NormalizeArtistIdentity(string value)
    {
        var artists = value.Split(
            ["、", "/", "&", ",", "，", ";", "；"],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
        return string.Join(
            '\u001F',
            artists.Select(artist => artist.ToUpperInvariant()));
    }
}
