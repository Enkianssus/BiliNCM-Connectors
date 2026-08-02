namespace KugouControlPoc;

internal sealed record KugouAnchorResetProfile(
    string PlayerVersion,
    string DllSha256,
    int GetServiceRootRva,
    int GetQueueControllerRva,
    int QueueControllerVtableRva,
    int AnchorTrackerOffset,
    int AnchorTrackerVtableRva,
    int AnchorTrackerSecondVtableRva,
    int AnchorTrackerThirdVtableRva,
    int ResetFunctionRva,
    byte[] GetServiceRootBytes,
    byte[] GetQueueControllerBytes,
    byte[] ResetFunctionBytes);

internal static class KugouAnchorResetProfilePolicy
{
    private static readonly KugouAnchorResetProfile[] Profiles =
    [
        new(
            "20.0.81.27563",
            "193CEB92AC2281FCDC8A109BC533F3BC54FCCAFDA0CB1C0E61C0D140657F6132",
            0x00C4982E,
            0x00C491E9,
            0x01548AFC,
            0x60,
            0x01546414,
            0x0154643C,
            0x01546444,
            0x00905251,
            [0xE9, 0x60, 0x30, 0x00, 0x00],
            [0x8B, 0x49, 0x10, 0xE9, 0x7F, 0xDD, 0x94, 0xFF],
            Convert.FromHexString(
                "6A1CB861590A11E8DBD471008BF18D7E10578D4DE8E8C4FF7BFF"))
    ];

    internal static KugouAnchorResetProfile? Find(
        string playerVersion,
        string dllSha256)
    {
        return Profiles.FirstOrDefault(profile =>
            profile.PlayerVersion.Equals(
                playerVersion,
                StringComparison.Ordinal)
            && profile.DllSha256.Equals(
                dllSha256,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildUpdatePrompt(string playerVersion)
    {
        var displayVersion = string.IsNullOrWhiteSpace(playerVersion)
            ? "未知版本"
            : playerVersion;
        return $"当前酷狗 {displayVersion} 不在无切歌锚点兼容画像中；"
               + "已使用旧兼容插入逻辑。如果点歌顺序异常，请在播放器设置中更新酷狗连接器。";
    }

    internal static string BuildFailurePrompt(
        string playerVersion,
        string reason)
    {
        var displayVersion = string.IsNullOrWhiteSpace(playerVersion)
            ? "未知版本"
            : playerVersion;
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "未知原因"
            : reason;
        return $"当前酷狗 {displayVersion} 的无切歌锚点重置失败（{detail}）；"
               + "已回退旧兼容插入逻辑。请更新酷狗连接器。";
    }
}
