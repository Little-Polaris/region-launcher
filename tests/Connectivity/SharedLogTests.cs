using System.Diagnostics;

static class SharedLogTests
{
    public static async Task Run(string folder)
    {
        string tool = Environment.GetEnvironmentVariable("REGION_TEST_LOG_WRITER") ?? throw new Exception("Set REGION_TEST_LOG_WRITER to the compiled SharedLogWriter fixture");
        string path = Path.Combine(folder, "held-network.log");
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        try {
            if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "READY") throw new Exception("Native writer did not start");
            string directory = Path.Combine(folder, "shared-log-" + Guid.NewGuid().ToString("N"));
            using (var log = new DiagnosticLog(directory)) log.CaptureNetworkLog("held-writer", path);
            string text = string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText));
            if (!text.Contains("held-writer token=[已隐藏]") || text.Contains("暂不可读") || text.Contains("fixture-secret")) throw new Exception("Active native log read/redaction failed");
            if (text.Split('\n').Length > 14000) throw new Exception("Snapshot exceeded tail bound");
            bool denied = false;
            try { using var second = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { denied = true; }
            if (!denied) throw new Exception("Shared log permits a conflicting writer");
            await process.StandardInput.WriteLineAsync(); await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0 || !File.ReadAllText(path).EndsWith("writer-still-usable\n")) throw new Exception("Reading snapshot disrupted native writer");
            Console.WriteLine("PASS: active native writer remains writable, snapshot readable/bounded/redacted, conflicting writer denied");
        } finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
