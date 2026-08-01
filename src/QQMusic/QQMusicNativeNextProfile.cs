using System.Globalization;
using System.Text.Json;

namespace QQMusicControlPoc;

internal sealed record QQMusicNativeNextProfile(
    string FileVersion,
    string ClientSha256,
    string CommonSha256,
    int SingleSongPlayDispatchRva,
    byte[] ExpectedPlayDispatchBytes,
    int GetCatManagerRva,
    int GetQqUinExRva,
    int SongItemConstructorRva,
    int SongItemDestructorRva,
    int AddSongsRva,
    int HiddenCategoryIdRva,
    int GetListRootRva,
    int GetListHelperRva,
    int GetCategoryCountRva,
    int SongItemSize,
    string Evidence);

internal static class QQMusicNativeNextProfiles
{
    private static readonly QQMusicNativeNextProfile[] BuiltInProfiles =
    [
        new(
            "22.22",
            "FF0AB7911EB2ACF433F2DAF0FC4BA48FFFC64169CD822CE4D5B00E88FA180A50",
            "9F7FC7DF5BC4BBE9B4C3377449CBCB3C47A218A934FAAE4DFF8578C3EDAF652F",
            0x0047A4F4,
            [0xE8, 0xD7, 0x53, 0x16, 0x00],
            0x0000F0ED,
            0x0002E089,
            0x0004A2A0,
            0x00049DE0,
            0x0042C010,
            0x00C141A0,
            0x00602430,
            0x00602590,
            0x004DBBC0,
            0xA0,
            "2026-07-30 现场捕捉右键下一首播放并重复验证"),
        new(
            "22.41",
            "A5F3E917A5233D925268C34656E49096B6223B74631C5002DB606AD4B2C7A3F3",
            "36775378403DB33D049EE87BCAD654BA3A041B7D41259CD7EDFE65457D7E2A06",
            0x0048C124,
            [0xE8, 0x67, 0x55, 0x16, 0x00],
            0x0000F0ED,
            0x0002E089,
            0x0004B800,
            0x0004B340,
            0x0043DA80,
            0x00C301A0,
            0x006142F0,
            0x00614450,
            0x004ED5D0,
            0xA0,
            "2026-08-01 校准 cmd_count=1 单曲分支并现场动态验证")
    ];

    private static readonly Lazy<IReadOnlyList<QQMusicNativeNextProfile>>
        LoadedProfiles = new(LoadProfiles);

    public static IReadOnlyList<QQMusicNativeNextProfile> All =>
        LoadedProfiles.Value;

    public static QQMusicNativeNextProfile? Find(
        string fileVersion,
        string clientSha256,
        string commonSha256)
    {
        return All.FirstOrDefault(profile =>
            string.Equals(
                profile.FileVersion,
                fileVersion,
                StringComparison.Ordinal)
            && string.Equals(
                profile.ClientSha256,
                clientSha256,
                StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(profile.CommonSha256)
                || string.Equals(
                    profile.CommonSha256,
                    commonSha256,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<QQMusicNativeNextProfile> LoadProfiles()
    {
        var profiles = new Dictionary<string, QQMusicNativeNextProfile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BuiltInProfiles)
        {
            profiles[BuildKey(profile)] = profile;
        }

        foreach (var directory in GetProfileDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var document = JsonSerializer.Deserialize<
                        QQMusicNativeNextProfileDocument>(
                        File.ReadAllText(path),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    var profile = document?.ToProfile();
                    if (profile is not null)
                    {
                        profiles[BuildKey(profile)] = profile;
                    }
                }
                catch
                {
                    // A malformed downloaded profile must never disable the
                    // built-in, hash-locked compatibility set.
                }
            }
        }

        return profiles.Values.ToArray();
    }

    private static IEnumerable<string> GetProfileDirectories()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "profiles",
            "qqmusic");
        var configured = Environment.GetEnvironmentVariable(
            "BILINCM_QQMUSIC_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Path.GetFullPath(configured);
        }
    }

    private static string BuildKey(QQMusicNativeNextProfile profile) =>
        $"{profile.FileVersion}|{profile.ClientSha256}|{profile.CommonSha256}";

    private sealed record QQMusicNativeNextProfileDocument(
        int SchemaVersion,
        string FileVersion,
        string ClientSha256,
        string CommonSha256,
        string SingleSongPlayDispatchRva,
        string ExpectedPlayDispatchBytes,
        string GetCatManagerRva,
        string GetQqUinExRva,
        string SongItemConstructorRva,
        string SongItemDestructorRva,
        string AddSongsRva,
        string HiddenCategoryIdRva,
        string GetListRootRva,
        string GetListHelperRva,
        string GetCategoryCountRva,
        string SongItemSize,
        string Evidence)
    {
        public QQMusicNativeNextProfile? ToProfile()
        {
            if (SchemaVersion != 1
                || string.IsNullOrWhiteSpace(FileVersion)
                || !IsSha256(ClientSha256)
                || !IsSha256(CommonSha256))
            {
                return null;
            }

            var expected = ExpectedPlayDispatchBytes
                .Split([' ', '-', ':'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => byte.Parse(
                    value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture))
                .ToArray();
            if (expected.Length != 5)
            {
                return null;
            }

            return new QQMusicNativeNextProfile(
                FileVersion.Trim(),
                ClientSha256.Trim().ToUpperInvariant(),
                CommonSha256.Trim().ToUpperInvariant(),
                ParseNumber(SingleSongPlayDispatchRva),
                expected,
                ParseNumber(GetCatManagerRva),
                ParseNumber(GetQqUinExRva),
                ParseNumber(SongItemConstructorRva),
                ParseNumber(SongItemDestructorRva),
                ParseNumber(AddSongsRva),
                ParseNumber(HiddenCategoryIdRva),
                ParseNumber(GetListRootRva),
                ParseNumber(GetListHelperRva),
                ParseNumber(GetCategoryCountRva),
                ParseNumber(SongItemSize),
                Evidence ?? string.Empty);
        }

        private static int ParseNumber(string value)
        {
            var trimmed = value.Trim();
            return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(
                    trimmed[2..],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture)
                : int.Parse(trimmed, CultureInfo.InvariantCulture);
        }

        private static bool IsSha256(string value) =>
            value.Length == 64
            && value.All(Uri.IsHexDigit);
    }
}
