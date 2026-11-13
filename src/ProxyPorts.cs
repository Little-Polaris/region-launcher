using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// A successful exclusive bind is the release check; table/PID snapshots are diagnostics only.
sealed class ProxyPorts(int proxyPort, IPAddress dnsAddress, int dnsPort = 53)
{
    internal readonly record struct Endpoint(IPAddress Address, int Port, ProtocolType Protocol)
    {
        public override string ToString() => $"{Protocol.ToString().ToUpperInvariant()} {Address}:{Port}";
    }
    readonly Endpoint[] endpoints = [new(IPAddress.Loopback, proxyPort, ProtocolType.Tcp),
        new(IPAddress.Loopback, proxyPort, ProtocolType.Udp), new(dnsAddress, dnsPort, ProtocolType.Tcp), new(dnsAddress, dnsPort, ProtocolType.Udp)];
    internal Endpoint[] Endpoints => endpoints;
    internal record Row(Endpoint Endpoint, int Pid, int State);
    internal Row[] ListenerRows()
    {
        List<Row> rows = [];
        foreach (bool tcp in new[] { true, false }) {
            nint buffer = 0;
            try {
                int size = 0;
                uint Read(nint data, ref int bytes) => tcp ? GetExtendedTcpTable(data, ref bytes, false, 2, 5, 0) : GetExtendedUdpTable(data, ref bytes, false, 2, 1, 0);
                uint result = Read(0, ref size);
                if (result != 0 && result != 122) throw new IOException($"端口表错误={result}");
                for (int attempt = 0; attempt < 5; attempt++) {
                    if (buffer != 0) { Marshal.FreeHGlobal(buffer); buffer = 0; }
                    buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
                    result = Read(buffer, ref size);
                    if (result != 122) break;
                }
                if (result != 0) throw new IOException($"端口表错误={result}");
                int count = Marshal.ReadInt32(buffer), stride = tcp ? 24 : 12;
                if (count < 0 || count > (size - 4) / stride) throw new IOException("端口表长度不正确。");
                for (int i = 0; i < count; i++) {
                    nint row = buffer + 4 + i * stride;
                    int state = tcp ? Marshal.ReadInt32(row) : 0;
                    if (tcp && state != 2) continue;
                    var address = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, tcp ? 4 : 0)));
                    int offset = tcp ? 8 : 4;
                    var endpoint = new Endpoint(address, Marshal.ReadByte(row, offset) * 256 + Marshal.ReadByte(row, offset + 1), tcp ? ProtocolType.Tcp : ProtocolType.Udp);
                    if (endpoints.Any(e => e.Protocol == endpoint.Protocol && e.Port == endpoint.Port && (e.Address.Equals(address) || address.Equals(IPAddress.Any))))
                        rows.Add(new(endpoint, Marshal.ReadInt32(row, tcp ? 20 : 8), state));
                }
            } finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); }
        }
        return rows.ToArray();
    }

    internal (Endpoint Endpoint, SocketException Error)? Check()
    {
        // Hold all reservations together so TCP and UDP checks describe one point in time.
        List<Socket> reservations = [];
        try {
            foreach (var endpoint in endpoints) {
                var socket = new Socket(AddressFamily.InterNetwork, endpoint.Protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, endpoint.Protocol);
                reservations.Add(socket);
                try { socket.ExclusiveAddressUse = true; socket.Bind(new IPEndPoint(endpoint.Address, endpoint.Port)); }
                catch (SocketException ex) { return (endpoint, ex); }
            }
            return null;
        } finally { foreach (var socket in reservations) socket.Dispose(); }
    }

    public void RequireAvailable(Action<string> log)
    {
        if (Check() is not { } failure) return;
        log($"[端口检查] 无法绑定 {failure.Endpoint}；错误码={failure.Error.NativeErrorCode}：{failure.Error.Message}");
        Snapshot(log);
        throw new IOException($"{failure.Endpoint} 尚未释放或被其他程序占用（{failure.Error.NativeErrorCode}）。未复用或结束占用进程，请查看日志。", failure.Error);
    }

    public bool WaitForRelease(Action<string> log, int milliseconds = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (true) {
            if (Check() is not { } failure) {
                log($"[端口释放] 已确认可重新绑定：{string.Join("、", endpoints.Select(e => e.ToString()))}；检查耗时={watch.ElapsedMilliseconds}ms。");
                return true;
            }
            if (watch.ElapsedMilliseconds >= milliseconds) {
                log($"[端口释放] 未完成：{failure.Endpoint}，错误码={failure.Error.NativeErrorCode}；等待={watch.ElapsedMilliseconds}ms。未强制接管端口。");
                Snapshot(log); return false;
            }
            Thread.Sleep(100);
        }
    }

    public void Snapshot(Action<string> log)
    {
        foreach (bool tcp in new[] { true, false }) {
            nint buffer = 0;
            try {
                int size = 0;
                uint Read(nint data, ref int bytes) => tcp ? GetExtendedTcpTable(data, ref bytes, false, 2, 5, 0) : GetExtendedUdpTable(data, ref bytes, false, 2, 1, 0);
                uint result = Read(0, ref size);
                if (result != 122 && result != 0) throw new IOException($"端口表错误={result}");
                // The table can grow between calls; retry with the new requested size.
                for (int attempt = 0; attempt < 3; attempt++) {
                    if (buffer != 0) { Marshal.FreeHGlobal(buffer); buffer = 0; }
                    buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
                    result = Read(buffer, ref size);
                    if (result != 122) break;
                }
                if (result != 0) throw new IOException($"端口表错误={result}");
                int count = Marshal.ReadInt32(buffer), matches = 0;
                for (int i = 0; i < count; i++) {
                    nint row = buffer + 4 + i * (tcp ? 24 : 12);
                    var address = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, tcp ? 4 : 0)));
                    int offset = tcp ? 8 : 4;
                    int port = Marshal.ReadByte(row, offset) * 256 + Marshal.ReadByte(row, offset + 1);
                    if (!endpoints.Any(e => e.Protocol == (tcp ? ProtocolType.Tcp : ProtocolType.Udp) && e.Port == port && (e.Address.Equals(address) || address.Equals(IPAddress.Any)))) continue;
                    if (++matches > 12) continue;
                    int pid = Marshal.ReadInt32(row, tcp ? 20 : 8);
                    string state = tcp ? ((TcpState)Marshal.ReadInt32(row)).ToString() : "Bound";
                    string owner;
                    try {
                        if (pid == 0) owner = "系统未提供所属进程";
                        else { using var process = Process.GetProcessById(pid); owner = process.HasExited ? "进程已退出" : $"{process.ProcessName}，创建={process.StartTime:O}"; }
                    }
                    catch (ArgumentException) { owner = "对应进程已不存在"; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { owner = "进程信息不可读：" + ex.Message; }
                    log($"[端口快照] {(tcp ? "TCP" : "UDP")} {address}:{port}，状态={state}，PID={pid}，{owner}");
                }
                if (matches == 0) log($"[端口快照] {(tcp ? "TCP" : "UDP")} IPv4 表未匹配到目标；不据此判断端口已释放。");
                if (matches > 12) log($"[端口快照] 共 {matches} 条匹配，以上显示前 12 条。");
            } catch (Exception ex) { log("[端口快照] 读取失败：" + ex.Message); }
            finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); }
        }
    }
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
