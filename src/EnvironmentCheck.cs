using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class EnvironmentCheck
{
    public static async Task<ProbeResult[]> RunAsync(string bin, string data, int port, string dns, string? existingJob, Action<string> log, DiagnosticLog? diagnostics)
    {
        string id = Guid.NewGuid().ToString("N"), folder = Path.Combine(data, "sessions", "check-" + id);
        Directory.CreateDirectory(folder);
        string report = Path.Combine(folder, "connectivity.json"), config = Path.Combine(folder, "session.ini");
        string job = existingJob ?? "Local\\RegionLauncher." + id;
        await File.WriteAllTextAsync(config, $"[session]\nJob={job}\nOwnerPid={Environment.ProcessId}\nApplication={Path.Combine(bin, "RegionLauncher.exe")}\nArguments=--network-probe \"{report}\"\nProxyHost=127.0.0.1\nProxyPort={port}\nDnsAddress={dns}\n", Encoding.Unicode);
        var psi = new ProcessStartInfo(Path.Combine(bin, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(existingJob == null ? "--run" : "--join"); psi.ArgumentList.Add(config);
        log($"环境内实际联网检测开始，编号 {id}");
        using var process = Process.Start(psi) ?? throw new IOException("无法启动环境检测程序。");
        var reads = Task.WhenAll(ProcessLog.ReadAsync(process.StandardOutput.BaseStream, s => log("[环境检测] " + s)), ProcessLog.ReadAsync(process.StandardError.BaseStream, s => log("[环境检测] " + s)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try {
            await process.WaitForExitAsync(timeout.Token);
            await reads.WaitAsync(TimeSpan.FromSeconds(5));
            if (!File.Exists(report) || new FileInfo(report).Length > 65536) throw new IOException("环境检测未生成有效结果，请查看网络服务与检测程序日志。");
            var results = JsonSerializer.Deserialize<ProbeResult[]>(await File.ReadAllTextAsync(report)) ?? throw new IOException("环境检测结果为空。");
            if (process.ExitCode != 0) { ProxyHealth.RequireInternet(results); throw new IOException($"环境检测进程退出异常：{process.ExitCode}。"); }
            return results;
        } catch (OperationCanceledException) {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            throw new IOException("环境内联网检测超时；本次目标程序没有启动。");
        } finally { diagnostics?.CaptureNetworkLog("检测 " + id); }
    }
}
