using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UnifiedPlayerControlPoc;

internal sealed record NeteaseBridgeInstallResult(
    bool Success,
    bool Loaded,
    int? ProcessId,
    string Message,
    string Details,
    long ForegroundBefore,
    long ForegroundAfter,
    long DurationMilliseconds);

internal static class NeteaseBridgeInstaller
{
    private const string SupportedPlayerVersion = "3.1.37.205354";
    private const string SupportedPlayerSha256 =
        "8DE6AE8AF8FEFB141FE49135BA4B5ED9FAF0457502470F283304E336A121B360";
    private const string SupportedCefSha256 =
        "724B3E35EDB5905540877FA8D7A8583A2503599639D7C79CCEF6FAA8E5A6BC49";

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0;
    private const uint Th32CsSnapModule = 0x00000008;
    private const uint Th32CsSnapModule32 = 0x00000010;
    private const uint GetModuleHandleExFromAddress = 0x00000004;
    private const uint GetModuleHandleExUnchangedRefcount = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    public static NeteaseBridgeInstallResult Install()
    {
        var stopwatch = Stopwatch.StartNew();
        var foregroundBefore = GetForegroundWindow().ToInt64();
        var alreadyConnected = NeteaseInjectedBridgeClient.Probe();
        if (alreadyConnected.Connected)
        {
            return Finish(
                true,
                false,
                alreadyConnected.ProcessId,
                "进程内 CEF 桥已经连接，无需重复加载。",
                alreadyConnected.Details);
        }

        var endpoint = NeteaseNativeIpc.FindEndpoint();
        if (endpoint is null)
        {
            return Finish(
                false,
                false,
                null,
                "没有发现正在运行的网易云音乐。",
                string.Empty);
        }
        _ = GetWindowThreadProcessId(
            endpoint.WindowHandle,
            out var windowOwnerProcessId);
        if (endpoint.WindowHandle == nint.Zero
            || windowOwnerProcessId != endpoint.ProcessId)
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "没有可验证归属的网易云播放器主窗口，已拒绝注入。",
                $"window=0x{endpoint.WindowHandle.ToInt64():X}, "
                + $"owner={windowOwnerProcessId}");
        }

        string playerPath;
        try
        {
            using var process =
                Process.GetProcessById(endpoint.ProcessId);
            playerPath =
                process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception exception)
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "无法读取网易云主进程路径，已拒绝注入。",
                exception.Message);
        }
        if (string.IsNullOrWhiteSpace(playerPath)
            || !File.Exists(playerPath))
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "网易云主程序路径无效，已拒绝注入。",
                playerPath);
        }

        var playerVersion =
            FileVersionInfo.GetVersionInfo(playerPath).FileVersion
            ?? string.Empty;
        var cefPath = Path.Combine(
            Path.GetDirectoryName(playerPath)
                ?? string.Empty,
            "libcef.dll");
        if (!playerVersion.Equals(
                SupportedPlayerVersion,
                StringComparison.Ordinal)
            || !File.Exists(cefPath))
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "网易云或 CEF 版本不在当前安全白名单，已拒绝注入。",
                $"player={playerVersion}, cef={cefPath}");
        }

        var playerHash = ComputeSha256(playerPath);
        var cefHash = ComputeSha256(cefPath);
        if (!playerHash.Equals(
                SupportedPlayerSha256,
                StringComparison.OrdinalIgnoreCase)
            || !cefHash.Equals(
                SupportedCefSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "网易云或 libcef.dll 哈希不匹配，已拒绝注入。",
                $"playerSha256={playerHash}, cefSha256={cefHash}");
        }

        var bridgePath = Path.Combine(
            AppContext.BaseDirectory,
            "bridge",
            "AwooNcmCefBridge.dll");
        if (!File.Exists(bridgePath))
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "没有找到本地 CEF 桥 DLL。",
                bridgePath);
        }

        var existingBridge =
            FindExistingBridgeModule(endpoint.ProcessId);
        if (!string.IsNullOrWhiteSpace(existingBridge))
        {
            var sameBridge = Path.GetFullPath(existingBridge)
                .Equals(
                    Path.GetFullPath(bridgePath),
                    StringComparison.OrdinalIgnoreCase);
            return Finish(
                false,
                true,
                endpoint.ProcessId,
                sameBridge
                    ? "CEF 桥 DLL 已在网易云中加载，但当前 CEF 宿主校验未通过。"
                    : "检测到另一个测试版本的 CEF 桥仍在网易云中；"
                      + "为避免同一进程加载两个桥，已拒绝覆盖。"
                      + "请正常关闭并重新打开一次网易云后再测试新版。",
                existingBridge);
        }

        var processHandle = OpenProcess(
            ProcessCreateThread
            | ProcessVmOperation
            | ProcessVmRead
            | ProcessVmWrite
            | ProcessQueryInformation,
            false,
            endpoint.ProcessId);
        if (processHandle == nint.Zero)
        {
            return Finish(
                false,
                false,
                endpoint.ProcessId,
                "无法打开网易云主进程。",
                Win32Message());
        }

        nint remotePath = nint.Zero;
        nint remoteThread = nint.Zero;
        try
        {
            var pathBytes =
                Encoding.Unicode.GetBytes(bridgePath + "\0");
            remotePath = VirtualAllocEx(
                processHandle,
                nint.Zero,
                (nuint)pathBytes.Length,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remotePath == nint.Zero)
            {
                return Finish(
                    false,
                    false,
                    endpoint.ProcessId,
                    "无法在网易云进程中分配桥路径内存。",
                    Win32Message());
            }
            if (!WriteProcessMemory(
                    processHandle,
                    remotePath,
                    pathBytes,
                    (nuint)pathBytes.Length,
                    out var bytesWritten)
                || bytesWritten != (nuint)pathBytes.Length)
            {
                return Finish(
                    false,
                    false,
                    endpoint.ProcessId,
                    "无法写入桥 DLL 路径。",
                    Win32Message());
            }

            var loadLibrary =
                ResolveRemoteLoadLibrary(endpoint.ProcessId);
            if (loadLibrary == nint.Zero)
            {
                return Finish(
                    false,
                    false,
                    endpoint.ProcessId,
                    "无法解析网易云进程中的 LoadLibraryW。",
                    Win32Message());
            }

            remoteThread = CreateRemoteThread(
                processHandle,
                nint.Zero,
                0,
                loadLibrary,
                remotePath,
                0,
                out _);
            if (remoteThread == nint.Zero)
            {
                return Finish(
                    false,
                    false,
                    endpoint.ProcessId,
                    "创建桥加载线程失败。",
                    Win32Message());
            }
            if (WaitForSingleObject(remoteThread, 10000)
                != WaitObject0)
            {
                return Finish(
                    false,
                    true,
                    endpoint.ProcessId,
                    "桥加载线程没有在 10 秒内结束。",
                    string.Empty);
            }
        }
        finally
        {
            if (remoteThread != nint.Zero)
            {
                _ = CloseHandle(remoteThread);
            }
            if (remotePath != nint.Zero)
            {
                _ = VirtualFreeEx(
                    processHandle,
                    remotePath,
                    0,
                    MemRelease);
            }
            _ = CloseHandle(processHandle);
        }

        NeteaseBridgeStatus status = new(
            false,
            endpoint.ProcessId,
            "等待桥接器",
            string.Empty);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(150);
            status = NeteaseInjectedBridgeClient.Probe();
            if (status.Connected)
            {
                return Finish(
                    true,
                    true,
                    endpoint.ProcessId,
                    "CEF 桥已加载并连接到存活的内部 DevTools 宿主。",
                    status.Details);
            }
        }

        return Finish(
            false,
            true,
            endpoint.ProcessId,
            "CEF 桥 DLL 已加载，但尚未取得可用的 CEF 宿主。"
            + "请确认网易云主界面已经完整加载；"
            + "当前不会发送任何播放命令。",
            status.Details);

        NeteaseBridgeInstallResult Finish(
            bool success,
            bool loaded,
            int? processId,
            string message,
            string details)
        {
            stopwatch.Stop();
            return new NeteaseBridgeInstallResult(
                success,
                loaded,
                processId,
                message,
                details,
                foregroundBefore,
                GetForegroundWindow().ToInt64(),
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static nint ResolveRemoteLoadLibrary(int processId)
    {
        var localKernel32 = GetModuleHandle("kernel32.dll");
        var localLoadLibrary =
            GetProcAddress(localKernel32, "LoadLibraryW");
        if (localKernel32 == nint.Zero
            || localLoadLibrary == nint.Zero
            || !GetModuleHandleEx(
                GetModuleHandleExFromAddress
                | GetModuleHandleExUnchangedRefcount,
                localLoadLibrary,
                out var localOwner)
            || localOwner == nint.Zero)
        {
            return nint.Zero;
        }

        var ownerPath = new StringBuilder(260);
        if (GetModuleFileName(
                localOwner,
                ownerPath,
                ownerPath.Capacity) == 0)
        {
            return nint.Zero;
        }
        var ownerName = Path.GetFileName(ownerPath.ToString());
        var remoteOwner =
            FindRemoteModule(processId, ownerName);
        if (remoteOwner == nint.Zero)
        {
            return nint.Zero;
        }

        var offset =
            localLoadLibrary.ToInt64() - localOwner.ToInt64();
        return new nint(remoteOwner.ToInt64() + offset);
    }

    private static string FindExistingBridgeModule(int processId)
    {
        try
        {
            using var process =
                Process.GetProcessById(processId);
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName.Equals(
                        "AwooNcmCefBridge.dll",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return module.FileName;
                }
            }
        }
        catch
        {
            // The subsequent OpenProcess/version checks will provide the
            // actionable error if module enumeration is not permitted.
        }
        return string.Empty;
    }

    private static nint FindRemoteModule(
        int processId,
        string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(
            Th32CsSnapModule | Th32CsSnapModule32,
            processId);
        if (snapshot == InvalidHandleValue)
        {
            return nint.Zero;
        }
        try
        {
            var entry = new ModuleEntry32
            {
                Size = (uint)Marshal.SizeOf<ModuleEntry32>()
            };
            if (!Module32First(snapshot, ref entry))
            {
                return nint.Zero;
            }
            do
            {
                if (entry.Module.Equals(
                        moduleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return entry.ModuleBaseAddress;
                }
                entry.Size =
                    (uint)Marshal.SizeOf<ModuleEntry32>();
            } while (Module32Next(snapshot, ref entry));
            return nint.Zero;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static string Win32Message()
    {
        var error = Marshal.GetLastPInvokeError();
        return $"Win32={error}: "
               + new Win32Exception(error).Message;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsageCount;
        public uint ProcessUsageCount;
        public nint ModuleBaseAddress;
        public uint ModuleBaseSize;
        public nint ModuleHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Module;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(
        nint process,
        nint address,
        nuint size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        nint process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        nint process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint numberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(
        nint process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        nint handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleEx(
        uint flags,
        nint moduleNameOrAddress,
        out nint module);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetModuleFileName(
        nint module,
        StringBuilder fileName,
        int size);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Ansi,
        SetLastError = true)]
    private static extern nint GetProcAddress(
        nint module,
        string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(
        uint flags,
        int processId);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32First(
        nint snapshot,
        ref ModuleEntry32 entry);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32Next(
        nint snapshot,
        ref ModuleEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out int processId);
}

