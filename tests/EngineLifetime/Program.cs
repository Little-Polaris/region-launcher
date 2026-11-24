// Proxy engine lifetime: an engine bound to its launcher ends when the launcher is killed, and an engine left behind
// by a launcher that no longer exists is reported as an orphan, while engines with a live launcher or from another
// installation are not. Stand-in engines are copies of waitfor.exe named mihomo.exe inside temporary folders, listening
// on nothing, so the real proxy port and any running launcher are never touched. With REGION_TEST_ENGINE set to a
// Mihomo executable, a real engine on free loopback ports also checks that the port is actually released.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

if (args.Length == 3 && args[0] == "--launcher") {
    // Stand-in launcher: start the engine, optionally bind it, report its PID, then wait to be killed or exit.
    string engineArgs = Environment.GetEnvironmentVariable("ENGINE_TEST_ARGS") ?? "/t 600 RegionLauncherEngineTest" + Guid.NewGuid().ToString("N")[..8];
    var engine = Process.Start(new ProcessStartInfo(args[1], engineArgs) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = engineArgs.StartsWith("-d"), RedirectStandardError = engineArgs.StartsWith("-d") })!;
    if (engineArgs.StartsWith("-d")) { _ = engine.StandardOutput.ReadToEndAsync(); _ = engine.StandardError.ReadToEndAsync(); }
    if (args[2] == "bind" && !EngineLifetime.Bind(engine, Console.Error.WriteLine)) { Console.WriteLine("BIND-FAILED"); return 2; }
    Console.WriteLine(engine.Id);
    Console.Out.Flush();
    if (args[2] == "exit") return 0;
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

string root = Path.Combine(Path.GetTempPath(), "rl-engine-" + Guid.NewGuid().ToString("N"));
string Engine(string name)
{
    string folder = Path.Combine(root, name, "proxy");
    Directory.CreateDirectory(folder);
    string path = Path.Combine(folder, "mihomo.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "waitfor.exe"), path);
    return path;
}
var started = new List<int>();
(Process Launcher, Process Engine) Start(string engine, string mode)
{
    var launcher = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "--launcher", engine, mode } })!;
    string line = launcher.StandardOutput.ReadLine() ?? "";
    if (!int.TryParse(line, out int pid)) throw new Exception("stand-in launcher failed: " + line);
    started.Add(pid);
    return (launcher, Process.GetProcessById(pid));
}
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
int failures = 0;
try {
    string mine = Engine("mine"), other = Engine("other");

    // 1. A bound engine ends when its launcher is killed (Task Manager, crash, forced update).
    var (bound, boundEngine) = Start(mine, "bind");
    bound.Kill();
    Check(boundEngine.WaitForExit(10000), "bound engine survived its launcher being killed");
    Console.WriteLine("PASS: an engine bound to its launcher ends when the launcher is killed");

    // 2. Without the binding (older versions), the engine is left running and is reported as an orphan.
    var (gone, orphan) = Start(mine, "exit");
    gone.WaitForExit();
    Thread.Sleep(300);
    Check(!orphan.HasExited, "unbound stand-in engine ended unexpectedly");
    // An engine whose launcher is still running, and an orphan from another installation, must not be reported.
    var (alive, aliveEngine) = Start(mine, "stay");
    var (otherGone, otherOrphan) = Start(other, "exit");
    otherGone.WaitForExit();
    var found = EngineLifetime.Orphans(mine, null);
    var ids = found.Select(p => p.Id).ToList();
    foreach (var p in found) p.Dispose();
    Check(ids.Contains(orphan.Id), "orphaned engine from this installation was not found");
    Check(!ids.Contains(aliveEngine.Id), "engine with a live launcher was reported as orphaned");
    Check(!ids.Contains(otherOrphan.Id), "engine from another installation was reported");
    Check(!EngineLifetime.Orphans(mine, orphan.Id).Any(p => p.Id == orphan.Id), "excluded engine was reported");
    alive.Kill(); alive.WaitForExit();
    Console.WriteLine("PASS: orphans are found only for this installation's engine whose launcher no longer exists");

    if (Environment.GetEnvironmentVariable("REGION_TEST_ENGINE") is { Length: > 0 } real) {
        int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        bool Bindable(int port) { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); try { s.ExclusiveAddressUse = true; s.Bind(new IPEndPoint(IPAddress.Loopback, port)); return true; } catch (SocketException) { return false; } }
        bool Within(Func<bool> condition, int ms) { var w = Stopwatch.StartNew(); while (w.ElapsedMilliseconds < ms) { if (condition()) return true; Thread.Sleep(100); } return condition(); }
        string realEngine = Path.Combine(root, "real", "proxy", "mihomo.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(realEngine)!);
        File.Copy(real, realEngine);
        string RunEngine(string mode, out Process launcher, out Process engine, out int port)
        {
            port = FreePort();
            string dir = Path.Combine(root, "real", "run-" + mode); Directory.CreateDirectory(dir);
            string config = Path.Combine(dir, "config.yaml");
            File.WriteAllLines(config, [$"mixed-port: {port}", "bind-address: 127.0.0.1", "allow-lan: false", "ipv6: false", "log-level: warning", "rules:", "  - MATCH,DIRECT"]);
            Environment.SetEnvironmentVariable("ENGINE_TEST_ARGS", $"-d \"{dir}\" -f \"{config}\"");
            (launcher, engine) = Start(realEngine, mode);
            Environment.SetEnvironmentVariable("ENGINE_TEST_ARGS", null);
            int p = port;
            Check(Within(() => !Bindable(p), 10000), "real engine did not start listening");
            return dir;
        }
        // Bound: killing the launcher frees the port.
        RunEngine("bind", out var l1, out var e1, out int p1);
        l1.Kill();
        Check(e1.WaitForExit(10000) && Within(() => Bindable(p1), 5000), "port still bound after the launcher was killed");
        Console.WriteLine($"PASS: real engine on port {p1} ends with its killed launcher and the port is free again");
        // Unbound (older versions): the port stays bound until the orphan is found and ended.
        RunEngine("exit", out var l2, out var e2, out int p2);
        l2.WaitForExit();
        Thread.Sleep(500);
        Check(!e2.HasExited && !Bindable(p2), "unbound real engine did not keep the port");
        foreach (var leftover in EngineLifetime.Orphans(realEngine, null)) using (leftover) { leftover.Kill(); leftover.WaitForExit(10000); }
        Check(e2.HasExited && Within(() => Bindable(p2), 5000), "port still bound after ending the orphaned engine");
        Console.WriteLine($"PASS: orphaned real engine kept port {p2} bound; ending it as an orphan released the port");
    }
} catch (Exception ex) {
    Console.WriteLine("FAIL: " + ex.Message); failures++;
} finally {
    foreach (int pid in started) { try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(5000); } catch (ArgumentException) { } catch (InvalidOperationException) { } }
    try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
return failures == 0 ? 0 : 1;
