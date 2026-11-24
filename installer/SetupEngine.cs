using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

/// <summary>Installs or upgrades from the embedded release ZIP. User data under app\data is never touched.</summary>
static class SetupEngine
{
    public static string Version => Application.ProductVersion.Split('+')[0];

    public static Stream? Payload() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

    public static string License()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EULA.md");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        // Show the Markdown as plain text.
        return string.Join("\r\n", reader.ReadToEnd().Replace("\r\n", "\n").Split('\n')
            .Select(line => line.StartsWith("## ") ? "\r\n" + line[3..] : line.StartsWith("# ") ? line[2..] : line.Replace("**", "")));
    }

    /// <summary>Null when the folder can be used, otherwise why not.</summary>
    public static string? CheckTarget(string root)
    {
        string full;
        try { full = Path.GetFullPath(root); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "安装位置不是有效的路径。"; }
        if (!Path.IsPathFullyQualified(root)) return "请填写完整的安装路径。";
        if (Path.GetPathRoot(full) is { } drive && Installation.SamePath(drive, full)) return "不能直接安装到磁盘根目录，请选择一个文件夹。";
        if (File.Exists(full)) return "安装位置是一个文件。";
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !Installation.IsOwnedDirectory(full) && !Installation.IsPortableDirectory(full))
            return "所选文件夹不为空，且不是 Region Launcher 的安装位置或便携版目录。请选择空文件夹或新文件夹。";
        return null;
    }

    public sealed record Options(string Root, bool DesktopShortcut, bool Upgrade);

    /// <summary>Runs the whole installation; returns a service warning (null when the service is ready or skipped).</summary>
    public static string? Install(Options options, IProgress<(double Value, string Text)> progress, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(options.Root);
        if (CheckTarget(root) is { } invalid) throw new IOException(invalid);
        using var payload = Payload() ?? throw new IOException("安装程序不完整：缺少程序文件。");
        Directory.CreateDirectory(root);
        progress.Report((0.02, "正在复制程序文件…"));
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read)) Extract(zip, root, value => progress.Report((0.02 + value * 0.78, "正在复制程序文件…")), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report((0.82, "正在写入注册表和快捷方式…"));
        Installation.WriteMarker(root, Version);
        Installation.Register(root, Version);
        Installation.CreateShortcut(Installation.StartMenuShortcut, Installation.Launcher(root));
        if (options.DesktopShortcut) Installation.CreateShortcut(Installation.DesktopShortcut, Installation.Launcher(root));
        if (Installation.SkipService) { progress.Report((1, "已跳过后台网络服务。")); return null; }
        var state = Installation.Service(root);
        if (state == Installation.ServiceState.Current) { progress.Report((1, "后台网络服务已安装且为当前版本，已跳过。")); return null; }
        progress.Report((0.88, state == Installation.ServiceState.Outdated
            ? "正在更新后台网络服务，请在 Windows 管理员确认窗口中选择“是”…" : "正在安装后台网络服务，请在 Windows 管理员确认窗口中选择“是”…"));
        string? error = Installation.RequestService(root, uninstall: false);
        progress.Report((1, error == null ? "后台网络服务已就绪。" : "后台网络服务未安装：" + error));
        return error;
    }

    static void Extract(ZipArchive zip, string root, Action<double> progress, CancellationToken cancellationToken)
    {
        string prefix = root.TrimEnd('\\') + "\\";
        var files = zip.Entries.Where(e => e.Name.Length != 0).ToList();
        for (int i = 0; i < files.Count; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = files[i];
            string relative = entry.FullName.Replace('/', '\\');
            if (relative.StartsWith(@"app\data\", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("程序文件包含无效路径：" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".new";
            entry.ExtractToFile(temp, true);
            Replace(temp, target);
            progress((i + 1.0) / files.Count);
        }
    }

    // Files may stay locked briefly while the old launcher exits.
    static void Replace(string temp, string target)
    {
        for (int attempt = 0; ; attempt++) {
            try {
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                File.Move(temp, target, true);
                return;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (attempt >= 40) { try { File.Delete(temp); } catch (IOException) { } throw new IOException($"无法替换 {Path.GetFileName(target)}，它可能仍在使用中。请退出 Region Launcher 后重试。", ex); }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Waits for the launcher that requested the update, then for anything else still running from the installation.</summary>
    public static bool WaitForExit(string root, int pid, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        try { using var process = Process.GetProcessById(pid); process.WaitForExit(timeout); } catch (ArgumentException) { }
        return Installation.WaitUntilClosed([root], timeout - deadline.Elapsed > TimeSpan.Zero ? timeout - deadline.Elapsed : TimeSpan.Zero);
    }

    public static void Launch(string root)
    {
        try { Process.Start(new ProcessStartInfo(Installation.Launcher(root)) { UseShellExecute = false, WorkingDirectory = Path.Combine(root, "app") })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
