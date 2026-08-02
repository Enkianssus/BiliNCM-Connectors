using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace UnifiedPlayerControlPoc;

internal enum KugouPlayerEventKind
{
    Initialized,
    WindowTitleChanged,
    WindowStateChanged,
    IniChanged,
    SnapshotInvalidated
}

internal sealed record KugouPlayerEvent(
    KugouPlayerEventKind Kind,
    DateTimeOffset ObservedAt,
    nint WindowHandle = default,
    string WindowTitle = "");

/// <summary>
/// Broadcasts KuGou window and KuGou.ini changes without polling.  The
/// WinEvent hook is owned by a thread with a native message pump, while the
/// INI watcher is created only when the KuGou8 directory already exists.
/// </summary>
internal sealed class KugouEventMonitor : IAsyncDisposable
{
    private const string KugouProcessName = "KuGou";
    private const int SubscriberCapacity = 64;

    private static readonly string IniDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KuGou8");
    private static readonly string IniFilePath = Path.Combine(
        IniDirectoryPath,
        "KuGou.ini");

    private readonly object _startSync = new();
    private readonly object _subscriberSync = new();
    private readonly object _sourceSync = new();
    private readonly Dictionary<long, Channel<KugouPlayerEvent>> _subscribers =
        [];

    private Task? _startTask;
    private long _nextSubscriberId;
    private volatile bool _disposed;
    private WinEventNameChangeHook? _windowHook;
    private FileSystemWatcher? _iniWatcher;
    private bool _iniWatcherAvailable;
    private string _iniWatcherStatus = "unavailable (not started)";

    public string SourceStatus
    {
        get
        {
            var winEventStatus = _windowHook?.IsActive == true
                ? "available"
                : "unavailable";
            string iniStatus;
            lock (_sourceSync)
            {
                iniStatus = _iniWatcherStatus;
                if (_iniWatcherAvailable)
                {
                    iniStatus = "available";
                }
            }
            return $"WinEventHook: {winEventStatus}; "
                + $"FileSystemWatcher: {iniStatus}";
        }
    }

    public Task EnsureStartedAsync()
    {
        lock (_startSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= StartAsync();
        }
    }

    public KugouEventSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<KugouPlayerEvent>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });

        long id;
        lock (_subscriberSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextSubscriberId;
            _subscribers[id] = channel;
        }

        return new KugouEventSubscription(
            channel.Reader,
            () => RemoveSubscriber(id));
    }

    public void NotifySnapshotInvalidated()
    {
        Publish(new KugouPlayerEvent(
            KugouPlayerEventKind.SnapshotInvalidated,
            DateTimeOffset.Now));
    }

    public async ValueTask DisposeAsync()
    {
        Task? startTask;
        lock (_startSync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            startTask = _startTask;
        }

        WinEventNameChangeHook? windowHook;
        FileSystemWatcher? iniWatcher;
        lock (_sourceSync)
        {
            windowHook = _windowHook;
            _windowHook = null;
            iniWatcher = _iniWatcher;
            _iniWatcher = null;
            _iniWatcherAvailable = false;
            _iniWatcherStatus = "disposed";
        }

        DisposeIniWatcher(iniWatcher);
        windowHook?.Dispose();

        Channel<KugouPlayerEvent>[] channels;
        lock (_subscriberSync)
        {
            channels = [.. _subscribers.Values];
            _subscribers.Clear();
        }
        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }

        if (startTask is not null)
        {
            try
            {
                await startTask.ConfigureAwait(false);
            }
            catch
            {
                // Startup is best-effort.  Disposal must still complete all
                // subscriber channels even when a native API failed.
            }
        }
    }

    private Task StartAsync()
    {
        // Install native callbacks off the caller's synchronization context;
        // WinEventNameChangeHook.Start waits briefly for the hook thread to
        // create its message queue.
        return Task.Run(StartCore);
    }

    private void StartCore()
    {
        WinEventNameChangeHook? windowHook = null;
        try
        {
            windowHook = new WinEventNameChangeHook(OnWindowEvent);
            windowHook.Start();

            var disposeImmediately = false;
            lock (_sourceSync)
            {
                if (_disposed)
                {
                    disposeImmediately = true;
                }
                else
                {
                    _windowHook = windowHook;
                }
            }
            if (disposeImmediately)
            {
                windowHook.Dispose();
                return;
            }

            // If KuGou8 does not exist yet, this records an unavailable
            // source and deliberately does not create that directory.  A
            // later accepted KuGou window event retries this call.
            TryEnsureIniWatcher();
            Publish(new KugouPlayerEvent(
                KugouPlayerEventKind.Initialized,
                DateTimeOffset.Now));
        }
        catch
        {
            windowHook?.Dispose();
            lock (_sourceSync)
            {
                if (_windowHook is null)
                {
                    _iniWatcherStatus = _iniWatcherStatus
                        == "unavailable (not started)"
                        ? "unavailable (startup error)"
                        : _iniWatcherStatus;
                }
            }
            // A monitor remains useful with either source unavailable; the
            // status string tells callers which source could not be installed.
            Publish(new KugouPlayerEvent(
                KugouPlayerEventKind.Initialized,
                DateTimeOffset.Now));
        }
    }

    private void OnWindowEvent(uint eventType, nint window, string title)
    {
        // Window events are the only retry trigger when KuGou8 is created
        // after startup; there is intentionally no timer or polling loop.
        TryEnsureIniWatcher();

        Publish(new KugouPlayerEvent(
            eventType == WinEventNameChangeHook.EventObjectNameChange
                ? KugouPlayerEventKind.WindowTitleChanged
                : KugouPlayerEventKind.WindowStateChanged,
            DateTimeOffset.Now,
            window,
            title));
    }

    private void TryEnsureIniWatcher()
    {
        lock (_sourceSync)
        {
            if (_disposed || _iniWatcher is not null)
            {
                return;
            }

            if (!Directory.Exists(IniDirectoryPath))
            {
                _iniWatcherAvailable = false;
                _iniWatcherStatus = "unavailable (directory missing)";
                return;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(
                    IniDirectoryPath,
                    Path.GetFileName(IniFilePath))
                {
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 16 * 1024
                };
                watcher.Changed += OnIniChanged;
                watcher.Created += OnIniChanged;
                watcher.Deleted += OnIniChanged;
                watcher.Renamed += OnIniRenamed;
                watcher.Error += OnIniError;
                watcher.EnableRaisingEvents = true;

                _iniWatcher = watcher;
                _iniWatcherAvailable = true;
                _iniWatcherStatus = "available";
            }
            catch (Exception exception)
            {
                DisposeIniWatcher(watcher);
                _iniWatcherAvailable = false;
                _iniWatcherStatus =
                    $"unavailable ({exception.GetType().Name})";
            }
        }
    }

    private void OnIniChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsIniPath(args.FullPath))
        {
            return;
        }

        Publish(new KugouPlayerEvent(
            KugouPlayerEventKind.IniChanged,
            DateTimeOffset.Now));
    }

    private void OnIniRenamed(object sender, RenamedEventArgs args)
    {
        if (!IsIniPath(args.FullPath) && !IsIniPath(args.OldFullPath))
        {
            return;
        }

        Publish(new KugouPlayerEvent(
            KugouPlayerEventKind.IniChanged,
            DateTimeOffset.Now));
    }

    private void OnIniError(object sender, ErrorEventArgs args)
    {
        // An Error event can indicate an overflow or that the watched
        // directory disappeared.  Invalidate the current snapshot and drop
        // this watcher so a subsequent KuGou window event can recreate it.
        Publish(new KugouPlayerEvent(
            KugouPlayerEventKind.IniChanged,
            DateTimeOffset.Now));

        FileSystemWatcher? watcher = null;
        lock (_sourceSync)
        {
            if (ReferenceEquals(sender, _iniWatcher))
            {
                watcher = _iniWatcher;
                _iniWatcher = null;
                _iniWatcherAvailable = false;
                _iniWatcherStatus = "unavailable (error)";
            }
        }
        DisposeIniWatcher(watcher);
    }

    private static bool IsIniPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(path),
                IniFilePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeIniWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch
        {
            // The watcher may already have faulted or have been disposed.
        }
        watcher.Dispose();
    }

    private void Publish(KugouPlayerEvent playerEvent)
    {
        Channel<KugouPlayerEvent>[] channels;
        lock (_subscriberSync)
        {
            if (_disposed)
            {
                return;
            }
            channels = [.. _subscribers.Values];
        }

        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(playerEvent);
        }
    }

    private void RemoveSubscriber(long id)
    {
        Channel<KugouPlayerEvent>? channel;
        lock (_subscriberSync)
        {
            _subscribers.Remove(id, out channel);
        }
        channel?.Writer.TryComplete();
    }

    private sealed class WinEventNameChangeHook : IDisposable
    {
        internal const uint EventObjectNameChange = 0x800C;
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002;
        private const uint EventObjectHide = 0x8003;
        private const int ObjectIdWindow = 0;
        private const uint WinEventOutOfContext = 0;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const uint WindowMessageQuit = 0x0012;
        private const uint PeekMessageNoRemove = 0;

        private readonly Action<uint, nint, string> _onWindowEvent;
        private readonly HashSet<nint> _knownKugouWindows = [];
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private WinEventDelegate? _callback;
        private nint _nameHook;
        private nint _lifecycleHook;
        private uint _threadId;
        private int _disposed;

        internal WinEventNameChangeHook(
            Action<uint, nint, string> onWindowEvent)
        {
            _onWindowEvent = onWindowEvent;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "KuGou WinEventHook"
            };
        }

        internal bool IsActive => _nameHook != 0;

        internal void Start()
        {
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
            {
                _ = PostThreadMessage(threadId, WindowMessageQuit, 0, 0);
            }
            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }

        private void ThreadMain()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                // Force creation of this thread's native message queue before
                // signaling readiness, avoiding a WM_QUIT posting race.
                _ = PeekMessage(
                    out _,
                    0,
                    0,
                    0,
                    PeekMessageNoRemove);

                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _callback = OnWinEvent;
                _nameHook = SetWinEventHook(
                    EventObjectNameChange,
                    EventObjectNameChange,
                    0,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext | WinEventSkipOwnProcess);
                _lifecycleHook = SetWinEventHook(
                    EventObjectCreate,
                    EventObjectHide,
                    0,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext | WinEventSkipOwnProcess);

                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _ready.Set();

                if (_nameHook == 0 && _lifecycleHook == 0)
                {
                    return;
                }

                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    _ = TranslateMessage(in message);
                    _ = DispatchMessage(in message);
                }
            }
            catch
            {
                // Native APIs can be unavailable on non-Windows test hosts or
                // under a restricted desktop.  SourceStatus then reports the
                // hook as unavailable while the process remains healthy.
            }
            finally
            {
                _ready.Set();
                if (_nameHook != 0)
                {
                    _ = UnhookWinEvent(_nameHook);
                    _nameHook = 0;
                }
                if (_lifecycleHook != 0)
                {
                    _ = UnhookWinEvent(_lifecycleHook);
                    _lifecycleHook = 0;
                }
            }
        }

        private void OnWinEvent(
            nint hook,
            uint eventType,
            nint window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            try
            {
                if (window == 0 || objectId != ObjectIdWindow || childId != 0)
                {
                    return;
                }

                _ = GetWindowThreadProcessId(window, out var processId);
                var isKugou = processId != 0 && IsKugouProcess(processId);
                if (isKugou)
                {
                    _knownKugouWindows.Add(window);
                }
                else if (!_knownKugouWindows.Contains(window))
                {
                    return;
                }

                var title = eventType == EventObjectDestroy
                    ? string.Empty
                    : ReadWindowText(window);
                _onWindowEvent(eventType, window, title);
                if (eventType == EventObjectDestroy)
                {
                    _knownKugouWindows.Remove(window);
                }
            }
            catch
            {
                // Never allow an exception to unwind through the unmanaged
                // WinEvent callback boundary.
            }
        }

        private static bool IsKugouProcess(uint processId)
        {
            try
            {
                using var process = Process.GetProcessById(
                    checked((int)processId));
                return process.ProcessName.Equals(
                    KugouProcessName,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadWindowText(nint window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder(length + 1);
            _ = GetWindowText(window, text, text.Capacity);
            return text.ToString().Trim();
        }

        private delegate void WinEventDelegate(
            nint hook,
            uint eventType,
            nint window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativePoint
        {
            internal readonly int X;
            internal readonly int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeMessage
        {
            internal readonly nint Window;
            internal readonly uint Message;
            internal readonly nuint WParam;
            internal readonly nint LParam;
            internal readonly uint Time;
            internal readonly NativePoint Point;
            internal readonly uint Private;
        }

        [DllImport("user32.dll")]
        private static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint module,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(nint hook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(
            out NativeMessage message,
            nint window,
            uint messageMin,
            uint messageMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(
            out NativeMessage message,
            nint window,
            uint messageMin,
            uint messageMax,
            uint removeMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(in NativeMessage message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(in NativeMessage message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            nuint wParam,
            nint lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint window);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern int GetWindowText(
            nint window,
            StringBuilder text,
            int maximum);
    }
}

internal sealed class KugouEventSubscription : IAsyncDisposable
{
    private Action? _dispose;

    internal KugouEventSubscription(
        ChannelReader<KugouPlayerEvent> reader,
        Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<KugouPlayerEvent> Reader { get; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
