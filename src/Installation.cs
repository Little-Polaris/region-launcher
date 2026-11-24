using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

// Shared by the launcher (update, uninstall, first-run wizard) and the installer.
static class Installation
{
    public const string ProductName = "Region Launcher";
    public const string Publisher = "Little-Polaris";
    public const string Repository = "Little-Polaris/region-launcher";
    public const string ServiceName = "RegionLauncherNetwork";
    public const string MarkerFile = "installation.json";
    const string ShortcutName = "Region Launcher.lnk";

    // Tests redirect registry and shortcut writes away from the real user profile.
    static string RegistryRoot => Environment.GetEnvironmentVariable("REGION_LAUNCHER_REGISTRY_ROOT") is { Length: > 0 } root ? root : "Software";
    public static string ProductKey => RegistryRoot + @"\RegionLauncher";
    public static string UninstallKey => RegistryRoot + @"\Microsoft\Windows\CurrentVersion\Uninstall\RegionLauncher";
    public static bool SkipService => Environment.GetEnvironmentVariable("REGION_LAUNCHER_SKIP_SERVICE") == "1";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RegionLauncher");

    public static string Launcher(string root) => Path.Combine(root, "app", "RegionLauncher.exe");

    /// <summary>The registered installation root, if it still holds the launcher.</summary>
    public static string? InstalledDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ProductKey);
        if (key?.GetValue("InstallLocation") is not string location || location.Length == 0) return null;
        try { location = Path.GetFullPath(location); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return File.Exists(Launcher(location)) ? location : null;
    }

    public static string? InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ProductKey);
        return key?.GetValue("Version") as string;
    }

    /// <summary>The installation root that contains this app directory when it is the registered installation.</summary>
    public static string? CurrentInstallation(string appDirectory)
    {
        string root = Path.GetFullPath(Path.Combine(appDirectory, ".."));
        return InstalledDirectory() is { } installed && SamePath(installed, root) && IsOwnedDirectory(root) ? root : null;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Only directories the installer created (marker present, launcher inside) may be overwritten or removed.</summary>
    public static bool IsOwnedDirectory(string root)
    {
        try {
            string full = Path.GetFullPath(root);
            if (Path.GetPathRoot(full) is { } drive && SamePath(drive, full)) return false;
            string marker = Path.Combine(full, MarkerFile);
            if (!File.Exists(marker) || !File.Exists(Launcher(full))) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            return json.RootElement.TryGetProperty("product", out var product) && product.GetString() == "RegionLauncher";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return false; }
    }

    public static void WriteMarker(string root, string version) =>
        File.WriteAllText(Path.Combine(root, MarkerFile), JsonSerializer.Serialize(new { product = "RegionLauncher", version, installed = DateTime.Now.ToString("o") }));

    public static void Register(string root, string version)
    {
        string launcher = Launcher(root);
        using (var product = Registry.CurrentUser.CreateSubKey(ProductKey)) {
            product.SetValue("InstallLocation", root);
            product.SetValue("Version", version);
        }
        using var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey);
        uninstall.SetValue("DisplayName", ProductName);
        uninstall.SetValue("DisplayVersion", version);
        uninstall.SetValue("Publisher", Publisher);
        uninstall.SetValue("InstallLocation", root);
        uninstall.SetValue("DisplayIcon", launcher + ",0");
        uninstall.SetValue("UninstallString", $"\"{launcher}\" --uninstall");
        uninstall.SetValue("URLInfoAbout", $"https://github.com/{Repository}");
        uninstall.SetValue("HelpLink", $"https://github.com/{Repository}/issues");
        uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstall.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, DirectorySize(root) / 1024), RegistryValueKind.DWord);
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(ProductKey, false);
    }

    static long DirectorySize(string root)
    {
        try { return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---- Shortcuts ----

    static string ShortcutFolder(Environment.SpecialFolder folder) =>
        Environment.GetEnvironmentVariable("REGION_LAUNCHER_SHORTCUT_DIR") is { Length: > 0 } test ? Path.Combine(test, folder.ToString()) : Environment.GetFolderPath(folder);

    public static string StartMenuShortcut => Path.Combine(ShortcutFolder(Environment.SpecialFolder.Programs), ShortcutName);
    public static string DesktopShortcut => Path.Combine(ShortcutFolder(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    public static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows 脚本宿主不可用，无法创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "为所选程序提供独立的地区环境";
            link.Save();
        } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Removes our shortcuts, but only those that point into this installation.</summary>
    public static void RemoveShortcuts(string root)
    {
        foreach (string path in new[] { StartMenuShortcut, DesktopShortcut }) {
            if (!File.Exists(path)) continue;
            try {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                string target = "";
                if (shellType != null) {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    try { target = (string)shell.CreateShortcut(path).TargetPath; } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                }
                if (target == "" || SamePath(target, Launcher(root))) File.Delete(path);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        }
    }

    // ---- Background network service ----

    public enum ServiceState { NotInstalled, Outdated, Current }

    /// <summary>Compares the installed service files with the service package shipped in this installation.</summary>
    public static ServiceState Service(string root)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName))
            if (key == null) return ServiceState.NotInstalled;
        try {
            var shipped = FileHashes(Path.Combine(root, "install", "package", "manifest.json"));
            var installed = FileHashes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName, MarkerFile));
            return shipped.Count != 0 && shipped.Count == installed.Count &&
                shipped.All(file => installed.TryGetValue(file.Key, out var hash) && string.Equals(hash, file.Value, StringComparison.OrdinalIgnoreCase))
                ? ServiceState.Current : ServiceState.Outdated;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) {
            return ServiceState.Outdated;
        }
    }

    static Dictionary<string, string> FileHashes(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
    }

    public static string ServiceText(ServiceState state) => state switch {
        ServiceState.Current => "后台网络服务已安装，且为当前版本。",
        ServiceState.Outdated => "后台网络服务已安装，但与当前版本不一致，需要更新。",
        _ => "后台网络服务尚未安装。",
    };

    /// <summary>
    /// Installs, updates or removes the service through the shipped request script, which asks for Windows administrator approval.
    /// Returns null on success, otherwise the reason.
    /// </summary>
    public static string? RequestService(string root, bool uninstall)
    {
        string script = Path.Combine(root, "install", "request_install.ps1");
        if (!File.Exists(script)) return "安装目录中缺少后台服务安装脚本。";
        var started = DateTime.Now.AddSeconds(-1);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(arg);
        if (uninstall) psi.ArgumentList.Add("-Uninstall");
        try {
            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            var result = new DirectoryInfo(Path.Combine(root, "evidence")).EnumerateFiles("service-*.json")
                .Where(f => f.LastWriteTime >= started).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (result != null) {
                using var json = JsonDocument.Parse(File.ReadAllText(result.FullName));
                if (json.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True) return null;
                string error = json.RootElement.TryGetProperty("error", out var e) ? e.ToString() : "";
                return Explain(error);
            }
            return process.ExitCode == 0 ? null : Explain(output.Trim());
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException or UnauthorizedAccessException) {
            return ex.Message;
        }
    }

    static string Explain(string error) =>
        error.Contains("canceled by the user", StringComparison.OrdinalIgnoreCase) || error.Contains("已取消") || error.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            ? "已取消 Windows 管理员确认。" : error.Length == 0 ? "未知原因，请查看安装目录下 evidence 中的结果文件。" : error;

    // ---- Processes ----

    /// <summary>Launcher or session processes started from this installation.</summary>
    public static List<Process> RunningFrom(string root, int? except = null)
    {
        var found = new List<Process>();
        foreach (string name in new[] { "RegionLauncher", "RegionSession" })
            foreach (var process in Process.GetProcessesByName(name)) {
                bool match = false;
                try { match = process.Id != except && process.MainModule?.FileName is { } path && path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                if (match) found.Add(process); else process.Dispose();
            }
        return found;
    }

    public static bool DesktopRuntimeInstalled()
    {
        foreach (string? baseDir in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") }) {
            if (string.IsNullOrEmpty(baseDir)) continue;
            string shared = Path.Combine(baseDir, "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(shared) && Directory.EnumerateDirectories(shared, "10.*").Any()) return true;
        }
        return false;
    }

    public const string RuntimeDownloadPage = "https://dotnet.microsoft.com/download/dotnet/10.0";

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
