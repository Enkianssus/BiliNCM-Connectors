namespace UnifiedPlayerControlPoc;

/// <summary>
/// Watches KuGou's native event stream while a next-track request is armed.
/// Unlike the shared compatibility monitor this class never uses a repeating
/// timer: an event is the only trigger that causes a current-track read.
/// </summary>
internal sealed class KugouGuardedNextMonitor : IDisposable
{
    private static readonly TimeSpan BurstCoalesceWindow =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaximumLifetime =
        TimeSpan.FromHours(12);

    private readonly KugouEventMonitor _eventMonitor;
    private readonly Action _snapshotInvalidated;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private volatile string _status = string.Empty;

    public KugouGuardedNextMonitor(
        KugouEventMonitor eventMonitor,
        Action snapshotInvalidated)
    {
        _eventMonitor = eventMonitor
            ?? throw new ArgumentNullException(nameof(eventMonitor));
        _snapshotInvalidated = snapshotInvalidated
            ?? throw new ArgumentNullException(nameof(snapshotInvalidated));
    }

    public string Status => _status;

    public bool Arm(
        PlayerTrack? current,
        PlayerTrack target,
        Func<CancellationToken, Task<PlayerTrack?>> readCurrent,
        Func<PlayerTrack, CancellationToken, Task<string>> takeOver,
        CancellationToken lifetimeToken,
        out string message)
    {
        if (current is null || string.IsNullOrWhiteSpace(current.Title))
        {
            message = "当前歌曲不可识别，无法启动下一首守卫。";
            return false;
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(takeOver);

        var owner =
            CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous;
        var statusChanged = false;
        lock (_sync)
        {
            previous = _cancellation;
            _cancellation = owner;
            var status = $"下一首守卫待命：{target.DisplayName}";
            statusChanged = _status != status;
            _status = status;

            // MonitorAsync executes synchronously until its first incomplete
            // await. Its first operation is Subscribe(), so the subscription
            // is installed before EnsureStartedAsync() is requested.
            _ = MonitorAsync(
                owner,
                current,
                target,
                readCurrent,
                takeOver);
        }

        if (statusChanged)
        {
            NotifySnapshotInvalidated();
        }

        CancelOwner(previous);

        message =
            $"下一首守卫已启动：若实际下一首不是 {target.DisplayName}，"
            + "将执行连接器的安全兜底并切换到目标。";
        return true;
    }

    public void Cancel(string status = "")
    {
        CancellationTokenSource? cancellation;
        var statusChanged = false;
        lock (_sync)
        {
            cancellation = _cancellation;
            _cancellation = null;
            statusChanged = _status != status;
            _status = status;
        }

        if (statusChanged)
        {
            NotifySnapshotInvalidated();
        }

        CancelOwner(cancellation);
    }

    public void Dispose()
    {
        Cancel();
    }

    private async Task MonitorAsync(
        CancellationTokenSource owner,
        PlayerTrack initial,
        PlayerTrack target,
        Func<CancellationToken, Task<PlayerTrack?>> readCurrent,
        Func<PlayerTrack, CancellationToken, Task<string>> takeOver)
    {
        var cancellationToken = owner.Token;
        try
        {
            // Subscribe before EnsureStartedAsync. This prevents the monitor's
            // initial event from being lost during startup.
            var subscription = _eventMonitor.Subscribe();
            await using (subscription)
            {
                // A single delay is only an expiry deadline; it never drives
                // readCurrent. Track reads remain strictly event-triggered.
                var expiryTask = Task.Delay(
                    MaximumLifetime,
                    cancellationToken);
                await _eventMonitor.EnsureStartedAsync().ConfigureAwait(false);

                while (true)
                {
                    var eventTask = subscription.Reader
                        .WaitToReadAsync(cancellationToken)
                        .AsTask();
                    var completed = await Task.WhenAny(
                            eventTask,
                            expiryTask)
                        .ConfigureAwait(false);
                    if (ReferenceEquals(completed, expiryTask))
                    {
                        SetStatus(
                            owner,
                            "下一首守卫已过期（12 小时未发生切歌）");
                        return;
                    }

                    if (!await eventTask.ConfigureAwait(false))
                    {
                        return;
                    }

                    // KuGou commonly emits title/window and INI notifications
                    // as one burst. Debounce this one event burst and perform
                    // exactly one read; this is not a periodic poll.
                    await Task.Delay(
                            BurstCoalesceWindow,
                            cancellationToken)
                        .ConfigureAwait(false);
                    while (subscription.Reader.TryRead(out _))
                    {
                        // Drain notifications raised during the coalesce window.
                    }

                    if (!IsCurrent(owner, cancellationToken))
                    {
                        return;
                    }

                    var observed = await readCurrent(cancellationToken)
                        .ConfigureAwait(false);
                    if (!IsCurrent(owner, cancellationToken))
                    {
                        return;
                    }

                    if (observed is null || SameTrack(observed, initial))
                    {
                        continue;
                    }

                    if (SameTrack(observed, target))
                    {
                        SetStatus(
                            owner,
                            $"下一首已正确命中：{target.DisplayName}");
                        return;
                    }

                    SetStatus(
                        owner,
                        $"检测到错误下一首：{observed.DisplayName}；正在兜底接管");
                    if (!IsCurrent(owner, cancellationToken))
                    {
                        return;
                    }

                    // This invocation is the terminal action for the guard.
                    // The method returns immediately after it, so subsequent
                    // native events cannot cause a second takeover.
                    var result = await takeOver(target, cancellationToken)
                        .ConfigureAwait(false);
                    SetStatus(owner, result);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced, cancelled, or the application is closing.
        }
        catch (Exception exception)
        {
            SetStatus(owner, $"下一首守卫异常：{exception.Message}");
        }
        finally
        {
            // Cancelling here releases the one-shot expiry delay as soon as a
            // target/error/closed-subscription path has completed.
            CancelOwner(owner);
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, owner))
                {
                    _cancellation = null;
                }
            }

            owner.Dispose();
        }
    }

    private bool IsCurrent(
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        lock (_sync)
        {
            return ReferenceEquals(_cancellation, owner);
        }
    }

    private void SetStatus(
        CancellationTokenSource owner,
        string status)
    {
        var changed = false;
        lock (_sync)
        {
            if (ReferenceEquals(_cancellation, owner))
            {
                changed = _status != status;
                _status = status;
            }
        }

        if (changed)
        {
            NotifySnapshotInvalidated();
        }
    }

    private void NotifySnapshotInvalidated()
    {
        try
        {
            _snapshotInvalidated();
        }
        catch
        {
            // Snapshot invalidation is advisory and must not terminate the
            // event guard if a host callback fails.
        }
    }

    private static void CancelOwner(CancellationTokenSource? owner)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another completion path already disposed this owner.
        }
        catch
        {
            // Cancellation callbacks belong to the caller; one faulty
            // callback must not break replacement or connector shutdown.
        }
    }

    private static bool SameTrack(PlayerTrack left, PlayerTrack right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id)
            && !string.IsNullOrWhiteSpace(right.Id))
        {
            return string.Equals(
                left.Id,
                right.Id,
                StringComparison.OrdinalIgnoreCase);
        }

        return Normalize(left.Title) == Normalize(right.Title)
            && (string.IsNullOrWhiteSpace(left.Artist)
                || string.IsNullOrWhiteSpace(right.Artist)
                || Normalize(left.Artist) == Normalize(right.Artist));
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }
}
