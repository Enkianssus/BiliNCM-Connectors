using System.Text;
using System.Text.RegularExpressions;

namespace UnifiedPlayerControlPoc;

/// <summary>
/// Keeps QQ catalog metadata and the desktop/GSMTC metadata on one narrow
/// equivalence rule. QQ has used several explicit labels for instrumental
/// recordings, but other version labels remain authoritative.
/// </summary>
internal static class QQMusicTrackMatchPolicy
{
    private static readonly Regex InstrumentalSuffix = new(
        @"\((?:纯音乐|inst\.?|instrumental)\)$",
        RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.Compiled);

    internal static bool MetadataRepresentsSameSong(
        string? firstTitle,
        string? firstArtist,
        string? secondTitle,
        string? secondArtist)
    {
        if (!TitlesRepresentSameSong(firstTitle, secondTitle))
        {
            return false;
        }

        var normalizedFirstArtist = NormalizeArtist(firstArtist);
        var normalizedSecondArtist = NormalizeArtist(secondArtist);
        return !string.IsNullOrWhiteSpace(normalizedFirstArtist)
            && !string.IsNullOrWhiteSpace(normalizedSecondArtist)
            && normalizedFirstArtist == normalizedSecondArtist;
    }

    internal static bool TracksRepresentSameSong(
        string? actualId,
        string? actualTitle,
        string? actualArtist,
        string? expectedId,
        string? expectedTitle,
        string? expectedArtist)
    {
        var actualIdentity = actualId?.Trim() ?? string.Empty;
        var expectedIdentity = expectedId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(actualIdentity)
            && actualIdentity == expectedIdentity)
        {
            return true;
        }

        if (IsStableTrackId(actualIdentity)
            && IsStableTrackId(expectedIdentity)
            && actualIdentity != expectedIdentity)
        {
            return false;
        }

        if (!TitlesRepresentSameSong(actualTitle, expectedTitle))
        {
            return false;
        }

        var normalizedExpectedArtist = NormalizeArtist(expectedArtist);
        if (string.IsNullOrWhiteSpace(normalizedExpectedArtist))
        {
            return true;
        }

        var normalizedActualArtist = NormalizeArtist(actualArtist);
        return !string.IsNullOrWhiteSpace(normalizedActualArtist)
            && normalizedActualArtist == normalizedExpectedArtist;
    }

    private static bool TitlesRepresentSameSong(
        string? firstTitle,
        string? secondTitle)
    {
        var normalizedFirst = NormalizeText(firstTitle);
        var normalizedSecond = NormalizeText(secondTitle);
        if (string.IsNullOrWhiteSpace(normalizedFirst)
            || string.IsNullOrWhiteSpace(normalizedSecond))
        {
            return false;
        }
        if (normalizedFirst == normalizedSecond)
        {
            return true;
        }

        var firstIsInstrumental = InstrumentalSuffix.IsMatch(normalizedFirst);
        var secondIsInstrumental = InstrumentalSuffix.IsMatch(normalizedSecond);
        return firstIsInstrumental
            && secondIsInstrumental
            && InstrumentalSuffix.Replace(normalizedFirst, string.Empty)
                == InstrumentalSuffix.Replace(normalizedSecond, string.Empty);
    }

    private static string NormalizeArtist(string? value)
    {
        return NormalizeText(value)
            .Replace('、', '/')
            .Replace('，', '/')
            .Replace(',', '/')
            .Replace(';', '/')
            .Replace('；', '/')
            .Replace('&', '/');
    }

    private static string NormalizeText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsStableTrackId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && !value.Contains('|', StringComparison.Ordinal);
    }
}
