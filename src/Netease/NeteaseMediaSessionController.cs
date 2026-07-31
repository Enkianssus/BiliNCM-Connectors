using Windows.Media.Control;

namespace UnifiedPlayerControlPoc;

internal sealed record NeteaseMediaSessionCommandResult(
    bool SessionFound,
    bool Accepted,
    string Message);

internal static class NeteaseMediaSessionController
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;

    public static async Task<NeteaseMediaSessionCommandResult> ExecuteAsync(
        PlayerCommand command,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = await GetManagerAsync().ConfigureAwait(false);
            var session = manager.GetSessions().FirstOrDefault(candidate =>
                IsNeteaseSession(candidate.SourceAppUserModelId));
            if (session is null)
            {
                return new NeteaseMediaSessionCommandResult(
                    false,
                    false,
                    "没有发现网易云 Windows 媒体会话。");
            }

            var accepted = command switch
            {
                PlayerCommand.Pause => await session.TryPauseAsync(),
                PlayerCommand.Resume => await session.TryPlayAsync(),
                _ => false
            };
            return new NeteaseMediaSessionCommandResult(
                true,
                accepted,
                accepted
                    ? $"网易云媒体会话已接收明确的 {command} 指令。"
                    : $"网易云媒体会话拒绝 {command} 指令。");
        }
        catch (Exception exception)
        {
            _manager = null;
            return new NeteaseMediaSessionCommandResult(
                false,
                false,
                $"网易云媒体会话异常：{exception.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<
        GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        _manager ??=
            await GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync();
        return _manager;
    }

    private static bool IsNeteaseSession(string sourceAppUserModelId)
    {
        return sourceAppUserModelId.Contains(
                "cloudmusic",
                StringComparison.OrdinalIgnoreCase)
            || sourceAppUserModelId.Contains(
                "netease",
                StringComparison.OrdinalIgnoreCase)
            || sourceAppUserModelId.Contains(
                "orpheus",
                StringComparison.OrdinalIgnoreCase)
            || sourceAppUserModelId.Contains(
                "music.163",
                StringComparison.OrdinalIgnoreCase);
    }
}
