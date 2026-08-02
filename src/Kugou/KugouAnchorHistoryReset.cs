using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KugouControlPoc;

internal sealed record KugouAnchorResetAttempt(
    bool Succeeded,
    bool ProfileMatched,
    string Message,
    string PlayerVersion,
    string DllSha256)
{
    internal static KugouAnchorResetAttempt Skipped(string reason) => new(
        false,
        false,
        reason,
        string.Empty,
        string.Empty);
}

/// <summary>
/// Exact-build-only invocation of KuGou's insertion-anchor history reset.
/// Every address and code path is validated against a signed-in-source profile
/// before a remote thread is created.  This class deliberately accepts a
/// caller-selected PID so the resolver and the reset can never target two
/// different KuGou processes.
/// </summary>
internal static class KugouAnchorHistoryReset
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteRead = 0x20;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const int ResolverDataOffset = 0x1000;
    private const int AnchorTrackerSourceOffset = 0x0C;
    private const int AnchorHistoryBeginOffset = 0x10;
    private const int AnchorHistoryEndOffset = 0x14;
    private const int AnchorHistoryCapacityOffset = 0x18;

    internal static KugouAnchorResetAttempt TryReset(int expectedProcessId)
    {
        if (IntPtr.Size != 4)
        {
            return KugouAnchorResetAttempt.Skipped(
                "酷狗无切歌锚点重置要求 x86 连接器；已回退旧兼容插入逻辑。请更新酷狗连接器。");
        }

        if (expectedProcessId <= 0)
        {
            return KugouAnchorResetAttempt.Skipped(
                "无法锁定酷狗目标进程 PID；已回退旧兼容插入逻辑。请更新酷狗连接器。");
        }

        Process? target = null;
        var playerVersion = string.Empty;
        var dllSha256 = string.Empty;
        var profileMatched = false;
        try
        {
            target = Process.GetProcessById(expectedProcessId);
            EnsureTargetProcess(target, expectedProcessId);
            var module = FindKugouModule(target);
            playerVersion = FileVersionInfo.GetVersionInfo(module.FileName)
                .FileVersion
                ?? string.Empty;
            dllSha256 = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(module.FileName)));
            var profile = KugouAnchorResetProfilePolicy.Find(
                playerVersion,
                dllSha256);
            if (profile is null)
            {
                return Failure(
                    playerVersion,
                    dllSha256,
                    profileMatched: false,
                    "酷狗版本或 kugou.dll SHA-256 不在已验证画像中");
            }

            profileMatched = true;
            using var processHandle = OpenProcess(
                ProcessCreateThread
                    | ProcessVmOperation
                    | ProcessVmRead
                    | ProcessVmWrite
                    | ProcessQueryInformation,
                false,
                expectedProcessId);
            if (processHandle.IsInvalid)
            {
                throw CreateWin32Exception("OpenProcess");
            }

            var moduleBase = module.BaseAddress;
            VerifyBytes(
                processHandle,
                nint.Add(moduleBase, profile.GetServiceRootRva),
                profile.GetServiceRootBytes,
                "GetServiceRoot");
            VerifyBytes(
                processHandle,
                nint.Add(moduleBase, profile.GetQueueControllerRva),
                profile.GetQueueControllerBytes,
                "GetQueueController");

            var resolverData = RunResolverStub(
                processHandle,
                moduleBase,
                profile);
            var stage = BitConverter.ToInt32(resolverData, 0);
            var controller = (nint)BitConverter.ToUInt32(resolverData, 4);
            var controllerVtable = (nint)BitConverter.ToUInt32(
                resolverData,
                8);
            if (stage != 3 || controller == 0 || controllerVtable == 0)
            {
                throw new InvalidOperationException(
                    $"酷狗队列控制器解析未就绪（stage={stage}, "
                    + $"controller=0x{controller:X}, "
                    + $"vtable=0x{controllerVtable:X}）");
            }

            if (controllerVtable != nint.Add(
                    moduleBase,
                    profile.QueueControllerVtableRva))
            {
                throw new InvalidOperationException(
                    $"QueueController vtable 不匹配（actual=0x{controllerVtable:X}）");
            }

            var tracker = ReadPointer(
                processHandle,
                nint.Add(controller, profile.AnchorTrackerOffset));
            if (tracker == 0)
            {
                throw new InvalidOperationException(
                    "QueueController anchor tracker 为空");
            }

            VerifyPointer(
                processHandle,
                tracker,
                nint.Add(moduleBase, profile.AnchorTrackerVtableRva),
                "AnchorTracker primary vtable");
            VerifyPointer(
                processHandle,
                nint.Add(tracker, 4),
                nint.Add(moduleBase, profile.AnchorTrackerSecondVtableRva),
                "AnchorTracker secondary vtable");
            VerifyPointer(
                processHandle,
                nint.Add(tracker, 8),
                nint.Add(moduleBase, profile.AnchorTrackerThirdVtableRva),
                "AnchorTracker tertiary vtable");

            var resetFunction = nint.Add(moduleBase, profile.ResetFunctionRva);
            VerifyBytes(
                processHandle,
                resetFunction,
                profile.ResetFunctionBytes,
                "AnchorTracker reset function");

            var beforeVector = ReadAndValidateVector(processHandle, tracker);
            var beforeState = KugouNativeController.ReadPlaybackState();
            var exitCode = RunResetStub(
                processHandle,
                tracker,
                resetFunction);
            if (exitCode != 1)
            {
                throw new InvalidOperationException(
                    $"锚点重置远程线程返回非成功码（{exitCode}）");
            }

            EnsureTargetProcess(target, expectedProcessId);
            var afterModule = FindKugouModule(target);
            var afterVersion = FileVersionInfo.GetVersionInfo(afterModule.FileName)
                .FileVersion
                ?? string.Empty;
            var afterHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(afterModule.FileName)));
            if (afterModule.BaseAddress != moduleBase
                || !afterVersion.Equals(playerVersion, StringComparison.Ordinal)
                || !afterHash.Equals(dllSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "酷狗进程或 DLL 在锚点重置期间发生变化");
            }

            var afterVector = ReadAndValidateVector(processHandle, tracker);
            if (afterVector.End != afterVector.Begin)
            {
                throw new InvalidOperationException(
                    $"锚点历史未清空（begin=0x{afterVector.Begin:X}, "
                    + $"end=0x{afterVector.End:X}）");
            }

            var afterState = KugouNativeController.ReadPlaybackState();
            if (!TrackStateUnchanged(beforeState, afterState))
            {
                throw new InvalidOperationException(
                    "锚点重置期间检测到酷狗当前歌曲变化");
            }

            return new KugouAnchorResetAttempt(
                true,
                true,
                $"已应用酷狗 {playerVersion} 无切歌锚点兼容画像",
                playerVersion,
                dllSha256);
        }
        catch (Exception exception)
        {
            return Failure(
                playerVersion,
                dllSha256,
                profileMatched,
                exception.Message);
        }
        finally
        {
            target?.Dispose();
        }
    }

    private static KugouAnchorResetAttempt Failure(
        string playerVersion,
        string dllSha256,
        bool profileMatched,
        string reason)
    {
        var message = profileMatched
            ? KugouAnchorResetProfilePolicy.BuildFailurePrompt(
                playerVersion,
                reason)
            : KugouAnchorResetProfilePolicy.BuildUpdatePrompt(playerVersion)
                + $" 原因：{reason}。";
        return new KugouAnchorResetAttempt(
            false,
            profileMatched,
            message,
            playerVersion,
            dllSha256);
    }

    private static void EnsureTargetProcess(
        Process process,
        int expectedProcessId)
    {
        process.Refresh();
        if (process.Id != expectedProcessId
            || process.HasExited
            || !process.ProcessName.Equals(
                "KuGou",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"酷狗目标进程 PID 校验失败（expected={expectedProcessId}）");
        }
    }

    private static ProcessModule FindKugouModule(Process process)
    {
        return process.Modules.Cast<ProcessModule>().Single(module =>
            module.ModuleName.Equals(
                "kugou.dll",
                StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] RunResolverStub(
        SafeProcessHandle processHandle,
        nint moduleBase,
        KugouAnchorResetProfile profile)
    {
        return RunRemoteStub(
            processHandle,
            remoteBlock => BuildResolverStub(
                nint.Add(remoteBlock, ResolverDataOffset),
                moduleBase,
                profile),
            ResolverDataOffset,
            32,
            allocationSize: 0x2000,
            codeProtectSize: 0x1000);
    }

    private static uint RunResetStub(
        SafeProcessHandle processHandle,
        nint tracker,
        nint resetFunction)
    {
        var stub = BuildResetStub(tracker, resetFunction);
        var data = RunRemoteStub(processHandle, stub, 0, 0, out var exitCode);
        _ = data;
        return exitCode;
    }

    private static byte[] RunRemoteStub(
        SafeProcessHandle processHandle,
        byte[] stub,
        int dataOffset,
        int dataSize)
    {
        return RunRemoteStub(
            processHandle,
            _ => stub,
            dataOffset,
            dataSize,
            out _);
    }

    private static byte[] RunRemoteStub(
        SafeProcessHandle processHandle,
        byte[] stub,
        int dataOffset,
        int dataSize,
        out uint exitCode)
    {
        return RunRemoteStub(
            processHandle,
            _ => stub,
            dataOffset,
            dataSize,
            out exitCode);
    }

    private static byte[] RunRemoteStub(
        SafeProcessHandle processHandle,
        Func<nint, byte[]> stubFactory,
        int dataOffset,
        int dataSize,
        nuint allocationSize = 0x1000,
        nuint codeProtectSize = 0x1000)
    {
        return RunRemoteStub(
            processHandle,
            stubFactory,
            dataOffset,
            dataSize,
            out _,
            allocationSize,
            codeProtectSize);
    }

    private static byte[] RunRemoteStub(
        SafeProcessHandle processHandle,
        Func<nint, byte[]> stubFactory,
        int dataOffset,
        int dataSize,
        out uint exitCode,
        nuint allocationSize = 0x1000,
        nuint codeProtectSize = 0x1000)
    {
        var remoteBlock = VirtualAllocEx(
            processHandle,
            0,
            allocationSize,
            MemCommit | MemReserve,
            PageReadWrite);
        if (remoteBlock == 0)
        {
            throw CreateWin32Exception("VirtualAllocEx");
        }

        var remoteThreadStarted = false;
        var remoteExecutionCompleted = false;
        Exception? failure = null;
        var result = Array.Empty<byte>();
        exitCode = 0;

        try
        {
            var stub = stubFactory(remoteBlock);
            var dataAddress = nint.Add(remoteBlock, dataOffset);
            WriteBytes(processHandle, remoteBlock, stub);
            if (dataSize > 0)
            {
                WriteBytes(processHandle, dataAddress, new byte[dataSize]);
            }

            if (!ReadBytes(processHandle, remoteBlock, stub.Length)
                    .SequenceEqual(stub))
            {
                throw new InvalidOperationException(
                    "酷狗远程解析 stub 回读校验失败");
            }

            if (!VirtualProtectEx(
                    processHandle,
                    remoteBlock,
                    codeProtectSize,
                    PageExecuteRead,
                    out _))
            {
                throw CreateWin32Exception("VirtualProtectEx");
            }
            if (!FlushInstructionCache(
                    processHandle,
                    remoteBlock,
                    (nuint)stub.Length))
            {
                throw CreateWin32Exception("FlushInstructionCache");
            }

            using var thread = CreateRemoteThread(
                processHandle,
                0,
                0,
                remoteBlock,
                0,
                0,
                out _);
            if (thread.IsInvalid)
            {
                throw CreateWin32Exception("CreateRemoteThread");
            }
            remoteThreadStarted = true;

            var waitResult = WaitForSingleObject(thread, 3000);
            if (waitResult == WaitTimeout)
            {
                throw new TimeoutException(
                    "酷狗远程解析线程三秒内未返回；为避免释放仍在执行的代码未回收远程页，已保留该页并需重启酷狗后再试。");
            }
            if (waitResult != WaitObject0)
            {
                throw new InvalidOperationException(
                    $"酷狗远程解析线程等待状态未知（{waitResult}）；为避免释放仍在执行的代码未回收远程页，已保留该页并未回收。");
            }
            remoteExecutionCompleted = true;
            if (!GetExitCodeThread(thread, out exitCode))
            {
                throw CreateWin32Exception("GetExitCodeThread");
            }

            result = dataSize > 0
                ? ReadBytes(processHandle, dataAddress, dataSize)
                : Array.Empty<byte>();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // A timed-out or unknown wait result may leave the remote thread
            // executing from this page. Keep that allocation in place to avoid
            // freeing code that KuGou could still be running.
            if (!remoteThreadStarted || remoteExecutionCompleted)
            {
                Exception? cleanupFailure = null;
                try
                {
                    if (!VirtualFreeEx(processHandle, remoteBlock, 0, MemRelease))
                    {
                        cleanupFailure = CreateWin32Exception("VirtualFreeEx");
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                if (cleanupFailure is not null)
                {
                    failure = failure is null
                        ? cleanupFailure
                        : new AggregateException(
                            $"酷狗远程页回收与主要操作同时失败；主错误：{failure.Message}；回收错误：{cleanupFailure.Message}",
                            failure,
                            cleanupFailure);
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    private static byte[] BuildResolverStub(
        nint dataAddress,
        nint moduleBase,
        KugouAnchorResetProfile profile)
    {
        var data = checked((uint)dataAddress.ToInt64());
        var getRoot = checked((uint)nint.Add(
            moduleBase,
            profile.GetServiceRootRva).ToInt64());
        var getController = checked((uint)nint.Add(
            moduleBase,
            profile.GetQueueControllerRva).ToInt64());
        var bytes = new List<byte>(96)
        {
            0x55, 0x8B, 0xEC, 0x53, 0x56, 0x57,
            0xBF
        };
        AddUInt32(bytes, data);
        bytes.AddRange([0xC7, 0x07]);
        AddUInt32(bytes, 1);
        bytes.Add(0xB8);
        AddUInt32(bytes, getRoot);
        bytes.AddRange([0xFF, 0xD0, 0x85, 0xC0, 0x74, 0x25]);
        bytes.AddRange([0xC7, 0x07]);
        AddUInt32(bytes, 2);
        bytes.AddRange([0x8B, 0xC8, 0xB8]);
        AddUInt32(bytes, getController);
        bytes.AddRange([0xFF, 0xD0, 0x89, 0x47, 0x04]);
        bytes.AddRange([0x85, 0xC0, 0x74, 0x12, 0x8B, 0x08]);
        bytes.AddRange([0x89, 0x4F, 0x08, 0xC7, 0x07]);
        AddUInt32(bytes, 3);
        bytes.AddRange([
            0x33, 0xC0, 0x5F, 0x5E, 0x5B,
            0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00
        ]);
        return bytes.ToArray();
    }

    private static byte[] BuildResetStub(
        nint tracker,
        nint resetFunction)
    {
        var code = new List<byte>(32)
        {
            0x55, 0x8B, 0xEC,
            0x53, 0x56, 0x57,
            0xB9
        };
        AddUInt32(code, checked((uint)tracker.ToInt64()));
        code.Add(0xB8);
        AddUInt32(code, checked((uint)resetFunction.ToInt64()));
        code.AddRange([
            0xFF, 0xD0,
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0x5F, 0x5E, 0x5B, 0x5D,
            0xC2, 0x04, 0x00
        ]);
        return code.ToArray();
    }

    private static void AddUInt32(ICollection<byte> bytes, uint value)
    {
        foreach (var item in BitConverter.GetBytes(value))
        {
            bytes.Add(item);
        }
    }

    private static AnchorVector ReadAndValidateVector(
        SafeProcessHandle processHandle,
        nint tracker)
    {
        var begin = ReadPointer(
            processHandle,
            nint.Add(tracker, AnchorHistoryBeginOffset));
        var end = ReadPointer(
            processHandle,
            nint.Add(tracker, AnchorHistoryEndOffset));
        var capacity = ReadPointer(
            processHandle,
            nint.Add(tracker, AnchorHistoryCapacityOffset));
        var allNull = begin == 0 && end == 0 && capacity == 0;
        if (!allNull
            && (begin == 0
                || begin > end
                || end > capacity
                || (begin.ToInt64() & 3) != 0
                || (end.ToInt64() & 3) != 0
                || (capacity.ToInt64() & 3) != 0))
        {
            throw new InvalidOperationException(
                $"酷狗 anchor-history vector 无效（begin=0x{begin:X}, "
                + $"end=0x{end:X}, capacity=0x{capacity:X}）");
        }

        var count = allNull
            ? 0
            : checked((end - begin).ToInt32() / 4);
        return new AnchorVector(begin, end, capacity, count);
    }

    private static bool TrackStateUnchanged(
        KugouPlaybackState before,
        KugouPlaybackState after)
    {
        return string.Equals(
                before.RawTitle,
                after.RawTitle,
                StringComparison.Ordinal)
            && before.SongItem == after.SongItem
            && before.SongList == after.SongList
            && before.SongTable == after.SongTable;
    }

    private static void VerifyPointer(
        SafeProcessHandle processHandle,
        nint address,
        nint expected,
        string label)
    {
        var actual = ReadPointer(processHandle, address);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"酷狗 {label} 不匹配（expected=0x{expected:X}, "
                + $"actual=0x{actual:X}）");
        }
    }

    private static void VerifyBytes(
        SafeProcessHandle processHandle,
        nint address,
        byte[] expected,
        string label)
    {
        var actual = ReadBytes(processHandle, address, expected.Length);
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"酷狗 {label} 原始字节不匹配（actual={Convert.ToHexString(actual)}）");
        }
    }

    private static nint ReadPointer(
        SafeProcessHandle processHandle,
        nint address)
    {
        return (nint)BitConverter.ToUInt32(
            ReadBytes(processHandle, address, 4));
    }

    private static byte[] ReadBytes(
        SafeProcessHandle processHandle,
        nint address,
        int count)
    {
        var bytes = new byte[count];
        if (!ReadProcessMemory(
                processHandle,
                address,
                bytes,
                (nuint)bytes.Length,
                out var read)
            || read != (nuint)bytes.Length)
        {
            throw CreateWin32Exception("ReadProcessMemory");
        }

        return bytes;
    }

    private static void WriteBytes(
        SafeProcessHandle processHandle,
        nint address,
        byte[] bytes)
    {
        if (!WriteProcessMemory(
                processHandle,
                address,
                bytes,
                (nuint)bytes.Length,
                out var written)
            || written != (nuint)bytes.Length)
        {
            throw CreateWin32Exception("WriteProcessMemory");
        }
    }

    private static Win32Exception CreateWin32Exception(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed");

    private sealed record AnchorVector(
        nint Begin,
        nint End,
        nint Capacity,
        int Count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        nint baseAddress,
        byte[] buffer,
        nuint size,
        out nuint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(
        SafeProcessHandle process,
        nint address,
        nuint size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        SafeProcessHandle process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(
        SafeProcessHandle process,
        nint address,
        nuint size,
        uint newProtect,
        out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(
        SafeProcessHandle process,
        nint baseAddress,
        nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateRemoteThread(
        SafeProcessHandle process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeWaitHandle handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(
        SafeWaitHandle thread,
        out uint exitCode);
}
