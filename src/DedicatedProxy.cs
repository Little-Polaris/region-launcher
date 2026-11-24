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
    ProxyRuntimeExit? runtimeExit;
    JsonObject? selectedNode;
    volatile bool stopping;
    public bool CleanupComplete => process == null && ports == null;
    public bool HasExitedCore => process is { HasExited: true };
    readonly SemaphoreSlim gate = new(1);
    public bool Configured => File.Exists(config);
    public bool UsesFailover => selectedNode == null && ProxyConfiguration.IsFailover(ProxyConfiguration.ReadNode(File.ReadAllText(config), out _));
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

    public async Task ReconnectAsync(CancellationToken token)
    {
        await EnsureStarted(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var exit = runtimeExit ?? throw new IOException("独立代理内核未就绪。");
            if (exit.HasAutomatic && selectedNode == null) return;
            exit.WriteNode(selectedNode);
            await exit.RefreshNodeAsync(token).ConfigureAwait(false);
            await exit.CloseConnectionsAsync(token).ConfigureAwait(false);
            log("[代理修复] 已在现有内核中重连当前节点。");
        }
        finally { gate.Release(); }
    }

    public async Task SwitchNodeAsync(JsonObject? node, CancellationToken token)
    {
        if (node != null && ProxyConfiguration.IsFailover(node))
            throw new IOException("请选择一个固定节点，或恢复已配置的自动组。");
        JsonObject? selection = node == null ? null :
            (JsonObject)ProxyConfiguration.Build(node, Port)["proxies"]![0]!.DeepClone();
        await EnsureStarted(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var exit = runtimeExit ?? throw new IOException("独立代理内核未就绪。");
            if (selection == null && !exit.HasAutomatic) throw new IOException("当前配置没有自动故障切换组。");
            if (selection == null && selectedNode == null) return;
            try
            {
                if (selection != null)
                {
                    Register(selection);
                    exit.WriteNode(selection);
                    await exit.RefreshNodeAsync(token).ConfigureAwait(false);
                }
                await exit.SelectAsync(selection == null, token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                try
                {
                    if (selection != null)
                    {
                        exit.WriteNode(selectedNode);
                        await exit.RefreshNodeAsync(token).ConfigureAwait(false);
                    }
                    await exit.SelectAsync(exit.HasAutomatic && selectedNode == null, token).ConfigureAwait(false);
                }
                catch (Exception) { throw new IOException("切换失败，未能确认代理出口是否恢复，请重新选择节点。"); }
                throw;
            }
            selectedNode = selection;
            try { await exit.CloseConnectionsAsync(token).ConfigureAwait(false); }
            catch (IOException ex) { log("[切换节点] 出口已切换，旧连接清理失败：" + ex.Message); }
        }
        finally { gate.Release(); }
    }

    public async Task EnsureStarted(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool starting = false;
        try
        {
            ProxyConfiguration.ValidateRuntimePolicy(config);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config)));
            // A recovery must keep the same exit configuration for the existing session.
            if (fingerprint != null && hash != fingerprint)
                throw new IOException("代理配置在运行期间发生变化，请恢复原配置，或退出环境后重新配置。");
            if (process is { HasExited: false }) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await Task.Run(() => StopCore(true), cancellationToken).ConfigureAwait(false))
                throw new IOException("旧代理或端口尚未释放，已取消启动替代内核。请查看端口回收日志。");
            starting = true;
            var dnsIp = IPAddress.Parse(DnsAddress);
            var requestedPorts = new ProxyPorts(Port, dnsIp);
            if (ReclaimOrphanedEngines()) requestedPorts.WaitForRelease(log);
            requestedPorts.RequireAvailable(log);
            var runtime = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
            ProxyConfiguration.PrepareRuntime(runtime);
            runtimeExit = new ProxyRuntimeExit(runtime, Path.GetDirectoryName(config)!);
            runtimeExit.WriteNode(selectedNode);
            Register(runtime);
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
            // If the launcher ends without its normal exit, Windows ends the engine too instead of leaving the ports bound.
            EngineLifetime.Bind(candidate, log);
            process = candidate;
            // Retain the process handle returned by Start, never reacquire ownership via a port-table PID.
            _ = candidate.SafeHandle;
            ports = requestedPorts;
            fingerprint = hash;
            output = Task.WhenAll(ReadOutput(candidate.StandardOutput.BaseStream), ReadOutput(candidate.StandardError.BaseStream));
            log($"独立代理内核已启动，PID={candidate.Id}，SOCKS5=127.0.0.1:{Port}，DNS={DnsAddress}:53，规则=MATCH,RegionExit");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(6));
            var readyToken = readiness.Token;
            try
            {
                while (true)
                {
                    readyToken.ThrowIfCancellationRequested();
                    if (candidate.HasExited) throw new IOException("独立代理内核启动失败，请检查节点配置。");
                    using var socket = new TcpClient();
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, Port, readyToken).ConfigureAwait(false);
                        using var dns = new TcpClient(); await dns.ConnectAsync(dnsIp, 53, readyToken).ConfigureAwait(false);
                        await runtimeExit.SelectAsync(runtimeExit.HasAutomatic && selectedNode == null, readyToken).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException)
                    { await Task.Delay(100, readyToken).ConfigureAwait(false); }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("独立代理内核或控制接口未就绪，程序没有启动。"); }
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
        try { if (StopCore()) { fingerprint = null; selectedNode = null; } }
        finally { gate.Release(); }
    }
    /// <summary>
    /// Ends engines started from this installation's engine file by a launcher that no longer runs (it ended without
    /// its normal exit). Returns true when one was ended.
    /// </summary>
    bool ReclaimOrphanedEngines()
    {
        bool ended = false;
        foreach (var orphan in EngineLifetime.Orphans(EnginePath, process?.Id)) {
            using (orphan) {
                try {
                    log($"[代理回收] 发现上次未正常退出留下的代理内核 PID={orphan.Id}，创建={orphan.StartTime:O}；其启动器已不存在，正在结束以释放端口。");
                    orphan.Kill();
                    ended |= orphan.WaitForExit(10000);
                    log(orphan.HasExited ? $"[代理回收] PID={orphan.Id} 已结束。" : $"[代理回收] PID={orphan.Id} 结束等待超时。");
                } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
                    log($"[代理回收] PID={orphan.Id} 无法结束：{ex.Message}");
                }
            }
        }
        return ended;
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
        if (runtimeExit != null)
        {
            try { runtimeExit.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("[代理退出] 临时节点配置清理失败：" + ex.Message); }
            runtimeExit = null;
        }
        if (runtimeConfig != null) {
            try { File.Delete(runtimeConfig); runtimeConfig = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("[代理退出] 临时配置清理失败：" + ex.Message); }
        }
        return released;
    }
}
