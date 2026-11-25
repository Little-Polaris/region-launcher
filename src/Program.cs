using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (ListenerRecovery.TryRun(args)) return;
        if (args.Length == 2 && args[0] == "--network-probe") {
            try {
                var results = ProxyHealth.ProbeAsync(null, Console.WriteLine).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(results));
                Environment.ExitCode = results.Any(r => r.Ok) ? 0 : 1;
            } catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        HomeTheme.Initialize();
        if (Uninstaller.TryRun(args)) return;
        if (args.Length == 2 && args[0] == "--render-preview")
        {
            using var preview = new Launcher(); preview.StartPosition = FormStartPosition.Manual;
            preview.Location = new(-32000, -32000); preview.ShowInTaskbar = false; preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); preview.Close(); return;
        }
        SingleInstance.ExitRequested = form => ((Launcher)form).RequestExit();
        SingleInstance.Run(() => new Launcher(), form => ((Launcher)form).RestoreWindow());
    }
}

sealed class Profile
{
    public string Application { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 7890;
    public bool AutoDst { get; set; } = true;
    public int GeoId { get; set; } = 244;
    public string Locale { get; set; } = "en-US";
    public string TimeZone { get; set; } = "Pacific Standard Time";
}

sealed class Launcher : Form
{
    readonly FlowLayoutPanel tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, AllowDrop = true, Padding = new(20, 8, 20, 20) };
    readonly RunningApplications running = new();
    readonly StatusLabel homeStatus = new() { Dock = DockStyle.Fill, AutoSize = true, AutoEllipsis = true, ForeColor = HomeTheme.Muted, Margin = new(0, 4, 0, 0) };
    readonly AntdUI.Button footer = HomeTheme.Link("查看运行日志", "FileTextOutlined");
    string? launchingId;
    readonly ContextMenuStrip actions = new();
    readonly ToolStripMenuItem configureProxy = new("独立代理配置…");
    readonly ToolStripMenuItem switchProxy = new("切换节点…");
    readonly ToolStripMenuItem checkProxy = new("网络检测");
    readonly ToolStripMenuItem linkBrowser = new("授权浏览器…");
    readonly ToolStripMenuItem probe = new("添加环境测试程序");
    bool preparing;
    long preparationId;
    bool exiting, allowClose;
    readonly CancellationTokenSource shutdown = new();
    readonly HashSet<Task> operations = [];
    readonly Icon windowIcon = AppIcon.Load(new Size(32, 32));
    readonly Icon trayIcon = AppIcon.Load(SystemInformation.SmallIconSize);
    readonly NotifyIcon tray = new() { Text = "Region Launcher" };
    readonly ContextMenuStrip trayMenu = new();
    readonly string data;
    readonly ApplicationLibrary library;
    ApplicationList? applicationList;
    readonly DedicatedProxy dedicatedProxy;
    readonly ProxyRecovery proxyRecovery;
    readonly DiagnosticLog? diagnostics;
    Process? session;
    string? currentJob;
    // Only the real startup shows the first-run wizard and checks for updates; tests construct the window with their own data folder.
    public Launcher() : this(Path.Combine(AppContext.BaseDirectory, "data")) { interactiveStartup = true; }
    readonly bool interactiveStartup;
    UpdateSettings updateSettings = new();
    readonly ToolStripMenuItem checkUpdate = new("检查更新…");
    readonly ToolStripMenuItem autoUpdate = new("启动时自动检查更新") { CheckOnClick = true };
    readonly ToolStripMenuItem wizard = new("使用向导…");
    bool checkingUpdate;
    internal Launcher(string dataDirectory)
    {
        Icon = windowIcon; tray.Icon = trayIcon;
        data = dataDirectory; library = new(Path.Combine(data, "applications.json"));
        string? logError = null;
        try { diagnostics = new(Path.Combine(data, "logs")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logError = ex.Message; }
        dedicatedProxy = new(Append, values => diagnostics?.AddSecrets(values));
        proxyRecovery = new(dedicatedProxy.EnsureStarted, dedicatedProxy.ReconnectAsync,
            ct => ProxyHealth.ProbeAsync(dedicatedProxy.Port, s => Append("[指定代理] " + s), cancellationToken: ct), Append);
        Text = "Region Launcher"; ClientSize = new(820, 560); MinimumSize = new(410, 390);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; BackColor = HomeTheme.Background;
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(24, 16, 24, 8), ColumnCount = 2, RowCount = 3 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int row = 0; row < 3; row++) header.RowStyles.Add(new(SizeType.AutoSize));
        var title = new Label { Text = "Region Launcher", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft,
            Font = new(Font.FontFamily, 17, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = Padding.Empty };
        var quick = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        var check = HomeTheme.Action("网络检测", AntdUI.TTypeMini.Primary, "SafetyCertificateOutlined"); check.Click += (_, _) => checkProxy.PerformClick();
        var node = HomeTheme.Action("切换节点", AntdUI.TTypeMini.Default, "SwapOutlined"); node.Click += (_, _) => switchProxy.PerformClick();
        checkProxy.EnabledChanged += (_, _) => { check.Enabled = checkProxy.Enabled; check.Loading = !checkProxy.Enabled && preparing; };
        switchProxy.EnabledChanged += (_, _) => node.Enabled = switchProxy.Enabled;
        var more = HomeTheme.Action("更多", AntdUI.TTypeMini.Default, "SettingOutlined"); more.AccessibleName = "更多设置与工具";
        more.Click += (_, _) => actions.Show(more, new Point(more.Width, more.Height + 2), ToolStripDropDownDirection.BelowLeft);
        quick.Controls.AddRange([check, node, more]);
        header.Controls.Add(title, 0, 0); header.Controls.Add(quick, 1, 0);
        header.Controls.Add(homeStatus, 0, 1); header.SetColumnSpan(homeStatus, 2);
        void FitHeader()
        {
            // A minimized window reports a zero-width client; keep the last layout instead of wrapping.
            if (WindowState == FormWindowState.Minimized || header.ClientSize.Width == 0) return;
            bool narrow = header.ClientSize.Width - header.Padding.Horizontal < title.PreferredWidth + quick.PreferredSize.Width + LogicalToDeviceUnits(12);
            if ((header.GetRow(quick) == 2) == narrow) return;
            header.SuspendLayout();
            header.SetCellPosition(quick, narrow ? new TableLayoutPanelCellPosition(0, 2) : new TableLayoutPanelCellPosition(1, 0));
            header.SetColumnSpan(quick, narrow ? 2 : 1);
            header.SetColumnSpan(title, narrow ? 2 : 1);
            header.ResumeLayout(true);
        }
        header.SizeChanged += (_, _) => FitHeader();
        quick.SizeChanged += (_, _) => FitHeader();
        // Re-flow after restoring from minimized; the tiles otherwise keep the scroll range of the zero-size layout.
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized) { FitHeader(); tiles.PerformLayout(); } };
        title.FontChanged += (_, _) => FitHeader();
        footer.AccessibleName = "查看运行日志"; footer.Dock = DockStyle.Left; footer.Margin = Padding.Empty;
        footer.Click += (_, _) => ShowRunLog();
        var version = new Label { Text = "v" + Application.ProductVersion.Split('+')[0], AutoSize = false, Width = LogicalToDeviceUnits(90), Dock = DockStyle.Right,
            ForeColor = HomeTheme.Subtle, TextAlign = ContentAlignment.MiddleRight, Padding = new(0, 0, 4, 0) };
        var footerBar = new FooterStrip { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(42), Padding = new(16, 4, 20, 4) };
        footerBar.Controls.Add(footer); footerBar.Controls.Add(version);
        Controls.Add(tiles); Controls.Add(running); Controls.Add(footerBar); Controls.Add(header);
        var openLogs = new ToolStripMenuItem("打开日志", null, (_, _) => OpenLogs());
        var showLog = new ToolStripMenuItem("查看运行日志", null, (_, _) => ShowRunLog());
        actions.Items.AddRange([configureProxy, switchProxy, checkProxy, linkBrowser, new ToolStripSeparator(), probe, showLog, openLogs,
            new ToolStripSeparator(), wizard, checkUpdate, autoUpdate]);
        foreach (var (item, icon) in new (ToolStripMenuItem, string)[] { (configureProxy, "CloudServerOutlined"), (switchProxy, "SwapOutlined"),
            (checkProxy, "SafetyCertificateOutlined"), (linkBrowser, "GlobalOutlined"), (probe, "ExperimentOutlined"), (showLog, "FileTextOutlined"), (openLogs, "FolderOpenOutlined"),
            (wizard, "CompassOutlined"), (checkUpdate, "CloudDownloadOutlined") })
            item.Image = HomeTheme.Icon(icon, LogicalToDeviceUnits(16), HomeTheme.Muted);
        HomeTheme.Apply(actions); HomeTheme.Apply(trayMenu);
        tiles.DragEnter += (_, e) => e.Effect = tiles.Enabled && DroppedExecutable(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        tiles.DragDrop += (_, e) => {
            if (!tiles.Enabled || DroppedExecutable(e) is not string file) return;
            try { EditSoftware(ApplicationLibrary.ImportTarget(file, DefaultEnvironment), adding: true); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) {
                MessageDialog.Show(this, ex.Message, "无法导入程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        checkProxy.Click += async (_, _) => await TrackOperation(async () => {
            if (!BeginPreparation(out long operation)) return;
            try { var result = await CheckProxy(); NotifyCheckResult(result.Recovered, result.Detail, null); }
            catch (Exception ex) { Append("代理检测失败：" + ex.Message); NotifyCheckResult(false, "", ex.Message); }
            finally { EndPreparation(operation); }
        });
        configureProxy.Click += (_, _) => {
            using var dialog = new ProxySettingsDialog(dedicatedProxy);
            if (dialog.ShowDialog(this) == DialogResult.OK) {
                dedicatedProxy.Dispose(); RefreshProxy();
                Append(dedicatedProxy.CleanupComplete ? "独立代理配置已保存；旧内核及端口已清理，下次检测或启动使用新节点。" : "独立代理配置已保存，但旧内核或端口尚未释放；未启动新节点，详情见退出检查日志。");
            }
        };
        switchProxy.Click += async (_, _) => await TrackOperation(SwitchProxyAsync);
        probe.Click += (_, _) => {
            if (preparing || applicationList == null) return;
            var entry = applicationList.Applications.FirstOrDefault(a => a.Application == "RegionProbe.exe" && a.Arguments == "");
            if (entry == null) SaveSoftware(new LaunchApplication { Name = "环境测试程序", Application = "RegionProbe.exe", Environment = DefaultEnvironment });
        };
        linkBrowser.Click += (_, _) => {
            try { using var dialog = new LinkBrowserDialog(data, applicationList); if (dialog.ShowDialog(this) == DialogResult.OK) Append("授权链接浏览器已保存，下次启动程序生效。"); }
            catch (Exception ex) { Append("授权浏览器配置读取失败：" + ex.Message); }
        };
        updateSettings = UpdateSettings.Load(data);
        autoUpdate.Checked = updateSettings.AutoCheck;
        autoUpdate.CheckedChanged += (_, _) => { updateSettings.AutoCheck = autoUpdate.Checked; updateSettings.Save(data); };
        checkUpdate.Click += async (_, _) => await TrackOperation(() => CheckForUpdates(manual: true));
        wizard.Click += (_, _) => ShowWizard();
        trayMenu.Items.Add("打开启动器", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出（结束全部运行）", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreWindow(); };
        tray.BalloonTipClicked += (_, _) => RestoreWindow();
        Shown += (_, _) => tray.Visible = true;
        FormClosing += (_, e) =>
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ShowInTaskbar = false; return; }
            shutdown.Cancel();
            StopSession();
        };
        dedicatedProxy.UnexpectedExit += () => Append("独立代理内核已退出；已启动的软件和网络隔离保留。点击“网络检测”或启动新程序时将重新连接代理。");
        Directory.CreateDirectory(data);
        Profile legacy = new();
        try { legacy = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(data, "profile.json"))) ?? new(); }
        catch (FileNotFoundException) {} catch (Exception ex) { Append("配置读取失败，使用默认值：" + ex.Message); }
        try {
            var legacyEnvironment = new LaunchEnvironment { GeoId = legacy.GeoId, Locale = legacy.Locale, TimeZone = legacy.TimeZone, AutoDst = legacy.AutoDst };
            try { legacyEnvironment.Validate(); }
            catch (InvalidDataException) { legacyEnvironment = new(); }
            applicationList = library.Load(legacy.Application, legacy.Arguments, legacyEnvironment);
        }
        catch (Exception ex) { Append($"软件列表读取失败，原文件已保留：{ex.Message} 请检查 {Path.Combine(data, "applications.json")} 后重启。"); }
        RefreshSoftware();
        RefreshProxy();
        Append($"启动器版本 {Application.ProductVersion}，PID={Environment.ProcessId}；日志目录：{Path.Combine(data, "logs")}");
        if (logError != null) Shown += (_, _) => MessageDialog.Show(this, logError, "日志文件不可写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    protected override void OnLoad(EventArgs e)
    {
        running.FitWidth();
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
        if (Width > work.Width || Height > work.Height) {
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
        }
        base.OnLoad(e);
    }

    internal static string? DroppedExecutable(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
        Path.IsPathFullyQualified(files[0]) && (ApplicationLibrary.SupportsTarget(files[0]) || ApplicationLibrary.IsShortcut(files[0])) ? files[0] : null;

    void ShowRunLog()
    {
        using var dialog = CreateRunLog();
        dialog.ShowDialog(this);
    }
    internal Form CreateRunLog()
    {
        var dialog = new Form { Icon = windowIcon };
        HomeTheme.Dialog(dialog, "运行日志", new(820, 500), new(420, 300));
        var log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(250, 251, 252), ForeColor = HomeTheme.Text, Font = new("Consolas", 9.5f) };
        var frame = new LogFrame { Dock = DockStyle.Fill };
        frame.Controls.Add(log);
        var close = HomeTheme.Action("关闭", AntdUI.TTypeMini.Primary); close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        // Follow the log live: new lines are appended every half second. While the view is at the bottom it stays there;
        // after scrolling up to read, the position is kept until the user scrolls back down.
        string? file = null; long position = 0;
        void Follow()
        {
            if (diagnostics == null) { if (log.TextLength == 0) log.Text = "当前运行日志不可用。"; return; }
            (string Text, string? File, long Position) next;
            try { next = diagnostics.ReadFrom(file, position); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (log.TextLength == 0) log.Text = "读取日志失败：" + ex.Message; return; }
            bool rotated = next.File != file;
            file = next.File; position = next.Position;
            if (next.Text.Length == 0 && !rotated) return;
            string text = ReadableLog(next.Text);
            bool atBottom = rotated || LogAtBottom(log);
            int firstLine = (int)SendMessageW(log.Handle, 0x00CE, 0, 0); // EM_GETFIRSTVISIBLELINE
            if (rotated) log.Text = text; else log.AppendText(text);
            // Keep memory bounded on long sessions.
            if (log.TextLength > 1_000_000) { log.Select(0, log.GetFirstCharIndexFromLine(log.GetLineFromCharIndex(log.TextLength - 600_000))); log.SelectedText = ""; atBottom = true; }
            if (atBottom) { log.Select(log.TextLength, 0); SendMessageW(log.Handle, 0x0115, 7, 0); SendMessageW(log.Handle, 0x0114, 6, 0); } // SB_BOTTOM, then back to the left edge
            else SendMessageW(log.Handle, 0x00B6, 0, firstLine - (int)SendMessageW(log.Handle, 0x00CE, 0, 0)); // EM_LINESCROLL back
        }
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => Follow();
        dialog.Shown += (_, _) => { log.Select(log.TextLength, 0); SendMessageW(log.Handle, 0x0115, 7, 0); timer.Start(); };
        dialog.FormClosed += (_, _) => timer.Dispose();
        var folder = HomeTheme.Action("打开日志文件夹", AntdUI.TTypeMini.Default, "FolderOpenOutlined"); folder.Click += (_, _) => OpenLogs();
        var buttons = HomeTheme.Footer(close, folder); buttons.Dock = DockStyle.Bottom;
        dialog.Controls.Add(frame); dialog.Controls.Add(buttons); Follow();
        return dialog;
    }

    // Some programs log text they decoded wrongly themselves (for example UTF-16 tool output read as UTF-8, which
    // leaves JSON-escaped NULs between the letters). Drop those escapes in the viewer; the file keeps the original.
    static string ReadableLog(string text) => text.Replace("\\u0000", "").Replace("\0", "");

    static bool LogAtBottom(TextBox box)
    {
        var info = new ScrollInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 }; // SIF_ALL
        if (!GetScrollInfo(box.Handle, 1, ref info)) return true; // no vertical scroll bar yet
        return info.Page == 0 || info.Pos + (int)info.Page >= info.Max;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ScrollInfo { public uint Size, Mask; public int Min, Max; public uint Page; public int Pos, TrackPos; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageW(IntPtr window, int message, int wParam, int lParam);
    void OpenLogs()
    {
        try {
            var folder = Path.Combine(data, "logs"); Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) {
            Append("无法打开日志目录：" + ex.Message);
            MessageDialog.Show(this, ex.Message, "无法打开日志目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void RefreshProxy()
    {
        Append(dedicatedProxy.Configured ? "已加载独立代理配置；公网走所选出口，局域网直连，DNS 使用专用解析器。" : "请在右上角菜单选择“独立代理配置”填写节点或导入配置。");
        UpdateActions();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); actions.Dispose(); windowIcon.Dispose(); trayIcon.Dispose(); shutdown.Dispose(); dedicatedProxy?.Dispose(); diagnostics?.Dispose(); }
        base.Dispose(disposing);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!interactiveStartup) return;
        BeginInvoke(async () => {
            if (!SetupWizard.Completed(data)) {
                // Existing users who already set everything up skip the guide.
                if (dedicatedProxy.Configured && applicationList?.Applications.Count > 0) SetupWizard.MarkCompleted(data);
                else ShowWizard();
            }
            if (updateSettings.AutoCheck && (updateSettings.LastCheck is not { } last || DateTime.Now - last > TimeSpan.FromHours(20))) {
                await Task.Delay(TimeSpan.FromSeconds(3));
                await TrackOperation(() => CheckForUpdates(manual: false));
            }
        });
    }

    void ShowWizard()
    {
        if (exiting) return;
        var host = new WizardHost(
            Installation.CurrentInstallation(AppContext.BaseDirectory) ?? PortableRoot(),
            () => dedicatedProxy.Configured, () => configureProxy.PerformClick(),
            () => applicationList?.Applications.Count ?? 0, () => EditSoftware(null),
            () => updateSettings.AutoCheck, value => autoUpdate.Checked = value);
        using var dialog = new SetupWizard(host);
        dialog.ShowDialog(this);
        SetupWizard.MarkCompleted(data);
    }

    // A portable copy unpacked from the ZIP still has the service installer next to the app folder.
    static string? PortableRoot()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return File.Exists(Path.Combine(root, "install", "request_install.ps1")) ? root : null;
    }

    async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdate || exiting || Application.OpenForms.OfType<ProxySettingsDialog>().Any()) return;
        checkingUpdate = true; UpdateActions();
        HttpClient? http = null;
        try {
            UpdateInfo? update;
            try {
                int? port = null;
                if (dedicatedProxy.Configured) {
                    Append("检查更新：使用启动器的独立代理。");
                    await dedicatedProxy.EnsureStarted(shutdown.Token);
                    port = dedicatedProxy.Port;
                } else Append("检查更新：未配置独立代理，使用系统网络。");
                shutdown.Token.ThrowIfCancellationRequested();
                http = Updater.CreateClient(port);
                update = await Updater.CheckAsync(http, shutdown.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or
                KeyNotFoundException or InvalidOperationException) {
                Append("检查更新失败：" + ex.Message);
                if (manual && !exiting) MessageDialog.Show(this, "无法连接 GitHub 检查更新：" + ex.Message + "\n\n可以稍后重试，或打开发布页查看。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            updateSettings.LastCheck = DateTime.Now; updateSettings.Save(data);
            if (update == null) {
                Append($"检查更新：当前 {Updater.Current} 已是最新版本。");
                if (manual) MessageDialog.Show(this, $"当前版本 {Updater.Current} 已是最新版本。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Append($"发现新版本 {update.Version}。");
            if (exiting) return;
            if (!manual) RestoreWindow();
            string notes = update.Notes.Length > 600 ? update.Notes[..600] + "…" : update.Notes;
            string? root = Installation.CurrentInstallation(AppContext.BaseDirectory);
            if (root == null) {
                if (MessageDialog.Show(this, $"发现新版本 {update.Version}（当前 {Updater.Current}）。\n\n{notes}\n\n当前是便携版，无法自动更新。是否打开发布页下载？",
                    "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Installation.OpenUrl(update.Page);
                return;
            }
            if (MessageDialog.Show(this, $"发现新版本 {update.Version}（当前 {Updater.Current}）。\n\n{notes}\n\n现在更新吗？更新时启动器会退出并结束全部运行中的程序，节点、订阅和程序列表会保留。",
                "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            using var download = new UpdateDownloadDialog(update, http);
            var result = download.ShowDialog(this);
            if (result == DialogResult.Abort) {
                Append("下载更新失败：" + download.Error?.Message);
                MessageDialog.Show(this, "下载更新失败：" + download.Error?.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (result != DialogResult.OK || download.Result == null) return;
            Append($"已下载并校验 {update.SetupName}，退出后开始安装。");
            Updater.StartInstaller(download.Result, root);
            _ = ExitAsync();
        } finally { http?.Dispose(); checkingUpdate = false; UpdateActions(); }
    }

    /// <summary>Exit requested from outside (installer, uninstaller): close open dialogs, then the normal tray exit.</summary>
    internal void RequestExit()
    {
        foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f != this && f.Modal).Reverse().ToList()) form.Close();
        BeginInvoke(async () => await ExitAsync());
    }

    internal void RestoreWindow() { Show(); ShowInTaskbar = true; if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
    async Task TrackOperation(Func<Task> action) {
        if (exiting) return;
        var task = action(); operations.Add(task);
        try { await task; } finally { operations.Remove(task); }
    }
    async Task ExitAsync() {
        if (exiting) return;
        exiting = true; shutdown.Cancel(); UpdateActions(); trayMenu.Items[2].Enabled = false;
        Append("正在退出：停止检测，结束本启动器的全部运行进程，然后关闭独立代理。");
        StopSession();
        var pending = Task.WhenAll(operations.ToArray());
        // Retry the stop signal while a newly created session is still creating its event.
        while (!pending.IsCompleted || session is { HasExited: false }) { SignalSessionStop(); await Task.Delay(100); }
        try { await pending; } catch (Exception ex) { Append("结束后台操作：" + ex.Message); }
        try {
            bool released = await dedicatedProxy.StopAsync();
            Append(released ? "退出清理完成：本启动器没有待清理的代理或端口。" : "退出清理未完全通过，详情已写入日志；未结束其他程序。");
        } catch (Exception ex) { Append("退出清理未完成：" + ex.Message); }
        allowClose = true; tray.Visible = false; Close();
    }
    void UpdateActions()
    {
        if (exiting) { foreach (Control control in Controls) control.Enabled = false; return; }
        tiles.Enabled = probe.Enabled = !preparing && applicationList != null;
        configureProxy.Enabled = session == null && !preparing && !checkingUpdate;
        checkUpdate.Enabled = !checkingUpdate;
        switchProxy.Enabled = !preparing && dedicatedProxy.Configured;
        linkBrowser.Enabled = session == null && !preparing;
        checkProxy.Enabled = !preparing;
        foreach (var tile in tiles.Controls.OfType<ApplicationTile>())
            tile.SetBusy(preparing && launchingId != null && launchingId == (tile.Tag as LaunchApplication)?.Id);
        homeStatus.Dot = applicationList == null ? HomeTheme.Danger : preparing ? HomeTheme.Accent :
            !dedicatedProxy.Configured ? HomeTheme.Warning : HomeTheme.Success;
        homeStatus.Text = applicationList == null ? "软件列表读取失败 · 请查看运行日志" :
            $"{applicationList.Applications.Count} 个程序  ·  {(dedicatedProxy.Configured ? "代理已配置" : "代理未配置")}  ·  {(preparing ? "正在准备…" : session != null ? "环境运行中" : "就绪")}";
    }

    async Task SwitchProxyAsync()
    {
        if (!BeginPreparation(out long operation)) return;
        try
        {
            var active = await dedicatedProxy.CurrentNodeAsync(shutdown.Token);
            using var picker = new ProxySwitchDialog(dedicatedProxy.ConfigPath, active,
                (nodes, progress, token) => NodeLatency.MeasureAsync(dedicatedProxy.EnginePath,
                    Path.GetDirectoryName(dedicatedProxy.ConfigPath)!, nodes, progress, token));
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            await dedicatedProxy.SwitchNodeAsync(picker.SelectedNode, shutdown.Token);
            Append("已切换本次运行的代理出口：" + picker.SelectedName + "。");
        }
        catch (Exception ex)
        {
            if (!shutdown.IsCancellationRequested)
            {
                Append("切换节点失败：" + ex.Message);
                MessageDialog.Show(this, ex.Message, "切换节点失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally { EndPreparation(operation); }
    }
    bool BeginPreparation(out long operation, string? applicationId = null)
    {
        operation = 0;
        if (preparing || exiting) return false;
        operation = ++preparationId; preparing = true; launchingId = applicationId; UpdateActions(); return true;
    }
    void EndPreparation(long operation)
    {
        if (operation == preparationId) { preparing = false; launchingId = null; }
        UpdateActions();
    }
    LaunchEnvironment DefaultEnvironment =>
        applicationList?.Applications.Find(app => app.Id == applicationList.SelectedId)?.Environment ?? new();

    void RefreshSoftware()
    {
        tiles.SuspendLayout();
        try {
            while (tiles.Controls.Count != 0) tiles.Controls[0].Dispose();
            if (applicationList != null) {
                foreach (var entry in applicationList.Applications) {
                    var tile = new ApplicationTile(entry);
                    tile.Click += async (_, _) => await TrackOperation(() => StartSession(entry));
                    var menu = new ContextMenuStrip();
                    Image? MenuIcon(string name, Color? color = null) => HomeTheme.Icon(name, LogicalToDeviceUnits(16), color ?? HomeTheme.Muted);
                    menu.Items.Add("启动", MenuIcon("CaretRightOutlined", HomeTheme.Accent), async (_, _) => await TrackOperation(() => StartSession(entry)));
                    menu.Items.Add("编辑程序与地区…", MenuIcon("EditOutlined"), (_, _) => EditSoftware(entry));
                    menu.Items.Add("打开所在文件夹", MenuIcon("FolderOpenOutlined"), (_, _) => RevealApplication(entry));
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("删除软件…", MenuIcon("DeleteOutlined", HomeTheme.Danger), (_, _) => RemoveSoftware(entry));
                    HomeTheme.Apply(menu);
                    tile.ContextMenuStrip = menu;
                    tiles.Controls.Add(tile);
                }
            }
            var add = new ApplicationTile(null);
            add.Click += (_, _) => EditSoftware(null);
            tiles.Controls.Add(add);
        } finally { tiles.ResumeLayout(); }
        UpdateActions();
    }
    void EditSoftware(LaunchApplication? entry, bool adding = false)
    {
        if (preparing || applicationList == null) return;
        using var dialog = new ApplicationEditor(entry, DefaultEnvironment, adding);
        if (dialog.ShowDialog(this) == DialogResult.OK) SaveSoftware(dialog.Entry!);
    }
    void SaveSoftware(LaunchApplication entry)
    {
        if (applicationList == null) return;
        var entries = applicationList.Applications.ToList();
        int index = entries.FindIndex(a => a.Id == entry.Id);
        if (index < 0) entries.Add(entry); else entries[index] = entry;
        SaveSoftwareList(applicationList with { SelectedId = entry.Id, Applications = entries });
    }
    void SaveSoftwareList(ApplicationList updated)
    {
        try { library.Save(updated); applicationList = updated; RefreshSoftware(); }
        catch (Exception ex) { MessageDialog.Show(this, ex.Message, "无法保存软件列表", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void RevealApplication(LaunchApplication entry)
    {
        try {
            string path = ApplicationLibrary.ResolvePath(entry.Application);
            if (!File.Exists(path)) throw new FileNotFoundException("程序不存在，请先编辑程序路径。");
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        } catch (Exception ex) {
            Append("无法打开所在文件夹：" + ex.Message);
            MessageDialog.Show(this, ex.Message, "无法打开所在文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void RemoveSoftware(LaunchApplication entry)
    {
        if (preparing || applicationList == null) return;
        if (MessageDialog.Show(this, $"从列表中删除“{entry.Name}”？这不会卸载软件或删除其文件。", "删除软件", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var entries = applicationList.Applications.Where(a => a.Id != entry.Id).ToList();
        SaveSoftwareList(applicationList with { SelectedId = entries.FirstOrDefault()?.Id, Applications = entries });
    }
    void Append(string text) => diagnostics?.Write(text);
    async Task<(bool Recovered, string Detail)> CheckProxy()
    {
        if (!dedicatedProxy.Configured) throw new IOException("请先点击“独立代理配置”填写或导入节点。");
        bool failover = dedicatedProxy.UsesFailover;
        Append(failover
            ? "开始检测：Mihomo 负责节点探活与切换。启动器检查本地代理和环境内实际联网。"
            : "开始网络检测：固定出口规则 → 代理实际联网及必要时重连 → 环境内实际联网。");
        Append($"检测通道：启动器独立代理 127.0.0.1:{dedicatedProxy.Port}，专用 DNS {dedicatedProxy.DnsAddress}:53。");
        bool exitedCore = dedicatedProxy.HasExitedCore;
        var throughProxy = await proxyRecovery.CheckAsync(shutdown.Token, reconnectOnProbeFailure: !failover);
        shutdown.Token.ThrowIfCancellationRequested();
        ProxyHealth.RequireInternet(throughProxy);
        var (inside, proxyRecheck) = await EnvironmentCheck.RunWithRecheckAsync(
            () => EnvironmentCheck.RunAsync(AppContext.BaseDirectory, data, dedicatedProxy.Port, dedicatedProxy.DnsAddress, session == null ? null : currentJob, Append, diagnostics, shutdown.Token),
            () => ProxyHealth.ProbeAsync(dedicatedProxy.Port, s => Append("[故障复核] " + s), cancellationToken: shutdown.Token),
            failover, Append, shutdown.Token);
        throughProxy = proxyRecheck ?? throughProxy;
        try { ProxyHealth.RequireMatchingExit(throughProxy, inside); }
        catch (IOException) when (failover && !shutdown.IsCancellationRequested)
        {
            Append("[出口复核] 检测期间可能发生节点切换，重新核对独立代理出口。");
            throughProxy = await ProxyHealth.ProbeAsync(dedicatedProxy.Port, Append, cancellationToken: shutdown.Token);
            ProxyHealth.RequireMatchingExit(throughProxy, inside);
        }
        Append($"实际联网及环境出口校验通过：代理 {throughProxy.Count(r => r.Ok)}/{throughProxy.Length}，环境 {inside.Count(r => r.Ok)}/{inside.Length} 个站点成功。同一站点两层出口一致；失败站点见上方日志。");
        Append("此结果验证检测程序当前的 TCP/HTTPS 路径，不代表所有应用、UDP 或所有网站始终可用；不能用国内网站加载快慢判断是否直连。");
        string detail = $"实际联网及环境出口校验通过。\n代理检测：{throughProxy.Count(r => r.Ok)}/{throughProxy.Length} 个站点成功。\n环境检测：{inside.Count(r => r.Ok)}/{inside.Length} 个站点成功。";
        if (throughProxy.Any(r => !r.Ok) || inside.Any(r => !r.Ok)) detail += "\n部分检测站点未通过，请查看日志了解详情。";
        return (exitedCore || proxyRecovery.Recovered || proxyRecheck != null, detail);
    }
    void NotifyCheckResult(bool recovered, string detail, string? error)
    {
        if (exiting || shutdown.IsCancellationRequested || IsDisposed) return;
        string title = error != null ? "网络检测：未能恢复连接" : recovered ? "网络检测：连接已恢复" : "网络检测：连接正常";
        string message = error != null ? "本次检测或修复未通过。\n\n" + error + "\n\n请从右上角菜单打开日志查看详情。" : detail;
        Append(title);
        RestoreWindow();
        MessageDialog.Show(this, message, title, MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }
    async Task StartSession(LaunchApplication selected)
    {
        if (!BeginPreparation(out long operation, selected.Id)) return;
        bool join = session != null;
        try
        {
            ApplicationLibrary.ValidateEntry(selected);
            string target = ApplicationLibrary.ResolvePath(selected.Application);
            var (app, appArgs, workingDirectory) = ApplicationLibrary.PrepareTarget(target, selected.Arguments, selected.WorkingDirectory);
            if (applicationList is { } list && list.SelectedId != selected.Id) {
                var updated = list with { SelectedId = selected.Id };
                try { library.Save(updated); applicationList = updated; }
                catch (Exception ex) { Append("无法保存上次启动的软件：" + ex.Message); }
            }
            Append($"请求启动软件：{selected.Name}；程序={target}；模式={(join ? "加入当前环境" : "新建环境")}。");
            foreach (string s in new[] { app, appArgs }) if (s.Contains('\n') || s.Contains('\r') || s.Contains('\0')) throw new ArgumentException("配置不能包含换行符或空字符。");
            if (!dedicatedProxy.Configured) throw new IOException("请先配置独立代理和专用 DNS，程序尚未启动。");
            string dnsAddress = dedicatedProxy.DnsAddress;
            await CheckProxy();
            shutdown.Token.ThrowIfCancellationRequested();
            if (join && session is not { HasExited: false }) throw new IOException("原运行环境已结束，请重新启动所选程序。");
            var settings = selected.Environment ?? throw new IOException("所选软件缺少地区设置，请重新编辑并保存。");
            settings.Validate();
            var region = WindowsSettings.Regions().Single(r => r.GeoId == settings.GeoId);
            var profile = new Profile { GeoId = settings.GeoId, Locale = settings.Locale, TimeZone = settings.TimeZone, Application = selected.Application, Arguments = selected.Arguments, ProxyHost = "127.0.0.1", ProxyPort = dedicatedProxy.Port, AutoDst = settings.AutoDst };
            Append($"软件环境：{selected.Name}；地区={region.Code}，语言={settings.Locale}，时区={settings.TimeZone}，自动冬夏时间={settings.AutoDst}。");
            File.WriteAllText(Path.Combine(data, "profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            string id = Guid.NewGuid().ToString("N"); if (!join) currentJob = @"Local\RegionLauncher." + id;
            string folder = Path.Combine(data, "sessions", id); Directory.CreateDirectory(folder); string config = Path.Combine(folder, "session.ini");
            string ini = $"[session]\r\nJob={currentJob}\r\nOwnerPid={Environment.ProcessId}\r\nApplication={app}\r\nArguments={appArgs}\r\nWorkingDirectory={workingDirectory}\r\nProxyHost={profile.ProxyHost}\r\nProxyPort={profile.ProxyPort}\r\nLocale={profile.Locale}\r\nRegion={region.Code}\r\nGeo={profile.GeoId}\r\nTimeZone={profile.TimeZone}\r\nAutoDst={(profile.AutoDst ? 1 : 0)}\r\n";
            File.WriteAllText(config, ini, Encoding.Unicode);
            File.AppendAllText(config, $"DnsAddress={dnsAddress}\r\n", Encoding.Unicode);
            var browserPlan = LinkBrowser.Prepare(data, currentJob!, settings, applicationList!);
            File.AppendAllText(config, browserPlan.Ini, Encoding.Unicode);
            Append(browserPlan.Executable == "" ? "未找到 Chrome / Edge；环境内打开网页将提示失败，请安装浏览器后重试。" : "授权链接使用环境内浏览器：" + Path.GetFileName(browserPlan.Executable) + (browserPlan.Profile == "" ? "；临时无痕资料。" : "；优先使用软件列表的独立资料。"));
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(join ? "--join" : "--run"); psi.ArgumentList.Add(config);
            var candidate = new Process { StartInfo = psi };
            try { candidate.Start(); } catch { candidate.Dispose(); throw; }
            string job = currentJob!;
            void OnStarted(int pid)
            {
                running.Track(selected.Name, pid, job);
                EndPreparation(operation);
            }
            if (join)
            {
                using (candidate) { await MonitorLaunch(candidate, OnStarted); Append($"加入程序已结束，退出码 {candidate.ExitCode}。"); }
                return;
            }
            session = candidate;
            Append("正在准备本次进程组；目标程序将在网络引擎就绪后运行。");
            await MonitorLaunch(session, OnStarted); diagnostics?.CaptureNetworkLog("会话 " + id); Append($"本次运行已结束，退出码 {session.ExitCode}。日志：{folder}"); session.Dispose(); session = null;
        }
        catch (Exception ex) {
            Append("未启动或运行失败：" + ex.Message);
            if (session is { HasExited: true }) { session.Dispose(); session = null; }
            NotifyLaunchFailure(selected.Name);
        }
        finally { EndPreparation(operation); }
    }
    async Task MonitorLaunch(Process process, Action<int>? onStarted = null) {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task logs = Task.WhenAll(
            ProcessLog.ReadAsync(process.StandardOutput.BaseStream, line => {
                if (line.StartsWith("STARTED PID=", StringComparison.Ordinal) && int.TryParse(line[12..], out int pid) && pid > 0) started.TrySetResult(pid);
                Append(line);
            }), ProcessLog.ReadAsync(process.StandardError.BaseStream, Append));
        await Task.WhenAny(started.Task, logs);
        if (started.Task.IsCompletedSuccessfully) onStarted?.Invoke(started.Task.Result);
        await process.WaitForExitAsync(); await logs;
        // Session exit codes also include the application's own exit code; only missing startup is a launch failure.
        if (!started.Task.IsCompletedSuccessfully) throw new IOException($"运行环境未能启动目标程序，退出码 {process.ExitCode}。具体原因见日志。");
    }
    void NotifyLaunchFailure(string applicationName) {
        if (exiting || shutdown.IsCancellationRequested || IsDisposed) return;
        string message = $"“{applicationName}”未能启动。请从启动器右上角菜单打开日志查看失败原因。";
        if (Visible && WindowState != FormWindowState.Minimized)
            MessageDialog.Show(this, message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else {
            tray.BalloonTipTitle = "Region Launcher · 启动失败";
            tray.BalloonTipText = message.Length > 240 ? message[..237] + "…" : message;
            tray.BalloonTipIcon = ToolTipIcon.Error;
            tray.ShowBalloonTip(8000);
        }
    }
    void StopSession()
    {
        if (currentJob == null) return;
        Append("请求结束运行环境：" + currentJob);
        SignalSessionStop();
        diagnostics?.CaptureNetworkLog("停止请求");
    }
    void SignalSessionStop() {
        if (currentJob == null) return;
        try { using var evt = EventWaitHandle.OpenExisting(currentJob + ".stop"); evt.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }
}
