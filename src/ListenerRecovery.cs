using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Native inspection stays in a bounded child. Only retained process handles authorize termination.
static class ListenerRecovery
{
    const string Argument = "--recover-listeners";
    sealed record Request(int LauncherPid, long LauncherCreated, long OldHandle, int OldPid, long OldCreated, int Port, string Dns, int DnsPort);
    public static async Task RecoverAsync(Process old, ProxyPorts ports, Action<string> log)
    {
        if (!old.HasExited) throw new IOException("旧代理尚未退出，不能回收监听引用。");
        using var current = Process.GetCurrentProcess();
        var request = new Request(Environment.ProcessId, current.StartTime.ToUniversalTime().ToFileTimeUtc(),
            old.SafeHandle.DangerousGetHandle().ToInt64(), old.Id, old.StartTime.ToUniversalTime().ToFileTimeUtc(),
            ports.Endpoints[0].Port, ports.Endpoints[2].Address.ToString(), ports.Endpoints[2].Port);
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add(Argument);
        using var helper = Process.Start(psi) ?? throw new IOException("无法启动监听引用回收助手。");
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync().ConfigureAwait(false) is string line) log(line); }
        var readers = Task.WhenAll(Drain(helper.StandardOutput), Drain(helper.StandardError));
        await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        helper.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) {
            if (!helper.HasExited) helper.Kill();
            await helper.WaitForExitAsync().ConfigureAwait(false);
            log("[端口回收] 检查超时，已结束本次检查助手；未确认的占用者保持原样。");
        }
        await readers.ConfigureAwait(false);
        log($"[端口回收] 助手退出码={helper.ExitCode}，接下来实际验证原端口能否重新绑定。");
    }

    public static bool TryRun(string[] args)
    {
        if (args.Length != 1 || args[0] != Argument) return false;
        Console.OutputEncoding = Encoding.UTF8;
        try {
            var request = JsonSerializer.Deserialize<Request>(Console.ReadLine() ?? "") ?? throw new IOException("请求为空。");
            Recover(request);
        } catch (Exception ex) {
            Console.WriteLine("[端口回收] 未完成：" + ex.Message); Environment.ExitCode = 2;
        }
        return true;
    }

    static void Log(string text) => Console.WriteLine("[端口回收] " + text);
    static void Recover(Request request)
    {
        if (!Environment.Is64BitProcess || request.Port is < 1 or > 65535 || request.DnsPort is < 1 or > 65535 ||
            !IPAddress.TryParse(request.Dns, out var dns) || dns.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(dns))
            throw new IOException("不支持的回收请求。");
        using var launcher = OpenProcess(0x1040 | 0x100000, false, request.LauncherPid);
        if (launcher.IsInvalid || Created(launcher) != request.LauncherCreated || WaitForSingleObject(launcher, 0) != 258)
            throw new IOException("启动器身份已变化。");
        using var old = Copy(launcher, (nint)request.OldHandle);
        if (old.IsInvalid || GetProcessId(old) != request.OldPid || Created(old) != request.OldCreated || WaitForSingleObject(old, 0) != 0)
            throw new IOException("无法确认旧代理的原始进程句柄与退出状态。");
        var ports = new ProxyPorts(request.Port, dns, request.DnsPort);
        var rows = ports.ListenerRows();
        var endpoints = rows.Where(r => r.Pid == request.OldPid && !rows.Any(other => other.Endpoint == r.Endpoint && other.Pid != request.OldPid))
            .Select(r => r.Endpoint).Distinct().ToArray();
        if (endpoints.Length == 0) { Log("没有旧代理 PID 对应的残留监听/UDP 绑定；不处理新占用者或普通连接。"); return; }
        Log($"原代理 PID={request.OldPid} 已退出；候选绑定：{string.Join("、", endpoints.Select(e => e.ToString()))}。仅结束持有这些监听对象引用的进程。");
        int inspected = 0, matched = 0, stopped = 0, denied = 0, blocked = 0;
        var watch = Stopwatch.StartNew();
        // Match the current provider's socket handle type/access. Other pipe handles may have blocking I/O.
        using var sample = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var handles = Handles();
        var own = handles.Single(h => h.Pid == (nuint)Environment.ProcessId && h.Value == (nuint)sample.SafeHandle.DangerousGetHandle());
        foreach (var group in handles.Where(h => h.Type == own.Type && h.Access == own.Access && h.Pid > 4 && h.Pid != (nuint)request.LauncherPid &&
                     h.Pid != (nuint)request.OldPid && h.Pid != (nuint)Environment.ProcessId && h.Pid <= int.MaxValue).GroupBy(h => (int)h.Pid)) {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) { Log("扫描达到时间上限，剩余引用留待再次检测。"); break; }
            using var process = OpenProcess(0x1000 | 0x100000 | 0x40 | 1, false, group.Key);
            if (process.IsInvalid) { denied++; continue; }
            if (WaitForSingleObject(process, 0) != 258 || !IsProcessCritical(process, out bool critical) || critical) continue;
            foreach (var entry in group) {
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) break;
                if (Environment.GetEnvironmentVariable("REGION_RECOVERY_TRACE") == "1") Log($"扫描 PID={group.Key} handle=0x{entry.Value:x} access=0x{entry.Access:x}");
                var copy = Copy(process, (nint)entry.Value);
                if (copy.IsInvalid) { copy.Dispose(); continue; }
                inspected++;
                // Synchronous sockets can block even read-only queries behind a pending receive. Isolate each query.
                var inspection = Inspect(copy, endpoints, ref blocked);
                if (inspection == null) {
                    if (blocked >= 64) { Log("阻塞查询达到上限；未确认的进程不会结束。"); return; }
                    continue;
                }
                using var reference = inspection.Reference;
                var endpoint = inspection.Endpoint;
                // Keep the object pinned, then re-read the handle slot. The process handle prevents PID reuse.
                {
                    using var verified = Copy(process, (nint)entry.Value);
                    if (verified.IsInvalid || !CompareObjectHandles(reference, verified) ||
                        !StillOld(ports, endpoint, request.OldPid)) continue;
                    matched++;
                    Log($"已确认持有者 PID={group.Key}，创建={DateTime.FromFileTimeUtc(Created(process)):O}，程序={Image(process)}，句柄=0x{entry.Value:x}，对象={endpoint}；与旧绑定匹配，准备结束该进程，未保存内容可能丢失。");
                    using var finalReference = Copy(process, (nint)entry.Value);
                    if (finalReference.IsInvalid || !CompareObjectHandles(reference, finalReference) || !StillOld(ports, endpoint, request.OldPid)) {
                        Log($"PID={group.Key} 在结束前引用已变化，本次跳过。"); continue;
                    }
                    bool terminated = TerminateProcess(process, 1);
                    if (!terminated) { Log($"PID={group.Key} 结束失败，错误={Marshal.GetLastWin32Error()}。"); break; }
                    if (WaitForSingleObject(process, 3000) == 0) { stopped++; Log($"持有者 PID={group.Key} 已退出；检查引用将在本轮结束时关闭。"); }
                    else Log($"PID={group.Key} 退出等待超时，不能视为已经释放。");
                }
                break;
            }
        }
        Log($"检查完成：候选句柄={inspected}，确证持有者={matched}，已退出={stopped}，权限不足进程={denied}，阻塞查询={blocked}；普通客户端、无关端口和身份变化者未结束。");
    }

    sealed record Inspection(SafeFileHandle Reference, ProxyPorts.Endpoint Endpoint);
    static Inspection? Inspect(SafeFileHandle copy, ProxyPorts.Endpoint[] endpoints, ref int blocked)
    {
        var result = new TaskCompletionSource<Inspection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            bool retained = false;
            try {
                if (GetFileType(copy) == 3 && ReadEndpoint(copy) is { } endpoint && endpoints.Contains(endpoint) && IsAfd(copy)) {
                    retained = true; result.SetResult(new(copy, endpoint));
                } else result.SetResult(null);
            } catch { result.TrySetResult(null); }
            finally { if (!retained) copy.Dispose(); }
        }) { IsBackground = true };
        thread.Start();
        if (result.Task.Wait(100)) return result.Task.Result;
        blocked++;
        _ = result.Task.ContinueWith(t => t.Result?.Reference.Dispose(), TaskScheduler.Default);
        return null;
    }

    static bool StillOld(ProxyPorts ports, ProxyPorts.Endpoint endpoint, int pid)
    {
        var rows = ports.ListenerRows().Where(r => r.Endpoint == endpoint).ToArray();
        return rows.Length > 0 && rows.All(r => r.Pid == pid);
    }
    static SafeFileHandle Copy(SafeHandle process, nint value)
    {
        if (!DuplicateHandle(process, value, GetCurrentProcess(), out var result, 0, false, 2)) return new(0, true);
        return result;
    }
    static bool IsAfd(SafeFileHandle handle)
    {
        nint buffer = Marshal.AllocHGlobal(4096);
        try {
            if (NtQueryObject(handle, 1, buffer, 4096, out _) != 0) return false;
            int length = (ushort)Marshal.ReadInt16(buffer);
            if (length <= 0 || length > 4000 || length % 2 != 0) return false;
            nint name = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            if (name < buffer || name + length > buffer + 4096) return false;
            string text = Marshal.PtrToStringUni(name, length / 2)!;
            return text.Equals(@"\Device\Afd", StringComparison.OrdinalIgnoreCase) || text.StartsWith(@"\Device\Afd\", StringComparison.OrdinalIgnoreCase);
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    static ProxyPorts.Endpoint? ReadEndpoint(SafeFileHandle handle)
    {
        // Read the AFD file object directly. Never register duplicated handles with Winsock or close foreign handles.
        byte[]? context = Query(handle, 0x12043);
        if (context == null || context.Length < 48 ||
            BitConverter.ToInt32(context, 4) != 2 || BitConverter.ToInt32(context, 16) != 16) return null;
        int type = BitConverter.ToInt32(context, 8), protocol = BitConverter.ToInt32(context, 12);
        if (type == 1 && protocol == 6) { if ((BitConverter.ToUInt16(context, 44) & 1) == 0) return null; }
        else if (type != 2 || protocol != 17) return null;
        byte[]? address = Query(handle, 0x1202f);
        if (address == null || address.Length != 16 || BitConverter.ToUInt16(address) != 2) return null;
        return new(new IPAddress(address.AsSpan(4, 4)), (address[2] << 8) | address[3], (ProtocolType)protocol);
    }
    static byte[]? Query(SafeFileHandle handle, uint code)
    {
        nint buffer = Marshal.AllocHGlobal(4096), status = Marshal.AllocHGlobal(16);
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
        try {
            Marshal.WriteInt64(status, 0); Marshal.WriteInt64(status, 8, 0);
            int result = NtDeviceIoControlFile(handle, done.SafeWaitHandle, 0, 0, status, code, 0, 0, buffer, 4096);
            if (result == 0x103) {
                if (!done.WaitOne(200)) {
                    // Exiting this isolated helper cancels pending native I/O before its buffers disappear.
                    Log("端点查询超时，终止检查助手。"); Environment.Exit(3);
                }
                result = Marshal.ReadInt32(status);
            }
            long length = Marshal.ReadInt64(status, 8);
            if (result != 0 || length < 0 || length > 4096) return null;
            byte[] data = new byte[length]; Marshal.Copy(buffer, data, 0, data.Length); return data;
        } finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(status); }
    }
    [StructLayout(LayoutKind.Sequential)] struct Entry {
        public nint Object; public nuint Pid, Value; public uint Access; public ushort Trace, Type; public uint Attributes, Reserved;
    }
    static Entry[] Handles()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 8 && size <= 64 * 1024 * 1024; attempt++) {
            nint buffer = Marshal.AllocHGlobal(size);
            try {
                int result = NtQuerySystemInformation(64, buffer, size, out int needed);
                if (result == unchecked((int)0xc0000004)) { size = Math.Max(size * 2, needed + 65536); continue; }
                if (result != 0) throw new IOException($"句柄表查询失败：0x{result:x8}");
                long count = Marshal.ReadInt64(buffer); int stride = Marshal.SizeOf<Entry>();
                if (count < 0 || count > (size - 16) / stride) throw new IOException("句柄表长度不正确。");
                Entry[] entries = new Entry[count];
                for (int i = 0; i < entries.Length; i++) entries[i] = Marshal.PtrToStructure<Entry>(buffer + 16 + i * stride);
                return entries;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("句柄表超过检查上限。");
    }
    static long Created(SafeHandle process) {
        if (!GetProcessTimes(process, out long created, out _, out _, out _)) throw new Win32Exception(); return created;
    }
    static string Image(SafeHandle process) {
        var text = new StringBuilder(32768); int length = text.Capacity;
        return QueryFullProcessImageName(process, 0, text, ref length) ? text.ToString() : "(路径不可读)";
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(SafeHandle process, nint value, nint target, out SafeFileHandle copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetProcessId(SafeHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeHandle handle, uint timeout);
    [DllImport("kernel32.dll")] static extern uint GetFileType(SafeHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessCritical(SafeHandle process, out bool critical);
    [DllImport("kernelbase.dll", SetLastError = true)] static extern bool CompareObjectHandles(SafeHandle first, SafeHandle second);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(SafeHandle process, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(SafeHandle handle, int type, nint buffer, int size, out int needed);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int type, nint buffer, int size, out int needed);
    [DllImport("ntdll.dll")] static extern int NtDeviceIoControlFile(SafeHandle handle, SafeWaitHandle done, nint apc, nint context, nint status, uint code, nint input, int inputSize, nint output, int outputSize);
}
