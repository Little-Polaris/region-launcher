using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

if (ListenerRecovery.TryRun(args)) return;
if (args.Length == 3 && args[0] == "--listener-tests") { await ListenerRecoveryTests.Run(args[1], args[2]); return; }
if (args.Length == 1 && args[0] == "--hold") { await Task.Delay(60000); return; }
if (args.Length == 2 && args[0] == "--recovery-child") { await RecoveryLive.Child(args[1]); return; }

string folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
if (args.Length == 3 && args[1] == "--switch")
{
    await RuntimeExitTests.Run(Path.GetFullPath(args[2]), folder);
    return;
}
if (args.Length >= 4 && args[1] == "--recovery-live") {
    await RecoveryLive.Run(folder, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); return;
}
if (args.Length >= 4 && args[1] == "--live") {
    string bin = Path.GetFullPath(args[2]);
    using var logs = new DiagnosticLog(Path.Combine(folder, "logs"));
    void Log(string s) => Console.WriteLine(logs.Write(s));
    using var proxy = new DedicatedProxy(Log, logs.AddSecrets, bin, Path.GetFullPath(args[3]));
    await proxy.EnsureStarted();
    var first = await ProxyHealth.ProbeAsync(proxy.Port, Log);
    ProxyHealth.RequireInternet(first);
    var second = await EnvironmentCheck.RunAsync(bin, folder, proxy.Port, proxy.DnsAddress, null, Log, logs);
    ProxyHealth.RequireMatchingExit(first, second);
    await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new { proxy = first, environment = second }));
    string job = "Local\\RegionLauncher." + Guid.NewGuid().ToString("N"), ownerConfig = Path.Combine(folder, "join-owner.ini");
    await File.WriteAllTextAsync(ownerConfig, $"[session]\nJob={job}\nOwnerPid={Environment.ProcessId}\nApplication={Environment.ProcessPath}\nArguments=--hold\nProxyHost=127.0.0.1\nProxyPort={proxy.Port}\nDnsAddress={proxy.DnsAddress}\n", Encoding.Unicode);
    var psi = new ProcessStartInfo(Path.Combine(bin, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("--run"); psi.ArgumentList.Add(ownerConfig);
    using var owner = Process.Start(psi)!;
    try {
        string? start = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
        if (start?.StartsWith("STARTED PID=") != true) throw new Exception("Owner environment did not start: " + start);
        using var target = Process.GetProcessById(int.Parse(start.Split('=')[1]));
        var joined = await EnvironmentCheck.RunAsync(bin, folder, proxy.Port, proxy.DnsAddress, job, Log, logs);
        ProxyHealth.RequireMatchingExit(first, joined);
        if (owner.HasExited || target.HasExited) throw new Exception("Check terminated existing environment");
        using var stop = EventWaitHandle.OpenExisting(job + ".stop"); stop.Set();
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS: checking inside an existing environment preserves its running application");
    } finally { if (!owner.HasExited) { owner.Kill(true); await owner.WaitForExitAsync(); } }
    Console.WriteLine("PASS: real proxy HTTPS and unconfigured managed-process HTTPS have matching exits");
    return;
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
using var direct = new TcpListener(IPAddress.Loopback, 0); direct.Start();
string url = $"http://127.0.0.1:{((IPEndPoint)direct.LocalEndpoint).Port}/trace";
foreach (string mode in new[] { "reject", "invalid", "success", "timeout", "stall", "body-stall", "oversized" }) {
    using var socks = new FakeSocks(mode);
    var results = await ProxyHealth.ProbeAsync(socks.Port, _ => { }, [new("Fixture", url, true)], TimeSpan.FromMilliseconds(mode is "timeout" or "stall" or "body-stall" ? 500 : 3000));
    Assert(results[0].Ok == (mode == "success"), "Handshake-only or invalid content incorrectly counted as usable internet");
    if (mode == "success") Assert(results[0].Ip == "203.0.113.7", "Exit parse failed");
    if (mode is "timeout" or "stall") Assert(results[0].Stage == "连接/握手/响应头" && results[0].Error == "请求超时", "Pre-header timeout stage missing");
    if (mode == "body-stall") Assert(results[0].Stage == "读取响应正文" && results[0].Status == 200 && results[0].Error == "请求超时", "Body timeout lost its deadline or response status");
    if (mode == "oversized") Assert(results[0].Error!.Contains("64 KiB"), "Oversized response accepted");
    Assert(!direct.Pending(), "Request fell back to direct connection");
    Assert(socks.Handshake, "SOCKS handshake not attempted");
}
var good = new ProbeResult("fixture", "fixture.test", true, 10, 200, "203.0.113.7", "US", null);
await RecoveryTests.Run(good);
using (var socks = new FakeSocks("timeout")) {
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    var elapsed = Stopwatch.StartNew();
    var canceled = await ProxyHealth.ProbeAsync(socks.Port, _ => {}, [new("Fixture", url, true)], cancellationToken: cancel.Token);
    Assert(!canceled[0].Ok && canceled[0].Error == "检测已取消" && elapsed.Elapsed < TimeSpan.FromSeconds(3), "Exit cancellation did not stop connectivity probe promptly");
    Assert(!direct.Pending(), "Canceled request fell back to direct");
}
ProxyHealth.RequireMatchingExit([good], [good]);
foreach (var bad in new[] { good with { Ip = "203.0.113.8" }, good with { Host = "other.test" }, good with { Ok = false } }) {
    try { ProxyHealth.RequireMatchingExit([good], [bad]); throw new Exception("Unverified route accepted"); } catch (IOException) { }
}
string configPath = Path.Combine(folder, "fixture-proxy.json");
var config = ProxyConfiguration.Build(new JsonObject { ["type"] = "hysteria2", ["server"] = "192.0.2.1", ["port"] = 443, ["password"] = "fixture-password" }, 17890);
await File.WriteAllTextAsync(configPath, config.ToJsonString()); ProxyConfiguration.ValidateRuntimePolicy(configPath);
await ProxyOwnershipTests.Run(configPath);
await ProxyShutdownTests.Run(folder);
config["rules"] = new JsonArray("MATCH,DIRECT");
await File.WriteAllTextAsync(configPath, config.ToJsonString());
try { ProxyConfiguration.ValidateRuntimePolicy(configPath); throw new Exception("Direct routing accepted"); } catch (IOException) { }
string logDirectory = Path.Combine(folder, "log-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(logDirectory);
for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(logDirectory, $"launcher-old-{i}.log"), "old");
using (var logs = new DiagnosticLog(logDirectory)) {
    logs.AddSecrets(["long-fixture-password"]);
    logs.Write("中文日志🚀 long-fixture-password token=abcdef https://user:password@example.com/path?subscription=abcdef");
    logs.Write("Authorization: Bearer hidden-bearer-value"); logs.Write("{\"password\":\"hidden-json-value\"}");
}
string text = string.Join("", Directory.GetFiles(logDirectory, "launcher-*.log").Select(File.ReadAllText));
Assert(text.Contains("中文日志🚀") && !text.Contains("abcdef") && !text.Contains("long-fixture-password") && !text.Contains("user:password"), "Log redaction or Unicode failed");
Assert(!text.Contains("hidden-bearer-value") && !text.Contains("hidden-json-value"), "Structured credentials not redacted");
Assert(Directory.GetFiles(logDirectory, "launcher-*.log").Length <= 10, "Log retention failed");
await SharedLogTests.Run(folder);
Console.WriteLine("PASS: handshake-only failure, content validation, timeout, no direct fallback, exit comparison, pinned routing, persistent UTF-8/redacted logs and retention");

sealed class FakeSocks : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource cancel = new();
    readonly Task worker;
    public bool Handshake;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FakeSocks(string mode) { listener.Start(); worker = Serve(mode); }
    async Task Serve(string mode)
    {
        try {
            using var client = await listener.AcceptTcpClientAsync(cancel.Token); var stream = client.GetStream();
            byte[] header = new byte[2]; await stream.ReadExactlyAsync(header, cancel.Token);
            await stream.ReadExactlyAsync(new byte[header[1]], cancel.Token); Handshake = true;
            await stream.WriteAsync(new byte[] { 5, 0 }, cancel.Token);
            byte[] request = new byte[4]; await stream.ReadExactlyAsync(request, cancel.Token);
            int size = request[3] == 1 ? 4 : request[3] == 4 ? 16 : await ReadByte(stream);
            await stream.ReadExactlyAsync(new byte[size + 2], cancel.Token);
            if (mode == "timeout") { await Task.Delay(10000, cancel.Token); return; }
            await stream.WriteAsync(new byte[] { 5, (byte)(mode == "reject" ? 5 : 0), 0, 1, 0, 0, 0, 0, 0, 0 }, cancel.Token);
            if (mode == "reject") return;
            if (mode == "stall") { await Task.Delay(10000, cancel.Token); return; }
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            while (await reader.ReadLineAsync(cancel.Token) is string line && line.Length > 0) { }
            string body = mode == "success" ? "ip=203.0.113.7\nloc=US\n" : "<html>not an IP response</html>";
            if (mode == "body-stall") {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nx"), cancel.Token);
                await Task.Delay(10000, cancel.Token); return;
            }
            if (mode == "oversized") body = new string('x', 65537);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), cancel.Token);
        } catch (OperationCanceledException) { } catch (IOException) { }
    }
    async Task<int> ReadByte(Stream stream) { byte[] b = new byte[1]; await stream.ReadExactlyAsync(b, cancel.Token); return b[0]; }
    public void Dispose() { cancel.Cancel(); listener.Stop(); worker.GetAwaiter().GetResult(); cancel.Dispose(); }
}
