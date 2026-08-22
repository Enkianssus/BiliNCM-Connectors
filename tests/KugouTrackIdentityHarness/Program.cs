using KugouControlPoc;

static void Equal<T>(T expected, T actual, string label)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{label}: expected {expected}, actual {actual}");
    }
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"{label}: expected true");
    }
}

static void False(bool value, string label)
{
    if (value)
    {
        throw new InvalidOperationException($"{label}: expected false");
    }
}

var spotlight = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、SIENA- SPOTLIGHT HUNTER (焦点猎手)");
Equal("三角洲行动、SIENA", spotlight.Artist, "SPOTLIGHT artist parse");
Equal("SPOTLIGHT HUNTER", spotlight.Title, "SPOTLIGHT localized title parse");
True(spotlight.RemovedLocalizedAlias, "SPOTLIGHT alias removal");

var spotlightWithOtherSpacing = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、SIENA -SPOTLIGHT HUNTER (焦点猎手)");
Equal(
    "SPOTLIGHT HUNTER",
    spotlightWithOtherSpacing.Title,
    "SPOTLIGHT alternate separator parse");

var dawn = KugouTrackIdentityPolicy.ParseArtistAndTitle(
    "三角洲行动、Lithium Done- Dawn (黎明将至) (...|)");
Equal("三角洲行动、Lithium Done", dawn.Artist, "Dawn artist parse");
Equal("Dawn", dawn.Title, "Dawn localized and metadata suffix parse");

True(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "SPOTLIGHT app-prefix/localized alias match");
True(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "",
        "Dawn (黎明将至) (...|)",
        "三角洲行动、Lithium Done",
        "390767001",
        "Dawn",
        "Lithium Done"),
    "Dawn fallback maps to confirmed numeric track");

True(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "493741552",
        "not-used",
        "not-used",
        "493741552",
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA"),
    "same stable ID remains authoritative");
False(
    KugouTrackIdentityPolicy.TracksRepresentSameSong(
        "493741552",
        "SPOTLIGHT HUNTER",
        "三角洲行动、SIENA",
        "493741553",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "different stable IDs must not merge");

False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER (Live)",
        "SIENA",
        "SPOTLIGHT HUNTER",
        "SIENA"),
    "Live version must remain distinct");
False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER (Remix)",
        "SIENA",
        "SPOTLIGHT HUNTER",
        "SIENA"),
    "Remix version must remain distinct");
False(
    KugouTrackIdentityPolicy.MetadataRepresentsSameSong(
        "SPOTLIGHT HUNTER",
        "Other Artist",
        "SPOTLIGHT HUNTER (焦点猎手)",
        "SIENA"),
    "different artists must remain distinct");

True(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        1),
    "first stale INI observation is held");
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "NEW-ID",
        "New Song",
        "New Artist",
        2),
    "repeated INI candidate is eventually accepted");
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "WindowTitle",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        1),
    "authoritative WindowTitle change is not delayed");

var confirmedAt = new DateTimeOffset(
    2026,
    8,
    22,
    12,
    0,
    0,
    TimeSpan.Zero);
var staleIniWrite = confirmedAt.AddSeconds(-1);
True(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "OLD-ID",
        "Merry Christmas",
        "Old Habits",
        2,
        staleIniWrite,
        confirmedAt,
        confirmedAt.AddSeconds(1),
        confirmedAt.AddSeconds(2)),
    "old INI remains held after repeated callbacks");

var newIniWrite = confirmedAt.AddSeconds(1);
False(
    KugouTrackIdentityPolicy.ShouldHoldTransientIni(
        "KuGou.ini",
        "POSTERGIRL-ID",
        "POSTERGIRL",
        "Artist",
        "NEW-ID",
        "New Song",
        "New Artist",
        2,
        newIniWrite,
        confirmedAt,
        confirmedAt.AddMilliseconds(-500),
        confirmedAt),
    "new INI eventually transitions after debounce");

Console.WriteLine("KuGou track identity harness passed.");
