using System.Text;

namespace KugouControlPoc;

internal sealed record KugouParsedTrackTitle(
    string Artist,
    string Title,
    bool RemovedLocalizedAlias);

/// <summary>
/// KuGou-specific normalization for the transient captions exposed by the
/// desktop client.  This deliberately knows only the Delta Force caption
/// prefix and localized parenthetical aliases observed in those captions;
/// it is not a general title/version normalizer.
/// </summary>
internal static class KugouTrackIdentityPolicy
{
    private const string DeltaForcePrefix = "三角洲行动、";
    private const string DeltaForceAsciiCommaPrefix = "三角洲行动,";
    private const int MaximumAliasLength = 64;

    internal static KugouParsedTrackTitle ParseArtistAndTitle(
        string? rawTitle)
    {
        var value = rawTitle?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return new KugouParsedTrackTitle(string.Empty, string.Empty, false);
        }

        var separatorIndex = FindArtistTitleSeparator(value);
        if (separatorIndex < 0)
        {
            var titleOnly = StripLocalizedAliases(value, out var removedOnly);
            return new KugouParsedTrackTitle(
                string.Empty,
                titleOnly,
                removedOnly);
        }

        var artist = value[..separatorIndex].Trim();
        var title = value[(separatorIndex + 1)..].Trim();
        title = StripLocalizedAliases(title, out var removed);
        return new KugouParsedTrackTitle(artist, title, removed);
    }

    internal static bool MetadataRepresentsSameSong(
        string? actualTitle,
        string? actualArtist,
        string? expectedTitle,
        string? expectedArtist)
    {
        var actual = PrepareMetadata(actualTitle, actualArtist);
        var expected = PrepareMetadata(expectedTitle, expectedArtist);
        if (actual.Title.Length == 0 || expected.Title.Length == 0)
        {
            return false;
        }

        if (!string.Equals(
                NormalizeTitle(actual.Title),
                NormalizeTitle(expected.Title),
                StringComparison.Ordinal))
        {
            return false;
        }

        var actualArtists = ArtistForms(actual.Artist);
        var expectedArtists = ArtistForms(expected.Artist);
        if (actualArtists.Count == 0 || expectedArtists.Count == 0)
        {
            // A title-only fallback is not strong enough to merge two
            // different KuGou recordings.
            return false;
        }

        return actualArtists.Overlaps(expectedArtists);
    }

    internal static bool TracksRepresentSameSong(
        string? actualId,
        string? actualTitle,
        string? actualArtist,
        string? expectedId,
        string? expectedTitle,
        string? expectedArtist)
    {
        var actualStableId = IsStableId(actualId) ? actualId!.Trim() : string.Empty;
        var expectedStableId = IsStableId(expectedId)
            ? expectedId!.Trim()
            : string.Empty;
        if (actualStableId.Length > 0 && expectedStableId.Length > 0)
        {
            return string.Equals(
                actualStableId,
                expectedStableId,
                StringComparison.OrdinalIgnoreCase);
        }

        return MetadataRepresentsSameSong(
            actualTitle,
            actualArtist,
            expectedTitle,
            expectedArtist);
    }

    internal static bool IsStableId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0
            && normalized.Length <= 128
            && !normalized.Contains('|', StringComparison.Ordinal);
    }

    /// <summary>
    /// The INI value can lag an authoritative window-title event.  An older
    /// timestamp remains held even across duplicate watcher callbacks; a
    /// newer candidate is accepted only after a short bounded debounce.
    /// </summary>
    internal static bool ShouldHoldTransientIni(
        string? source,
        string? confirmedId,
        string? confirmedTitle,
        string? confirmedArtist,
        string? candidateId,
        string? candidateTitle,
        string? candidateArtist,
        int candidateObservations,
        DateTimeOffset? iniLastWriteTime = null,
        DateTimeOffset? lastConfirmedWindowTitleAt = null,
        DateTimeOffset? pendingFirstObservedAt = null,
        DateTimeOffset? now = null)
    {
        if (!string.Equals(
                source?.Trim(),
                "KuGou.ini",
                StringComparison.OrdinalIgnoreCase)
            || candidateObservations < 1)
        {
            return false;
        }

        if (TracksRepresentSameSong(
                candidateId,
                candidateTitle,
                candidateArtist,
                confirmedId,
                confirmedTitle,
                confirmedArtist))
        {
            return false;
        }

        if (lastConfirmedWindowTitleAt is { } windowTitleAt
            && (iniLastWriteTime is null
                || iniLastWriteTime.Value <= windowTitleAt))
        {
            // The INI file is older than the last authoritative window-title
            // observation (or has no timestamp at all), so repeated watcher
            // callbacks must not turn the stale value into a track change.
            return true;
        }

        if (candidateObservations < 2)
        {
            return true;
        }

        return pendingFirstObservedAt is { } firstObservedAt
            && (now ?? DateTimeOffset.UtcNow) - firstObservedAt
                < TimeSpan.FromMilliseconds(250);
    }

    internal static IReadOnlyList<string> BuildIdentityKeys(
        string? rawTitle,
        string? artist,
        string? title)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        AddIdentityKey(keys, rawTitle);

        var parsed = PrepareMetadata(title, artist);
        if (parsed.Title.Length > 0)
        {
            foreach (var artistForm in ArtistFormsForIdentity(parsed.Artist))
            {
                AddIdentityKey(
                    keys,
                    $"{artistForm} - {parsed.Title}");
            }
        }

        return [.. keys];
    }

    internal static string BuildSearchQuery(
        string? title,
        string? artist)
    {
        var parsed = PrepareMetadata(title, artist);
        var artistForms = ArtistFormsForIdentity(parsed.Artist);
        var searchArtist = artistForms.Count > 1
            ? artistForms[1]
            : artistForms.FirstOrDefault() ?? string.Empty;
        return string.Join(
            ' ',
            new[] { parsed.Title, searchArtist }
                .Where(value => value.Length > 0));
    }

    internal static string NormalizeIdentityKey(string? value)
    {
        var normalized = value?
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToUpperInvariant()
            ?? string.Empty;
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

    private static (string Artist, string Title) PrepareMetadata(
        string? title,
        string? artist)
    {
        var actualArtist = artist?.Trim() ?? string.Empty;
        var actualTitle = title?.Trim() ?? string.Empty;
        if (actualArtist.Length == 0)
        {
            var parsed = ParseArtistAndTitle(actualTitle);
            actualArtist = parsed.Artist;
            actualTitle = parsed.Title;
        }
        else
        {
            actualTitle = StripLocalizedAliases(actualTitle, out _);
        }

        return (actualArtist, actualTitle);
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = StripLocalizedAliases(value, out _)
            .Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant();
        return string.Concat(
            normalized.Where(char.IsLetterOrDigit));
    }

    private static HashSet<string> ArtistForms(string? value)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var form in ArtistFormsForIdentity(value))
        {
            var normalized = NormalizeIdentityText(form);
            if (normalized.Length > 0)
            {
                forms.Add(normalized);
            }
        }

        return forms;
    }

    private static IReadOnlyList<string> ArtistFormsForIdentity(string? value)
    {
        var artist = value?.Trim() ?? string.Empty;
        if (artist.Length == 0)
        {
            return [];
        }

        var forms = new List<string> { artist };
        if (artist.StartsWith(
                DeltaForcePrefix,
                StringComparison.Ordinal))
        {
            forms.Add(artist[DeltaForcePrefix.Length..].Trim());
        }
        else if (artist.StartsWith(
                     DeltaForceAsciiCommaPrefix,
                     StringComparison.Ordinal))
        {
            forms.Add(artist[DeltaForceAsciiCommaPrefix.Length..].Trim());
        }

        return forms
            .Where(form => form.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeIdentityText(string value)
    {
        var normalized = value
            .Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant();
        return string.Concat(
            normalized.Where(char.IsLetterOrDigit));
    }

    private static void AddIdentityKey(
        HashSet<string> keys,
        string? value)
    {
        var key = NormalizeIdentityKey(value);
        if (key.Length > 0)
        {
            keys.Add(key);
        }
    }

    private static int FindArtistTitleSeparator(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '-')
            {
                continue;
            }

            var hasWhitespaceBefore = index > 0
                && char.IsWhiteSpace(value[index - 1]);
            var hasWhitespaceAfter = index + 1 < value.Length
                && char.IsWhiteSpace(value[index + 1]);
            if (!hasWhitespaceBefore && !hasWhitespaceAfter)
            {
                continue;
            }

            var artist = value[..index].Trim();
            var title = value[(index + 1)..].Trim();
            if (artist.Length > 0 && title.Length > 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string StripLocalizedAliases(
        string value,
        out bool removed)
    {
        var result = value.Trim();
        removed = false;
        while (TryReadTrailingParenthetical(result, out var start, out var content))
        {
            if (!IsLocalizedAlias(content)
                && !IsWindowMetadataSuffix(content))
            {
                break;
            }

            result = result[..start].TrimEnd();
            removed = true;
        }

        return result;
    }

    private static bool TryReadTrailingParenthetical(
        string value,
        out int start,
        out string content)
    {
        start = -1;
        content = string.Empty;
        if (value.Length < 3)
        {
            return false;
        }

        var closing = value[^1];
        var opening = closing == '）' ? '（' : '(';
        if (closing is not (')' or '）'))
        {
            return false;
        }

        var openingIndex = value.LastIndexOf(opening);
        if (openingIndex <= 0
            || value.Length - openingIndex - 2 > MaximumAliasLength)
        {
            return false;
        }

        start = openingIndex;
        content = value[(openingIndex + 1)..^1].Trim();
        return content.Length > 0;
    }

    private static bool IsLocalizedAlias(string value)
    {
        if (value.Length == 0 || ContainsVersionMarker(value))
        {
            return false;
        }

        return value.Any(IsCjkCharacter);
    }

    private static bool IsWindowMetadataSuffix(string value)
    {
        return value.Contains('|', StringComparison.Ordinal)
            && value.Length <= MaximumAliasLength;
    }

    private static bool ContainsVersionMarker(string value)
    {
        var normalized = NormalizeIdentityText(value);
        return new[]
        {
            "LIVE",
            "REMIX",
            "SPEDUP",
            "ACOUSTIC",
            "INSTRUMENTAL",
            "INST",
            "EDIT",
            "VERSION",
            "MIX",
            "COVER",
            "RADIO",
            "现场",
            "混音",
            "加速",
            "慢速",
            "翻唱",
            "伴奏",
            "纯音乐",
            "重混",
            "舞曲"
        }.Any(normalized.Contains);
    }

    private static bool IsCjkCharacter(char value)
    {
        return value is (>= '\u3400' and <= '\u4dbf')
            or (>= '\u4e00' and <= '\u9fff')
            or (>= '\uf900' and <= '\ufaff');
    }
}
