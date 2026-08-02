using System.Text.RegularExpressions;

namespace UnifiedPlayerControlPoc;

internal enum NeteaseSongQueryKind
{
    Keyword,
    ExplicitId,
    SuspectedId
}

internal readonly record struct NeteaseSongQuery(
    NeteaseSongQueryKind Kind,
    string Value);

internal enum KugouSongQueryKind
{
    Keyword,
    ShareCode,
    Chain
}

internal readonly record struct KugouSongQuery(
    KugouSongQueryKind Kind,
    string Value);

internal static class SongQueryPolicy
{
    public static NeteaseSongQuery ParseNetease(string input)
    {
        var query = input.Trim();
        var explicitId = Regex.Match(
            query,
            "^id\\s*=\\s*([0-9]{1,19})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitId.Success)
        {
            return new NeteaseSongQuery(
                NeteaseSongQueryKind.ExplicitId,
                explicitId.Groups[1].Value);
        }

        return Regex.IsMatch(
            query,
            "^[0-9]{6,19}$",
            RegexOptions.CultureInvariant)
            ? new NeteaseSongQuery(
                NeteaseSongQueryKind.SuspectedId,
                query)
            : new NeteaseSongQuery(
                NeteaseSongQueryKind.Keyword,
                query);
    }

    public static KugouSongQuery ParseKugou(string input)
    {
        var query = input.Trim();
        var explicitChain = Regex.Match(
            query,
            "^chain=([A-Za-z0-9_-]{6,32})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (explicitChain.Success)
        {
            return new KugouSongQuery(
                KugouSongQueryKind.Chain,
                explicitChain.Groups[1].Value);
        }

        var shareLink = Regex.Match(
            query,
            "^https?://m\\.kugou\\.com/share/song\\.html"
            + "\\?(?:[^#]*&)?chain=([A-Za-z0-9_-]{6,32})(?:[&#].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (shareLink.Success)
        {
            return new KugouSongQuery(
                KugouSongQueryKind.Chain,
                shareLink.Groups[1].Value);
        }

        if (Regex.IsMatch(
                query,
                "^(?=.*[A-Za-z])(?=.*[0-9])[A-Za-z0-9]{8,32}$",
                RegexOptions.CultureInvariant))
        {
            return new KugouSongQuery(
                KugouSongQueryKind.Chain,
                query);
        }

        var shareCode = Regex.Match(
            query,
            "^(?:#([0-9]+)#|([0-9]+))$",
            RegexOptions.CultureInvariant);
        if (shareCode.Success)
        {
            return new KugouSongQuery(
                KugouSongQueryKind.ShareCode,
                shareCode.Groups[1].Success
                    ? shareCode.Groups[1].Value
                    : shareCode.Groups[2].Value);
        }

        return new KugouSongQuery(
            KugouSongQueryKind.Keyword,
            query);
    }
}
