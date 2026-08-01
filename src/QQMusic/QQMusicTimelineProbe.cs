using Windows.Media.Control;

namespace UnifiedPlayerControlPoc;

internal sealed record QQMusicTimelineSnapshot(
    string SourceAppUserModelId,
    string PlaybackStatus,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan ReportedPosition,
    DateTimeOffset LastUpdatedTime,
    TimeSpan ElapsedSinceUpdate,
    TimeSpan EstimatedPosition,
    TimeSpan EstimatedRemaining,
    string EstimationSource);

/// <summary>
/// Uses the public Windows media-session timeline only as an early-warning
/// signal. QQ playback commands still go through the existing native adapter.
/// </summary>
internal sealed class QQMusicTimelineProbe
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly object _clockSync = new();
    private bool _fallbackClockActive;
    private DateTimeOffset _fallbackObservedAt;
    private DateTimeOffset _fallbackSourceUpdatedAt;
    private TimeSpan _fallbackSourcePosition;
    private TimeSpan _fallbackSourceEndTime;

    private QQMusicTimelineProbe(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
    }

    public static async Task<QQMusicTimelineProbe?> TryCreateAsync()
    {
        try
        {
            var manager =
                await GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync();
            return new QQMusicTimelineProbe(manager);
        }
        catch
        {
            return null;
        }
    }

    public bool IsPlayingNearNaturalEnd(
        TimeSpan threshold,
        out TimeSpan remaining)
    {
        remaining = TimeSpan.MaxValue;
        var snapshot = ReadSnapshot();
        if (snapshot is null
            || !snapshot.PlaybackStatus.Equals(
                GlobalSystemMediaTransportControlsSessionPlaybackStatus
                    .Playing.ToString(),
                StringComparison.Ordinal)
            || snapshot.EndTime <= snapshot.StartTime
            || snapshot.ReportedPosition < snapshot.StartTime
            || snapshot.ReportedPosition > snapshot.EndTime)
        {
            return false;
        }

        remaining = snapshot.EstimatedRemaining;
        // Keep a small negative tolerance so a delayed scheduler tick still
        // mutes before QQ's title notification catches up with audio output.
        return remaining <= threshold
            && remaining >= TimeSpan.FromSeconds(-1);
    }

    public QQMusicTimelineSnapshot? ReadSnapshot()
    {
        try
        {
            var session = _manager.GetSessions()
                .FirstOrDefault(candidate =>
                    candidate.SourceAppUserModelId.Contains(
                        "qqmusic",
                        StringComparison.OrdinalIgnoreCase));
            if (session is null)
            {
                return null;
            }

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var elapsedSinceUpdate =
                DateTimeOffset.Now - timeline.LastUpdatedTime;
            var (estimatedPosition, estimationSource) = EstimatePosition(
                playback.PlaybackStatus,
                timeline.Position,
                timeline.EndTime,
                timeline.LastUpdatedTime,
                elapsedSinceUpdate,
                DateTimeOffset.Now);

            return new QQMusicTimelineSnapshot(
                session.SourceAppUserModelId,
                playback.PlaybackStatus.ToString(),
                timeline.StartTime,
                timeline.EndTime,
                timeline.Position,
                timeline.LastUpdatedTime,
                elapsedSinceUpdate,
                estimatedPosition,
                timeline.EndTime - estimatedPosition,
                estimationSource);
        }
        catch
        {
            return null;
        }
    }

    private (TimeSpan Position, string Source) EstimatePosition(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status,
        TimeSpan reportedPosition,
        TimeSpan endTime,
        DateTimeOffset lastUpdatedTime,
        TimeSpan elapsedSinceUpdate,
        DateTimeOffset observedAt)
    {
        lock (_clockSync)
        {
            if (status
                != GlobalSystemMediaTransportControlsSessionPlaybackStatus
                    .Playing)
            {
                _fallbackClockActive = false;
                return (reportedPosition, "reported-paused");
            }

            var timestampEstimate = reportedPosition;
            if (elapsedSinceUpdate > TimeSpan.Zero)
            {
                timestampEstimate += elapsedSinceUpdate;
            }

            // Accept the Windows timestamp whenever it maps to this track's
            // plausible playback range. QQ may leave a paused timestamp behind
            // after resume; in that case it can be minutes past the track end.
            if (elapsedSinceUpdate >= TimeSpan.Zero
                && timestampEstimate <= endTime + TimeSpan.FromSeconds(2))
            {
                _fallbackClockActive = false;
                return (timestampEstimate, "windows-timestamp");
            }

            var sourceChanged = !_fallbackClockActive
                || _fallbackSourceUpdatedAt != lastUpdatedTime
                || _fallbackSourcePosition != reportedPosition
                || _fallbackSourceEndTime != endTime;
            if (sourceChanged)
            {
                _fallbackClockActive = true;
                _fallbackObservedAt = observedAt;
                _fallbackSourceUpdatedAt = lastUpdatedTime;
                _fallbackSourcePosition = reportedPosition;
                _fallbackSourceEndTime = endTime;
            }

            return (
                _fallbackSourcePosition
                    + (observedAt - _fallbackObservedAt),
                "local-fallback-clock");
        }
    }
}
