using System.Text;
using System.Text.RegularExpressions;

sealed class DiagnosticLog : IDisposable
{
    readonly object sync = new();
    readonly List<string> secrets = [];
    readonly string prefix = $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    StreamWriter? writer;
    int part;
    bool failed, disposed;
    public string DirectoryPath { get; }
    public DiagnosticLog(string directory)
    {
        DirectoryPath = directory; Directory.CreateDirectory(directory); Open();
    }
    void Open()
    {
        writer = new StreamWriter(Path.Combine(DirectoryPath, $"{prefix}-{part++}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        // Bound generated logs to about 50 MiB, preserving the newest ten parts.
        try {
            foreach (var file in new DirectoryInfo(DirectoryPath).GetFiles("launcher-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void AddSecrets(IEnumerable<string> values)
    {
        lock (sync) foreach (string value in values.Where(v => v.Length >= 4).Distinct()) if (!secrets.Contains(value)) secrets.Add(value);
    }
    public string Write(string text)
    {
        lock (sync) {
            foreach (string secret in secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
            text = Regex.Replace(text, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[已隐藏]@");
            text = Regex.Replace(text, @"(?i)(https?://[^\s?\""<>]+)\?[^\s\""<>]*", "$1?[已隐藏]");
            text = Regex.Replace(text, @"(?i)\b(authorization|proxy-authorization)\s*:\s*[^\r\n]*", "$1: [已隐藏]");
            text = Regex.Replace(text, @"(?i)\b(password|passwd|access[-_]?token|refresh[-_]?token|token|secret|uuid|api[-_]?key)(\""?\s*[:=]\s*)(\""[^\""\r\n]*\""|[^\s,;]+)", "$1$2[已隐藏]");
            string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {text}";
            try {
                if (!disposed) {
                    if (writer == null) Open();
                    if (writer!.BaseStream.Length >= 5 * 1024 * 1024) { writer.Dispose(); writer = null; Open(); }
                    writer!.WriteLine(line);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) {
                if (!failed) { line += "\n日志写入失败：" + ex.Message; failed = true; }
            }
            return line;
        }
    }
    public void CaptureNetworkLog(string label)
    {
        try {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RegionLauncherNetwork", "logs", "network.log");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - 512 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            Write($"[{label}] 后台网络日志快照（末尾最多 512 KiB）");
            while (reader.ReadLine() is string line) Write("[网络服务] " + line);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Write("后台日志暂不可读：" + ex.Message); }
    }
    public void Dispose() { lock (sync) { disposed = true; writer?.Dispose(); } }
}
