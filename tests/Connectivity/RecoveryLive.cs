using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

static class RecoveryLive
{
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr process);
    static async Task WaitFile(string path) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!File.Exists(path)) await Task.Delay(50, timeout.Token);
    }
    public static async Task Child(string folder) {
        foreach (string phase in new[] { "before", "blocked", "after" }) {
            if (phase != "before") await WaitFile(Path.Combine(folder, phase + ".go"));
            var results = await ProxyHealth.ProbeAsync(null, Console.WriteLine,
                timeout: TimeSpan.FromSeconds(phase == "blocked" ? 2 : 15));
            await File.WriteAllTextAsync(Path.Combine(folder, phase + ".json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, results }));
            if (results.Any(r => r.Ok) != (phase != "blocked")) throw new Exception("Unexpected child connectivity: " + phase);
        }
    }
    public static async Task Run(string folder, string bin, string input) {
        if (Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName == "RegionHook.dll"))
            throw new Exception("Run the fixture outside the existing environment");
        using var diagnostic = new DiagnosticLog(Path.Combine(folder, "logs"));
        void Log(string s) => Console.WriteLine(diagnostic.Write(s));
        using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        const string dns = "127.0.0.154";
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(dns), 53))) { }
        using (var tcp = new TcpListener(IPAddress.Parse(dns), 53)) { tcp.Start(); }
        var config = JsonNode.Parse(await File.ReadAllTextAsync(input))!.AsObject();
        int outsidePort = config["mixed-port"]!.GetValue<int>();
        config["mixed-port"] = port; config["dns"]!["listen"] = dns + ":53";
        string privateFolder = Path.Combine(folder, "private"); Directory.CreateDirectory(privateFolder);
        string configFile = Path.Combine(privateFolder, "fixture.json");
        await File.WriteAllTextAsync(configFile, config.ToJsonString());
        Process? core = null, harness = null; Task? output = null;
        int starts = 0; bool stalled = false;
        using var cancelMonitor = new CancellationTokenSource();
        var outsideResults = new List<bool>();
        async Task Monitor() {
            while (!cancelMonitor.IsCancellationRequested) {
                var results = await ProxyHealth.ProbeAsync(outsidePort, _ => {}, [ProxyHealth.Sites[0]], cancellationToken: cancelMonitor.Token);
                if (!cancelMonitor.IsCancellationRequested) outsideResults.Add(results[0].Ok);
                try { await Task.Delay(500, cancelMonitor.Token); } catch (OperationCanceledException) { }
            }
        }
        void Stop() {
            if (core == null) return;
            if (!core.HasExited) core.Kill(true);
            core.WaitForExit(); output?.GetAwaiter().GetResult(); core.Dispose(); core = null;
        }
        async Task Start(CancellationToken ct) {
            if (core is { HasExited: false }) return;
            var psi = new ProcessStartInfo(Path.Combine(bin, "proxy", "mihomo.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(privateFolder); psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(configFile);
            core = Process.Start(psi)!; starts++;
            // Discard core output because it may contain the private remote endpoint.
            output = Task.WhenAll(core.StandardOutput.ReadToEndAsync(ct), core.StandardError.ReadToEndAsync(ct));
            for (int i = 0; i < 100; i++) {
                if (core.HasExited) throw new IOException("Fixture core exited");
                using var tcp = new TcpClient();
                try { await tcp.ConnectAsync(IPAddress.Loopback, port, ct); return; }
                catch (SocketException) { await Task.Delay(50, ct); }
            }
            throw new IOException("Fixture listener did not start");
        }
        var recovery = new ProxyRecovery(Start, async ct => { Stop(); stalled = false; await Start(ct); },
            ct => ProxyHealth.ProbeAsync(port, Log, timeout: TimeSpan.FromSeconds(stalled ? 1 : 15), cancellationToken: ct), Log, retryDelay: TimeSpan.FromMilliseconds(100));
        Task monitor = Monitor();
        try {
            var before = await recovery.CheckAsync(default); int firstPid = core!.Id;
            var psi = new ProcessStartInfo(Path.Combine(bin, "..", "ConnectHarness.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(Path.Combine(bin, "RegionHook.dll")); psi.ArgumentList.Add(Environment.ProcessPath!);
            psi.ArgumentList.Add("--recovery-child \"" + folder + "\""); psi.ArgumentList.Add(port.ToString());
            psi.Environment["REGION_LAUNCHER_DNS_ADDRESS"] = dns;
            harness = Process.Start(psi)!;
            var childOutput = Task.WhenAll(harness.StandardOutput.ReadToEndAsync(), harness.StandardError.ReadToEndAsync());
            await WaitFile(Path.Combine(folder, "before.json"));
            if (NtSuspendProcess(core.Handle) != 0) throw new Exception("Cannot suspend owned fixture core");
            stalled = true; File.WriteAllText(Path.Combine(folder, "blocked.go"), "");
            await WaitFile(Path.Combine(folder, "blocked.json"));
            var after = await recovery.CheckAsync(default);
            if (starts != 2 || core!.Id == firstPid || harness.HasExited) throw new Exception("Recovery did not preserve the running application");
            int recoveredPid = core.Id;
            await recovery.CheckAsync(default);
            if (core.Id != recoveredPid || starts != 2) throw new Exception("Healthy core was restarted");
            File.WriteAllText(Path.Combine(folder, "after.go"), "");
            await harness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            await File.WriteAllTextAsync(Path.Combine(folder, "child.log"), string.Join("\n", await childOutput));
            if (harness.ExitCode != 0) throw new Exception("Child failed; inspect child.log");
            int? childPid = null;
            foreach (string phase in new[] { "before", "blocked", "after" }) {
                using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, phase + ".json")));
                int pid = report.RootElement.GetProperty("pid").GetInt32();
                if (childPid != null && childPid != pid) throw new Exception("Child was replaced"); childPid = pid;
                var results = report.RootElement.GetProperty("results").Deserialize<ProbeResult[]>()!;
                if (phase == "blocked") { if (results.Any(r => r.Ok)) throw new Exception("Child bypassed unavailable proxy"); }
                else ProxyHealth.RequireMatchingExit(phase == "before" ? before : after, results);
            }
            cancelMonitor.Cancel(); await monitor;
            if (outsideResults.Count < 2 || outsideResults.Any(ok => !ok)) throw new Exception("Outside HTTPS monitor failed");
            await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new { starts, firstPid, recoveredPid, childPid, outsideChecks = outsideResults.Count, outsideFailures = outsideResults.Count(ok => !ok), before, after }));
            Console.WriteLine("PASS: real isolated Mihomo stall/restart; same hooked application PID reconnects, blocked phase cannot bypass proxy, matching exits, healthy no-op, outside HTTPS remains available");
        } finally {
            cancelMonitor.Cancel(); await monitor;
            if (harness is { HasExited: false }) { harness.Kill(true); await harness.WaitForExitAsync(); }
            harness?.Dispose(); Stop(); File.Delete(configFile);
        }
    }
}
