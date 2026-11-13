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
    ProxyPorts? ports;
    volatile bool stopping;
    public bool CleanupComplete => process == null && ports == null;
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (!await Task.Run(() => StopCore(true), cancellationToken).ConfigureAwait(false))
                throw new IOException("旧代理或端口尚未释放，已取消启动替代内核。请查看端口回收日志。");
            starting = true;
            var dnsIp = IPAddress.Parse(DnsAddress);
            var requestedPorts = new ProxyPorts(Port, dnsIp);
            requestedPorts.RequireAvailable(log);
            var runtime = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            Register(runtime);
            runtime["log-level"] = "info";
            runtimeConfig = Path.Combine(Path.GetDirectoryName(config)!, $"runtime-{Environment.ProcessId}.json");
            await File.WriteAllTextAsync(runtimeConfig, runtime.ToJsonString(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(EnginePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(Path.GetDirectoryName(config)!);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(runtimeConfig);
            process?.Dispose();
            var candidate = new Process { StartInfo = psi, EnableRaisingEvents = true };
            candidate.Exited += (_, _) => { if (!stopping && ReferenceEquals(process, candidate)) UnexpectedExit?.Invoke(); };
            try { candidate.Start(); } catch { candidate.Dispose(); throw; }
            process = candidate;
            // Retain the process handle returned by Start, never reacquire ownership via a port-table PID.
            _ = candidate.SafeHandle;
            ports = requestedPorts;
            fingerprint = hash;
            output = Task.WhenAll(ReadOutput(candidate.StandardOutput.BaseStream), ReadOutput(candidate.StandardError.BaseStream));
            log($"独立代理内核已启动，PID={candidate.Id}，SOCKS5=127.0.0.1:{Port}，DNS={DnsAddress}:53，规则=MATCH,RegionExit");
            for (int i = 0; i < 50; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.HasExited) throw new IOException("独立代理内核启动失败，请检查节点配置。");
                using var socket = new TcpClient();
                try {
                    await socket.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);
                    using var dns = new TcpClient(); await dns.ConnectAsync(dnsIp, 53, cancellationToken).ConfigureAwait(false); return;
                }
                catch (SocketException) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
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
        gate.Wait();
        try { if (StopCore()) fingerprint = null; }
        finally { gate.Release(); }
    }
    public async Task<bool> StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => StopCore()).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    bool StopCore(bool recoverListeners = false)
    {
        var old = process;
        if (old != null) {
            stopping = true;
            var watch = Stopwatch.StartNew();
            try {
                log($"[代理退出] 开始检查自有内核 PID={old.Id}，创建={old.StartTime:O}。");
                // Mihomo runs as one process. Never walk descendants or kill a PID found by port lookup.
                if (!old.HasExited) old.Kill();
                if (!old.WaitForExit(10000)) {
                    log($"[代理退出] PID={old.Id} 终止等待超时，未确认退出；保留句柄，不启动替代内核。");
                    ports?.Snapshot(log); return false;
                }
                log($"[代理退出] PID={old.Id} 已确认退出，退出码={old.ExitCode}，耗时={watch.ElapsedMilliseconds}ms。");
                if (output != null && !output.Wait(3000)) log("[代理退出] 日志管道尚未结束，关闭本次内核的读取端。");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) {
                log("[代理退出] 清理异常：" + ex.Message);
                if (!old.HasExited) { ports?.Snapshot(log); return false; }
            }
            finally { stopping = false; }
            output = null;
        }
        bool released = ports?.WaitForRelease(log) ?? true;
        if (!released && recoverListeners && old != null && old.HasExited) {
            log("[端口回收] 原代理已退出但原端口仍占用；开始核验旧监听引用，确证持有者将被结束，可能丢失未保存内容。");
            try { ListenerRecovery.RecoverAsync(old, ports!, log).GetAwaiter().GetResult(); }
            catch (Exception ex) { log("[端口回收] 检查失败：" + ex.Message); }
            released = ports!.WaitForRelease(log);
        }
        // Keep the original process identity until the old endpoints have gone; a port-table PID is not ownership.
        if (released) { old?.Dispose(); process = null; ports = null; }
        if (runtimeConfig != null) {
            try { File.Delete(runtimeConfig); runtimeConfig = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("[代理退出] 临时配置清理失败：" + ex.Message); }
        }
        return released;
    }
}
