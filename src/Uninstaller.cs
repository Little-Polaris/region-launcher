using System.Diagnostics;

/// <summary>
/// "--uninstall" (from Windows Settings) copies the launcher to a temporary folder and runs the removal from there,
/// so the whole installation directory can be deleted.
/// </summary>
static class Uninstaller
{
    public static bool TryRun(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--uninstall") { Relaunch(); return true; }
        if (args.Length >= 2 && args[0] == "--uninstall-run") {
            bool silent = args.Contains("--silent");
            bool deleteData = args.Contains("--delete-data");
            Environment.ExitCode = Run(args[1], silent, deleteData);
            return true;
        }
        return false;
    }

    static void Relaunch()
    {
        string app = AppContext.BaseDirectory;
        string root = Path.GetFullPath(Path.Combine(app, ".."));
        if (!Installation.IsOwnedDirectory(root)) {
            MessageDialog.Show(null, "这个目录不是由安装程序创建的 Region Launcher，无法自动卸载。便携版直接删除整个文件夹即可。", "卸载 Region Launcher",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string temp = Path.Combine(Path.GetTempPath(), "RegionLauncherUninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        foreach (string file in Directory.EnumerateFiles(app))
            if (Path.GetExtension(file) is ".exe" or ".dll" or ".json") File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
        var psi = new ProcessStartInfo(Path.Combine(temp, "RegionLauncher.exe")) { UseShellExecute = false, WorkingDirectory = temp };
        psi.ArgumentList.Add("--uninstall-run"); psi.ArgumentList.Add(root);
        Process.Start(psi)?.Dispose();
    }

    static int Run(string root, bool silent, bool deleteData)
    {
        root = Path.GetFullPath(root);
        try {
            if (!Installation.IsOwnedDirectory(root)) {
                if (!silent) MessageDialog.Show(null, $"未在 {root} 找到由安装程序创建的 Region Launcher，未做任何修改。", "卸载 Region Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
            if (!silent) {
                using var dialog = new UninstallDialog(root);
                if (dialog.ShowDialog() != DialogResult.OK) return 1;
                return dialog.Succeeded ? 0 : 3;
            }
            var log = new List<string>();
            return Remove(root, deleteData, log.Add) ? 0 : 3;
        } finally { ScheduleSelfCleanup(); }
    }

    /// <summary>Removes service, shortcuts, registry entries and files. Returns false when something was left behind.</summary>
    internal static bool Remove(string root, bool deleteData, Action<string> report)
    {
        bool complete = true;
        if (!Installation.SkipService && Installation.Service(root) != Installation.ServiceState.NotInstalled) {
            report("正在卸载后台网络服务，请在 Windows 管理员确认窗口中选择“是”…");
            if (Installation.RequestService(root, uninstall: true) is { } error) { complete = false; report("后台网络服务未卸载：" + error); }
            else report("后台网络服务已卸载。");
        }
        Installation.RemoveShortcuts(root);
        report("已删除快捷方式。");
        if (Installation.InstalledDirectory() is not { } registered || Installation.SamePath(registered, root)) {
            Installation.Unregister();
            report("已清除注册表信息。");
        }
        string data = Path.Combine(root, "app", "data");
        var failed = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(root).ToList()) {
            if (!deleteData && Installation.SamePath(entry, Path.Combine(root, "app"))) {
                foreach (string inner in Directory.EnumerateFileSystemEntries(entry).ToList())
                    if (!Installation.SamePath(inner, data)) Delete(inner, failed);
                continue;
            }
            Delete(entry, failed);
        }
        if (deleteData) Delete(root, failed);
        if (failed.Count != 0) { complete = false; report("以下文件暂时无法删除，可手动删除：" + string.Join("；", failed.Take(5))); }
        report(deleteData ? "程序文件和个人数据已删除。" : $"程序文件已删除，个人数据保留在 {data}。");
        return complete;
    }

    static void Delete(string path, List<string> failed)
    {
        for (int attempt = 0; ; attempt++) {
            try {
                if (Directory.Exists(path)) {
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
                    Directory.Delete(path, true);
                } else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                return;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (attempt >= 10) { failed.Add(path); return; }
                Thread.Sleep(300);
            }
        }
    }

    // The temporary copy removes itself after exiting.
    static void ScheduleSelfCleanup()
    {
        string self = AppContext.BaseDirectory.TrimEnd('\\');
        if (!Path.GetFileName(self).StartsWith("RegionLauncherUninstall-", StringComparison.Ordinal) ||
            !Installation.SamePath(Path.GetDirectoryName(self)!, Path.GetTempPath())) return;
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add($"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{self}\"");
        try { Process.Start(psi)?.Dispose(); } catch (System.ComponentModel.Win32Exception) { }
    }
}

sealed class UninstallDialog : Form
{
    readonly AntdUI.Checkbox deleteData = new() { Text = "同时删除我的个人数据（节点、订阅、程序列表、授权浏览器资料和日志）", Checked = true, AutoSizeMode = AntdUI.TAutoSize.Height, Dock = DockStyle.Top };
    readonly TextBox progress = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = HomeTheme.Surface };
    readonly AntdUI.Button start = HomeTheme.Action("卸载", AntdUI.TTypeMini.Error, "DeleteOutlined");
    readonly AntdUI.Button close = HomeTheme.Action("取消");
    public bool Succeeded { get; private set; }

    public UninstallDialog(string root)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22, 16, 22, 8), ColumnCount = 1, RowCount = 4, BackColor = HomeTheme.Surface };
        body.RowStyles.Add(new(SizeType.AutoSize)); body.RowStyles.Add(new(SizeType.AutoSize)); body.RowStyles.Add(new(SizeType.AutoSize)); body.RowStyles.Add(new(SizeType.Percent, 100));
        body.Controls.Add(new Label { Text = "卸载 Region Launcher", AutoSize = true, Font = new(HomeTheme.FontName, 13, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = new(0, 0, 0, 8) });
        body.Controls.Add(new Label { Text = $"将删除 {root} 中的程序文件、开始菜单和桌面快捷方式及注册表信息，并在你确认后卸载后台网络服务。卸载前请先退出启动器。",
            AutoSize = true, MaximumSize = new(560, 0), ForeColor = HomeTheme.Muted, Margin = new(0, 0, 0, 10) });
        deleteData.Anchor = AnchorStyles.Left | AnchorStyles.Right; deleteData.Dock = DockStyle.None;
        body.Controls.Add(deleteData);
        var frame = new LogFrame(3, 6) { Dock = DockStyle.Fill, Margin = new(0, 8, 0, 0) };
        frame.Controls.Add(progress);
        body.Controls.Add(frame);
        CancelButton = close;
        bool finished = false;
        close.Click += (_, _) => DialogResult = finished ? DialogResult.OK : DialogResult.Cancel;
        start.Click += async (_, _) => {
            var running = Installation.RunningFrom(root);
            if (running.Count != 0) {
                foreach (var p in running) p.Dispose();
                MessageDialog.Show(this, "Region Launcher 仍在运行。请先在托盘图标上右键选择“退出（结束全部运行）”，然后再卸载。", "卸载 Region Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (deleteData.Checked && MessageDialog.Show(this, "个人数据删除后无法恢复，确定继续？", "卸载 Region Launcher", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            start.Enabled = deleteData.Enabled = close.Enabled = false; start.Loading = true;
            bool delete = deleteData.Checked;
            var report = new Progress<string>(line => progress.AppendText(line + Environment.NewLine));
            Succeeded = await Task.Run(() => Uninstaller.Remove(root, delete, ((IProgress<string>)report).Report));
            start.Loading = false; start.Visible = false;
            close.Text = "完成"; close.Type = AntdUI.TTypeMini.Primary; close.Enabled = true;
            finished = true;
            ((IProgress<string>)report).Report(Succeeded ? "卸载完成。" : "卸载已完成，但有项目未能清理，详见上方说明。");
        };
        var footer = HomeTheme.Footer(close, start); footer.Dock = DockStyle.Bottom;
        Controls.Add(body); Controls.Add(footer);
        HomeTheme.Dialog(this, "卸载 Region Launcher", new(620, 400), new(520, 340));
        ShowInTaskbar = true; StartPosition = FormStartPosition.CenterScreen; Icon = AppIcon.Load(new Size(32, 32));
    }
}
