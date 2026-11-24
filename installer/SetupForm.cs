using System.Reflection;

/// <summary>Installer wizard: agreement → location → install → finish. Update mode skips straight to installing.</summary>
sealed class SetupForm : Form
{
    readonly Label title = new() { AutoSize = true, Font = new(HomeTheme.FontName, 14, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = Padding.Empty };
    readonly Label subtitle = new() { AutoSize = true, ForeColor = HomeTheme.Muted, Margin = new(0, 4, 0, 0) };
    readonly Panel page = new() { Dock = DockStyle.Fill, Padding = new(28, 12, 28, 8), BackColor = HomeTheme.Surface };
    readonly AntdUI.Button back = HomeTheme.Action("上一步");
    readonly AntdUI.Button next = HomeTheme.Action("下一步", AntdUI.TTypeMini.Primary);
    readonly AntdUI.Button cancel = HomeTheme.Action("取消");

    // License page
    readonly TextBox license = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = HomeTheme.Surface };
    readonly AntdUI.Checkbox accept = new() { Text = "我已阅读并同意《Region Launcher 用户协议》", AutoSizeMode = AntdUI.TAutoSize.Auto, Dock = DockStyle.Bottom };
    // Location page
    readonly AntdUI.Input location = HomeTheme.Input("安装位置");
    readonly AntdUI.Checkbox desktop = new() { Text = "创建桌面快捷方式", Checked = true, AutoSizeMode = AntdUI.TAutoSize.Auto };
    // Progress page
    readonly AntdUI.Progress bar = new() { Height = 26, Radius = 6, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = HomeTheme.Surface };
    // Finish page
    readonly AntdUI.Checkbox launch = new() { Text = "立即启动 Region Launcher", Checked = true, AutoSizeMode = AntdUI.TAutoSize.Auto };

    readonly string? existing = Installation.InstalledDirectory();
    // A running portable copy (unpacked ZIP) is offered for in-place conversion.
    readonly string? portable;
    readonly Label mode = new() { AutoSize = true, ForeColor = HomeTheme.Accent, Margin = new(3, 2, 3, 6) };
    readonly string? updateRoot;
    readonly int updatePid;
    int index;
    bool installing, installed;
    string? failure, serviceWarning;

    public SetupForm(string? updateRoot = null, int updatePid = 0)
    {
        this.updateRoot = updateRoot; this.updatePid = updatePid;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RegionLauncher.AppIcon.ico")) if (stream != null) Icon = new Icon(stream);
        license.Text = SetupEngine.License();
        license.Font = new(HomeTheme.FontName, 9.5f);
        log.Font = new("Consolas", 9.5f);
        portable = existing == null ? Installation.RunningLauncherRoots().FirstOrDefault(Installation.IsPortableDirectory) : null;
        location.Text = existing ?? portable ?? Installation.DefaultDirectory;
        location.TextChanged += (_, _) => UpdateMode();
        accept.CheckedChanged += (_, _) => next.Enabled = accept.Checked || index != 0;

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(28, 18, 28, 14), ColumnCount = 1, BackColor = HomeTheme.Background };
        header.Controls.Add(title); header.Controls.Add(subtitle);
        back.Click += (_, _) => Show(index - 1);
        next.Click += (_, _) => Next();
        cancel.Click += (_, _) => Close();
        var footer = HomeTheme.Footer(next, back, cancel); footer.Dock = DockStyle.Bottom;
        Controls.Add(page); Controls.Add(header); Controls.Add(footer);
        HomeTheme.Dialog(this, $"Region Launcher {SetupEngine.Version} 安装", new(680, 520), new(560, 440));
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true; MaximizeBox = false;
        Shown += (_, _) => { if (updateRoot != null) { location.Text = updateRoot; Show(2); _ = RunInstall(); } };
        Show(updateRoot != null ? 2 : 0);
    }

    void Show(int target)
    {
        if (target < 0 || target > 3) return;
        index = target;
        page.SuspendLayout();
        foreach (Control control in page.Controls.Cast<Control>().ToList()) page.Controls.Remove(control);
        back.Visible = index == 1;
        cancel.Visible = index < 2;
        next.Visible = true; next.Enabled = true;
        switch (index) {
            case 0:
                title.Text = "用户协议"; subtitle.Text = "安装前请阅读并同意以下协议。";
                var frame = new LogFrame(0, 8) { Dock = DockStyle.Fill };
                frame.Controls.Add(license);
                page.Controls.Add(frame); page.Controls.Add(accept);
                next.Text = "下一步"; next.Enabled = accept.Checked;
                break;
            case 1:
                title.Text = existing != null ? "升级 Region Launcher" : "安装位置";
                subtitle.Text = existing != null ? $"检测到已安装的 {Installation.InstalledVersion() ?? "旧版本"}，将在原位置升级，节点、订阅和程序列表会保留。"
                    : portable != null ? "检测到正在使用的便携版，默认原地转为安装版，也可以改装到其他位置。" : "Region Launcher 将安装到当前用户目录，无需管理员权限。";
                page.Controls.Add(LocationPage());
                UpdateMode();
                break;
            case 2:
                title.Text = updateRoot != null ? "正在更新 Region Launcher" : "正在安装"; subtitle.Text = "请稍候，安装过程中可能会出现 Windows 管理员确认窗口。";
                page.Controls.Add(ProgressPage());
                next.Visible = false;
                break;
            default:
                title.Text = failure == null ? (updateRoot != null ? "更新完成" : "安装完成") : "安装未完成";
                subtitle.Text = failure == null ? "Region Launcher 已可以使用。" : "安装过程中出现问题，详见下方说明。";
                page.Controls.Add(FinishPage());
                next.Text = "完成";
                break;
        }
        page.ResumeLayout(true);
    }

    Control LocationPage()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = HomeTheme.Surface };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.AutoSize));
        var browse = HomeTheme.Action("浏览…", AntdUI.TTypeMini.Default, "FolderOpenOutlined");
        browse.Click += (_, _) => {
            using var dialog = new FolderBrowserDialog { Description = "选择安装位置", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                location.Text = Directory.EnumerateFileSystemEntries(dialog.SelectedPath).Any() && !Installation.IsOwnedDirectory(dialog.SelectedPath) &&
                    !Installation.IsPortableDirectory(dialog.SelectedPath) ? Path.Combine(dialog.SelectedPath, "RegionLauncher") : dialog.SelectedPath;
        };
        location.ReadOnly = existing != null; browse.Enabled = existing == null;
        grid.Controls.Add(HomeTheme.Caption("安装到"), 0, 0); grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 2);
        grid.Controls.Add(location, 0, 1); grid.Controls.Add(browse, 1, 1);
        grid.Controls.Add(mode, 0, 2); grid.SetColumnSpan(mode, 2);
        mode.MaximumSize = new(LogicalToDeviceUnits(600), 0);
        grid.Controls.Add(desktop, 0, 3);
        int row = 4;
        if (!Installation.DesktopRuntimeInstalled()) {
            var runtime = new AntdUI.Alert { TextTitle = "需要 .NET 10 桌面运行时", Text = "这台电脑尚未安装 .NET 10 Desktop Runtime x64，安装后才能打开 Region Launcher。",
                Icon = AntdUI.TType.Warn, Radius = 8, Dock = DockStyle.Fill, Height = LogicalToDeviceUnits(66), Margin = new(3, 12, 3, 4) };
            var get = HomeTheme.Link("打开 .NET 下载页", "LinkOutlined");
            get.Click += (_, _) => Installation.OpenUrl(Installation.RuntimeDownloadPage);
            grid.Controls.Add(runtime, 0, row); grid.SetColumnSpan(runtime, 2);
            grid.Controls.Add(get, 0, row + 1); row += 2;
        }
        var note = HomeTheme.Hint("安装完成后会检查后台网络服务：已是当前版本则跳过，否则请求一次 Windows 管理员确认来安装或更新。");
        note.MaximumSize = new(LogicalToDeviceUnits(600), 0); note.Margin = new(3, 14, 3, 0);
        grid.Controls.Add(note, 0, row); grid.SetColumnSpan(note, 2);
        return grid;
    }

    // Explains what happens at the chosen path: fresh install, in-place upgrade, or portable conversion.
    void UpdateMode()
    {
        string path = location.Text.Trim();
        bool owned = false, isPortable = false;
        try { owned = Path.IsPathFullyQualified(path) && Installation.IsOwnedDirectory(path); isPortable = !owned && Path.IsPathFullyQualified(path) && Installation.IsPortableDirectory(path); }
        catch (ArgumentException) { }
        mode.Text = owned ? "这是已有的安装位置：将替换程序文件，保留 app\\data 中的节点、订阅和程序列表。"
            : isPortable ? $"这是便携版 {Installation.CopyVersion(path) ?? ""} 的目录：将原地转为安装版，替换程序文件，保留 app\\data 中的节点、订阅、程序列表和浏览器资料。"
            : "";
        mode.Visible = mode.Text.Length != 0;
        if (index == 1) next.Text = owned || isPortable ? "升级" : "安装";
    }

    Control ProgressPage()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = HomeTheme.Surface };
        grid.RowStyles.Add(new(SizeType.AutoSize)); grid.RowStyles.Add(new(SizeType.Percent, 100));
        bar.Margin = new(3, 6, 3, 12);
        grid.Controls.Add(bar, 0, 0);
        var frame = new LogFrame(0, 8) { Dock = DockStyle.Fill };
        frame.Controls.Add(log);
        grid.Controls.Add(frame, 0, 1);
        return grid;
    }

    Control FinishPage()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = HomeTheme.Surface };
        int width = LogicalToDeviceUnits(600);
        if (failure != null)
            flow.Controls.Add(new AntdUI.Alert { TextTitle = "安装失败", Text = failure, Icon = AntdUI.TType.Error, Radius = 8, Width = width, Height = LogicalToDeviceUnits(80) });
        else {
            flow.Controls.Add(new AntdUI.Alert { TextTitle = "已安装到", Text = Path.GetFullPath(location.Text), Icon = AntdUI.TType.Success, Radius = 8, Width = width, Height = LogicalToDeviceUnits(66), Margin = new(0, 0, 0, 10) });
            if (serviceWarning != null)
                flow.Controls.Add(new AntdUI.Alert { TextTitle = "后台网络服务尚未就绪", Text = serviceWarning + " 可以稍后在启动器的使用向导中重新安装。", Icon = AntdUI.TType.Warn, Radius = 8, Width = width, Height = LogicalToDeviceUnits(80), Margin = new(0, 0, 0, 10) });
            var hint = HomeTheme.Hint("可在开始菜单中找到 Region Launcher；卸载请使用 Windows“设置 → 应用 → 已安装的应用”。");
            hint.MaximumSize = new(width, 0); hint.Margin = new(0, 0, 0, 12);
            flow.Controls.Add(hint);
            launch.Enabled = Installation.DesktopRuntimeInstalled();
            if (!launch.Enabled) launch.Checked = false;
            flow.Controls.Add(launch);
        }
        return flow;
    }

    async void Next()
    {
        if (index == 0) { if (accept.Checked) Show(1); return; }
        if (index == 1) {
            string root = location.Text.Trim();
            if (SetupEngine.CheckTarget(root) is { } error) { MessageDialog.Show(this, error, "安装位置", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            // Any running launcher must exit first: files may be in use, and only one launcher can run at a time.
            next.Enabled = back.Enabled = cancel.Enabled = false; next.Loading = true;
            bool closed;
            try { closed = await Installation.CloseRunning(this, [Path.GetFullPath(root)], "安装"); }
            finally { next.Loading = false; next.Enabled = back.Enabled = cancel.Enabled = true; }
            if (!closed) return;
            Show(2); _ = RunInstall(); return;
        }
        if (index == 3) {
            if (failure == null && launch.Checked) SetupEngine.Launch(Path.GetFullPath(location.Text.Trim()));
            Close();
        }
    }

    async Task RunInstall()
    {
        installing = true;
        string root = location.Text.Trim();
        var progress = new Progress<(double Value, string Text)>(p => {
            bar.Value = (float)Math.Clamp(p.Value, 0, 1);
            if (!log.Text.EndsWith(p.Text + Environment.NewLine)) log.AppendText(p.Text + Environment.NewLine);
        });
        void Report(string text) => ((IProgress<(double, string)>)progress).Report((bar.Value, text));
        try {
            bool upgrade = Installation.IsOwnedDirectory(root) || Installation.IsPortableDirectory(root);
            if (updateRoot != null) {
                Report("正在等待 Region Launcher 退出…");
                bool exited = await Task.Run(() => SetupEngine.WaitForExit(root, updatePid, TimeSpan.FromSeconds(90)));
                if (!exited && !await Installation.CloseRunning(this, [root], "更新")) throw new IOException("已取消：Region Launcher 仍在运行。");
            } else if (!await Task.Run(() => Installation.WaitUntilClosed([root], TimeSpan.FromSeconds(10))))
                throw new IOException("Region Launcher 仍在运行。请在托盘图标上右键选择“退出（结束全部运行）”后重试。");
            var options = new SetupEngine.Options(root, upgrade && updateRoot != null ? File.Exists(Installation.DesktopShortcut) : desktop.Checked, upgrade);
            serviceWarning = await Task.Run(() => SetupEngine.Install(options, progress, CancellationToken.None));
            installed = true;
            Report("完成。");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException or System.Security.SecurityException) {
            failure = ex.Message;
            Report("失败：" + ex.Message);
        } finally { installing = false; }
        if (updateRoot != null && failure == null) {
            // Silent update: reopen the launcher and close.
            SetupEngine.Launch(Path.GetFullPath(root));
            await Task.Delay(1200);
            Close();
            return;
        }
        Show(3);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing) { e.Cancel = true; return; }
        if (index < 2 && !installed && e.CloseReason == CloseReason.UserClosing &&
            MessageDialog.Show(this, "确定退出安装？", "Region Launcher 安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
