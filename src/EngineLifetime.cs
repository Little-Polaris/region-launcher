using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Ties the proxy engine's lifetime to the launcher process, and finds engines left behind by a launcher that ended
/// without its normal exit (Task Manager, crash, sign-out, forced update), which would otherwise keep the proxy and
/// DNS ports bound.
/// </summary>
static class EngineLifetime
{
    static readonly object sync = new();
    static SafeFileHandle? job;

    /// <summary>
    /// Places the engine in a job that Windows closes when this process ends for any reason, ending the engine with it.
    /// Returns false (and leaves the engine running normally) when Windows refuses the assignment.
    /// </summary>
    public static bool Bind(Process engine, Action<string> log)
    {
        lock (sync) {
            if (job == null) {
                var created = CreateJobObjectW(IntPtr.Zero, null);
                if (created.IsInvalid) { log($"[代理生命周期] 无法创建进程组（{Marshal.GetLastWin32Error()}），代理仍按原方式在退出时结束。"); return false; }
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(created, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) {
                    log($"[代理生命周期] 无法设置进程组（{Marshal.GetLastWin32Error()}）。");
                    created.Dispose(); return false;
                }
                job = created;
            }
            if (AssignProcessToJobObject(job, engine.SafeHandle)) return true;
            log($"[代理生命周期] 代理未能加入随启动器结束的进程组（{Marshal.GetLastWin32Error()}），仍会在正常退出时结束。");
            return false;
        }
    }

    /// <summary>
    /// Engines started from this installation's own engine file whose launcher process no longer exists.
    /// A process whose parent PID is now used by a newer process also counts as orphaned.
    /// </summary>
    public static List<Process> Orphans(string enginePath, int? except)
    {
        var orphans = new List<Process>();
        var parents = ParentMap();
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(enginePath))) {
            bool orphan = false;
            try {
                if (process.Id != except && process.SessionId == session && !process.HasExited &&
                    process.MainModule?.FileName is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(enginePath), StringComparison.OrdinalIgnoreCase) &&
                    parents.TryGetValue(process.Id, out int parentId))
                    orphan = !ParentAlive(parentId, process.StartTime);
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
            if (orphan) orphans.Add(process); else process.Dispose();
        }
        return orphans;
    }

    static bool ParentAlive(int parentId, DateTime childStarted)
    {
        try {
            using var parent = Process.GetProcessById(parentId);
            // A reused PID belongs to a process that started after the engine, so it is not the engine's launcher.
            return !parent.HasExited && parent.StartTime <= childStarted;
        } catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; } // unknown: leave it alone
    }

    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return map;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        return map;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int PriorityClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
