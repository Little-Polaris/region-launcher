using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

// This independent process keeps Clash's routing and DNS configuration untouched.
sealed class DedicatedProxy : IDisposable
{
    readonly string config;
    readonly string bin;
    readonly Action<string> log;
    readonly Action<IEnumerable<string>> registerSecrets;
    Task? output;
    string? runtimeConfig, fingerprint;
    public DedicatedProxy(Action<string>? log = null, Action<IEnumerable<string>>? registerSecrets = null, string? bin = null, string? configPath = null)
    {
        this.bin = bin ?? AppContext.BaseDirectory;
        config = configPath ?? Path.Combine(this.bin, "data", "private", "proxy.json");
        this.log = log ?? (_ => { }); this.registerSecrets = registerSecrets ?? (_ => { });
    }
    public string ConfigPath => config;
    public string EnginePath => Path.Combine(bin, "proxy", "mihomo.exe");
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

    public Task EnsureStarted(CancellationToken cancellationToken = default) => StartCore(false, cancellationToken);
    public Task RestartAsync(CancellationToken cancellationToken = default) => StartCore(true, cancellationToken);

    async Task StartCore(bool restart, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        bool starting = false;
        try
        {
            ProxyConfiguration.ValidateRuntimePolicy(config);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config)));
            // A recovery must keep the original node, listeners and DNS for the existing group.
            if (fingerprint != null && hash != fingerprint)
                throw new IOException("代理配置在运行期间发生变化，请恢复原配置，或退出环境后重新配置。");
            if (process is { HasExited: false }) {
                if (!restart) return;
                log($"[代理修复] 重启本启动器持有的内核 PID={process.Id}，运行时长={(DateTime.Now - process.StartTime).TotalSeconds:F0} 秒，内存={process.WorkingSet64 / 1048576} MiB，句柄={process.HandleCount}；保持节点、代理端口和 DNS 不变。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            StopCore();
            starting = true;
            var dnsIp = IPAddress.Parse(DnsAddress);
            using (var dnsUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                dnsUdp.Bind(new IPEndPoint(dnsIp, 53));
            var dnsTcp = new TcpListener(dnsIp, 53);
            try { dnsTcp.Start(); } finally { dnsTcp.Stop(); }
            // Refuse to reuse an unrelated service on the dedicated port.
            var probe = new TcpListener(IPAddress.Loopback, Port);
            try { probe.Start(); } finally { probe.Stop(); }
            var runtime = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            Register(runtime);
            runtime["log-level"] = "info";
            runtimeConfig = Path.Combine(Path.GetDirectoryName(config)!, $"runtime-{Environment.ProcessId}.json");
            await File.WriteAllTextAsync(runtimeConfig, runtime.ToJsonString(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(EnginePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(Path.GetDirectoryName(config)!);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(runtimeConfig);
            process?.Dispose();
            var candidate = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process = candidate;
            candidate.Exited += (_, _) => { if (ReferenceEquals(process, candidate)) UnexpectedExit?.Invoke(); };
            candidate.Start();
            fingerprint = hash;
            output = Task.WhenAll(ReadOutput(candidate.StandardOutput.BaseStream), ReadOutput(candidate.StandardError.BaseStream));
            log($"独立代理内核已启动，PID={candidate.Id}，SOCKS5=127.0.0.1:{Port}，DNS={DnsAddress}:53，规则=MATCH,RegionExit");
            for (int i = 0; i < 50; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.HasExited) throw new IOException("独立代理内核启动失败，请检查节点配置。");
                using var socket = new TcpClient();
                try {
                    await socket.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
                    using var dns = new TcpClient(); await dns.ConnectAsync(dnsIp, 53, cancellationToken); return;
                }
                catch (SocketException) { await Task.Delay(100, cancellationToken); }
            }
            throw new IOException("独立代理内核未就绪，程序没有启动。");
        }
        catch { if (starting) StopCore(); throw; }
        finally { gate.Release(); }
    }

    void Register(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var pair in obj) {
            if (pair.Value is JsonValue value && new[] { "password", "uuid", "token", "private-key", "secret", "authorization" }.Contains(pair.Key.ToLowerInvariant()) && value.TryGetValue<string>(out string? text) && text != null)
                registerSecrets([text]);
            else Register(pair.Value);
        }
        else if (node is JsonArray array) foreach (var item in array) Register(item);
    }
    async Task ReadOutput(Stream stream)
    {
        try { await ProcessLog.ReadAsync(stream, text => log("[代理内核] " + text)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { log("代理日志通道关闭：" + ex.Message); }
    }

    public void Dispose()
    {
        StopCore(); fingerprint = null;
    }
    void StopCore()
    {
        var old = process; process = null;
        if (old != null) {
            try { if (!old.HasExited) old.Kill(entireProcessTree: true); old.WaitForExit(3000); output?.Wait(3000); }
            catch (InvalidOperationException) { }
            finally { old.Dispose(); output = null; }
        }
        if (runtimeConfig != null) { try { File.Delete(runtimeConfig); } catch (IOException) { } runtimeConfig = null; }
    }
}
