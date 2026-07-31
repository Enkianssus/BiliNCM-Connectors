using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace UnifiedPlayerControlPoc;

internal sealed record NeteaseBridgeCommandResult(
    bool Success,
    string Message,
    int? ProcessId,
    long? WindowHandle,
    string? MappingName,
    long ForegroundBefore,
    long ForegroundAfter,
    long DurationMilliseconds);

internal static class NeteaseInjectedBridgeClient
{
    private const string ProtocolVersion = "1";
    private static readonly object RequestSync = new();

    public static NeteaseBridgeCommandResult Pause() =>
        Send("PAUSE");

    public static NeteaseBridgeCommandResult Resume() =>
        Send("RESUME");

    public static NeteaseBridgeCommandResult PlaySong(string songId) =>
        Send($"PLAY {songId}");

    public static NeteaseBridgeCommandResult AddNext(string songId) =>
        Send($"ADD_NEXT {songId}");

    public static NeteaseBridgeStatus Probe()
    {
        lock (RequestSync)
        {
            var endpoint = NeteaseNativeIpc.FindEndpoint();
            if (endpoint is null)
            {
                return new NeteaseBridgeStatus(
                    false,
                    null,
                    "没有发现正在运行的网易云音乐。",
                    string.Empty);
            }

            var response = Exchange(
                endpoint.ProcessId,
                $"HELLO {ProtocolVersion}",
                350);
            var ready = response.Success
                && response.Response.StartsWith(
                    "OK READY",
                    StringComparison.Ordinal);
            return ready
                ? new NeteaseBridgeStatus(
                    true,
                    endpoint.ProcessId,
                    "进程内 CEF 桥已连接。",
                    response.Response)
                : new NeteaseBridgeStatus(
                    false,
                    endpoint.ProcessId,
                    response.Success
                        ? "进程内 CEF 桥已加载，但尚未取得有效的网易云 CEF 宿主；"
                          + "精确命令暂时禁用。"
                        : "进程内 CEF 桥尚未加载或没有响应；"
                          + "精确命令暂时禁用。",
                    response.Response);
        }
    }

    private static NeteaseBridgeCommandResult Send(string command)
    {
        lock (RequestSync)
        {
            var stopwatch = Stopwatch.StartNew();
            var foregroundBefore =
                NeteaseBridgeForeground.Read().ToInt64();
            var endpoint = NeteaseNativeIpc.FindEndpoint();
            if (endpoint is null)
            {
                return Failed(
                    "没有发现正在运行的网易云音乐。",
                    null,
                    null);
            }

            var exchange = Exchange(
                endpoint.ProcessId,
                command,
                3000);
            if (!exchange.Success)
            {
                return Failed(
                    "进程内 CEF 桥尚未就绪或没有响应；"
                    + "已拒绝命令，且不会回退到会弹窗的旧通道。"
                    + (string.IsNullOrWhiteSpace(exchange.Response)
                        ? string.Empty
                        : $" 桥响应：{exchange.Response}"),
                    endpoint.ProcessId,
                    endpoint.WindowHandle.ToInt64());
            }

            stopwatch.Stop();
            var accepted = exchange.Response.StartsWith(
                "OK",
                StringComparison.Ordinal);
            return new NeteaseBridgeCommandResult(
                accepted,
                accepted
                    ? $"进程内 CEF 桥已接收 {command.Split(' ')[0]}；"
                      + "是否播放成功仍由实际歌曲状态确认。"
                    : $"进程内 CEF 桥拒绝命令：{exchange.Response}",
                endpoint.ProcessId,
                endpoint.WindowHandle.ToInt64(),
                null,
                foregroundBefore,
                NeteaseBridgeForeground.Read().ToInt64(),
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);

            NeteaseBridgeCommandResult Failed(
                string message,
                int? processId,
                long? windowHandle)
            {
                stopwatch.Stop();
                return new NeteaseBridgeCommandResult(
                    false,
                    message,
                    processId,
                    windowHandle,
                    null,
                    foregroundBefore,
                    NeteaseBridgeForeground.Read().ToInt64(),
                    DurationMilliseconds:
                        stopwatch.ElapsedMilliseconds);
            }
        }
    }

    private static BridgeExchangeResult Exchange(
        int processId,
        string request,
        int timeoutMilliseconds)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                $"AwooNcmCefBridge-v{ProtocolVersion}-{processId}",
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(timeoutMilliseconds);

            var requestBytes = Encoding.UTF8.GetBytes(
                request + "\n");
            pipe.Write(requestBytes, 0, requestBytes.Length);
            pipe.Flush();

            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                false,
                1024,
                leaveOpen: true);
            using var readCancellation =
                new CancellationTokenSource(timeoutMilliseconds);
            var response = reader.ReadLineAsync(
                    readCancellation.Token)
                .GetAwaiter()
                .GetResult()
                ?? string.Empty;
            return new BridgeExchangeResult(
                true,
                response.Trim());
        }
        catch (Exception exception)
            when (exception is TimeoutException
                  or OperationCanceledException
                  or IOException
                  or InvalidOperationException)
        {
            return new BridgeExchangeResult(
                false,
                exception.Message);
        }
    }

    private sealed record BridgeExchangeResult(
        bool Success,
        string Response);
}

internal sealed record NeteaseBridgeStatus(
    bool Connected,
    int? ProcessId,
    string Message,
    string Details);

internal static class NeteaseBridgeForeground
{
    public static nint Read() => GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
