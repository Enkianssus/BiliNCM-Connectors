namespace QQMusicControlPoc;

internal sealed record QQMusicAlbumArtworkCandidate(
    string Title,
    string Artist,
    string Album,
    string PictureId);

internal static class QQMusicAlbumArtwork
{
    internal static string SelectPictureId(
        string? existingPictureId,
        string title,
        string artist,
        IEnumerable<QQMusicAlbumArtworkCandidate> candidates)
    {
        var normalizedExisting = existingPictureId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedExisting))
        {
            return normalizedExisting;
        }

        return candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.PictureId)
                && QQMusicWindowTitleParser.MetadataRepresentsSameSong(
                    title,
                    artist,
                    candidate.Title,
                    candidate.Artist))
            .OrderByDescending(candidate =>
                string.Equals(
                    candidate.Album.Trim(),
                    title.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.PictureId.Trim())
            .FirstOrDefault() ?? string.Empty;
    }

    internal static string BuildCoverUrl(string? pictureId)
    {
        return string.IsNullOrWhiteSpace(pictureId)
            ? string.Empty
            : "https://y.gtimg.cn/music/photo_new/"
              + $"T002R300x300M000{pictureId.Trim()}.jpg";
    }
}
