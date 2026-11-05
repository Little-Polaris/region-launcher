using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// This independent process keeps Clash's routing and DNS configuration untouched.
sealed class DedicatedProxy : IDisposable
{
    readonly string config = Path.Combine(AppContext.BaseDirectory, "data", "private", "proxy.json");
    Process? process;
    readonly SemaphoreSlim gate = new(1);
    public bool Configured => File.Exists(config);
    public event Action? UnexpectedExit;
    public int Port { get { using var json = JsonDocument.Parse(File.ReadAllText(config)); return json.RootElement.GetProperty("mixed-port").GetInt32(); } }
    public string DnsAddress {
        get {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (!json.RootElement.GetProperty("dns").TryGetProperty("listen", out var listen) || listen.GetString() != "127.0.0.153:53")
                throw new IOException("独立代理配置需要启用专用 DNS 127.0.0.153:53。");
            return "127.0.0.153";
        }
    }

    public async Task EnsureStarted()
    {
        await gate.WaitAsync();
        try
        {
            if (process is { HasExited: false }) return;
            var dnsIp = IPAddress.Parse(DnsAddress);
            using (var dnsUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                dnsUdp.Bind(new IPEndPoint(dnsIp, 53));
            var dnsTcp = new TcpListener(dnsIp, 53);
            try { dnsTcp.Start(); } finally { dnsTcp.Stop(); }
            // Refuse to reuse an unrelated service on the dedicated port.
            var probe = new TcpListener(IPAddress.Loopback, Port);
            try { probe.Start(); } finally { probe.Stop(); }
            string engine = Path.Combine(AppContext.BaseDirectory, "proxy", "mihomo.exe");
            var psi = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(Path.GetDirectoryName(config)!);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(config);
            process?.Dispose();
            var candidate = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process = candidate;
            candidate.Exited += (_, _) => { if (ReferenceEquals(process, candidate)) UnexpectedExit?.Invoke(); };
            candidate.Start();
            for (int i = 0; i < 50; i++)
            {
                if (candidate.HasExited) throw new IOException("独立代理内核启动失败，请检查节点配置。");
                using var socket = new TcpClient();
                try {
                    await socket.ConnectAsync(IPAddress.Loopback, Port);
                    using var dns = new TcpClient(); await dns.ConnectAsync(dnsIp, 53); return;
                }
                catch (SocketException) { await Task.Delay(100); }
            }
            Dispose();
            throw new IOException("独立代理内核未就绪，程序没有启动。");
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        var old = process; process = null;
        if (old == null) return;
        try { if (!old.HasExited) old.Kill(entireProcessTree: true); }
        finally { old.Dispose(); }
    }
}
