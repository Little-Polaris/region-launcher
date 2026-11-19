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
        if (args.Length == 2 && args[0] == "--render-preview")
        {
            using var preview = new Launcher(); preview.StartPosition = FormStartPosition.Manual;
            preview.Location = new(-32000, -32000); preview.ShowInTaskbar = false; preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); preview.Close(); return;
        }
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
    readonly Label homeStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = HomeTheme.Muted };
    readonly Button footer = new() { Text = "查看运行日志", AccessibleName = "查看运行日志", Dock = DockStyle.Bottom, Height = 32,
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(234, 236, 241), ForeColor = HomeTheme.Muted,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new(22, 0, 0, 0), Cursor = Cursors.Hand };
    string? launchingId;
    readonly ContextMenuStrip actions = new();
    readonly ToolStripMenuItem configureProxy = new("独立代理配置…");
    readonly ToolStripMenuItem switchProxy = new("切换节点…");
    readonly ToolStripMenuItem checkProxy = new("检测并修复");
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
    public Launcher() : this(Path.Combine(AppContext.BaseDirectory, "data")) { }
    internal Launcher(string dataDirectory)
    {
        Icon = windowIcon; tray.Icon = trayIcon;
        data = dataDirectory; library = new(Path.Combine(data, "applications.json"));
        string? logError = null;
        try { diagnostics = new(Path.Combine(data, "logs")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logError = ex.Message; }
        dedicatedProxy = new(Append, values => diagnostics?.AddSecrets(values));
        proxyRecovery = new(dedicatedProxy.EnsureStarted, dedicatedProxy.ReconnectAsync,
            ct => ProxyHealth.ProbeAsync(dedicatedProxy.Port, s => Append("[指定代理] " + s), cancellationToken: ct), Append);
        Text = "Region Launcher"; ClientSize = new(820, 560); MinimumSize = new(480, 390);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = HomeTheme.Background;
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new(28, 16, 28, 4), ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.RowStyles.Add(new(SizeType.Percent, 60)); header.RowStyles.Add(new(SizeType.Percent, 40));
        var title = new Label { Text = "Region Launcher", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Font = new(Font.FontFamily, 16, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = Padding.Empty };
        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        var check = HomeTheme.Action("检测并修复"); check.Click += (_, _) => checkProxy.PerformClick();
        var node = HomeTheme.Action("切换节点"); node.Click += (_, _) => switchProxy.PerformClick();
        checkProxy.EnabledChanged += (_, _) => check.Enabled = checkProxy.Enabled;
        switchProxy.EnabledChanged += (_, _) => node.Enabled = switchProxy.Enabled;
        var more = HomeTheme.Action("更多 ▾"); more.AccessibleName = "更多设置与工具";
        more.Click += (_, _) => actions.Show(more, new Point(more.Width, more.Height + 2), ToolStripDropDownDirection.BelowLeft);
        quick.Controls.AddRange([check, node, more]);
        header.Controls.Add(title, 0, 0); header.Controls.Add(quick, 1, 0);
        header.Controls.Add(homeStatus, 0, 1); header.SetColumnSpan(homeStatus, 2);
        footer.FlatAppearance.BorderSize = 0;
        footer.Click += (_, _) => ShowRunLog();
        Controls.Add(tiles); Controls.Add(footer); Controls.Add(header);
        var openLogs = new ToolStripMenuItem("打开日志", null, (_, _) => OpenLogs());
        var showLog = new ToolStripMenuItem("查看运行日志", null, (_, _) => ShowRunLog());
        actions.Items.AddRange([configureProxy, switchProxy, checkProxy, linkBrowser, new ToolStripSeparator(), probe, showLog, openLogs]);
        tiles.DragEnter += (_, e) => e.Effect = tiles.Enabled && DroppedExecutable(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        tiles.DragDrop += (_, e) => {
            if (tiles.Enabled && DroppedExecutable(e) is string file)
                EditSoftware(new LaunchApplication { Name = Path.GetFileNameWithoutExtension(file), Application = file, Environment = DefaultEnvironment }, adding: true);
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
        dedicatedProxy.UnexpectedExit += () => Append("独立代理内核已退出；已启动的软件和网络隔离保留。点击“检测并修复”或启动新程序时将重新连接代理。");
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
        if (logError != null) Shown += (_, _) => MessageBox.Show(this, logError, "日志文件不可写", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    internal static string? DroppedExecutable(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
        Path.IsPathFullyQualified(files[0]) && files[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    void ShowRunLog()
    {
        using var dialog = CreateRunLog();
        dialog.ShowDialog(this);
    }
    internal Form CreateRunLog()
    {
        var dialog = new Form { Text = "运行日志", Font = Font, ClientSize = new(760, 450), MinimumSize = new(400, 280),
            StartPosition = FormStartPosition.CenterParent, Icon = windowIcon, ShowInTaskbar = false };
        var log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new(8) };
        var refresh = HomeTheme.Action("刷新");
        void LoadTail()
        {
            try { log.Text = diagnostics?.ReadTail() ?? "当前运行日志不可用。"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Text = "读取日志失败：" + ex.Message; }
            log.SelectionStart = log.TextLength; log.ScrollToCaret();
        }
        refresh.Click += (_, _) => LoadTail();
        var folder = HomeTheme.Action("打开日志文件夹"); folder.Click += (_, _) => OpenLogs();
        buttons.Controls.AddRange([refresh, folder]);
        dialog.Controls.Add(log); dialog.Controls.Add(buttons); LoadTail();
        return dialog;
    }
    void OpenLogs()
    {
        try {
            var folder = Path.Combine(data, "logs"); Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) {
            Append("无法打开日志目录：" + ex.Message);
            MessageBox.Show(this, ex.Message, "无法打开日志目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        configureProxy.Enabled = session == null && !preparing;
        switchProxy.Enabled = !preparing && dedicatedProxy.Configured;
        linkBrowser.Enabled = session == null && !preparing;
        checkProxy.Enabled = !preparing;
        foreach (var tile in tiles.Controls.OfType<ApplicationTile>())
            tile.SetBusy(preparing && launchingId != null && launchingId == (tile.Tag as LaunchApplication)?.Id);
        homeStatus.Text = applicationList == null ? "软件列表读取失败 · 请查看运行日志" :
            $"{applicationList.Applications.Count} 个程序  ·  {(dedicatedProxy.Configured ? "代理已配置" : "代理未配置")}  ·  {(preparing ? "正在准备…" : session != null ? "环境运行中" : "就绪")}";
    }

    async Task SwitchProxyAsync()
    {
        if (!BeginPreparation(out long operation)) return;
        try
        {
            using var picker = new ProxySwitchDialog(dedicatedProxy.ConfigPath);
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            await dedicatedProxy.SwitchNodeAsync(picker.SelectedNode, shutdown.Token);
            Append("已切换本次运行的代理出口：" + picker.SelectedName + "。");
        }
        catch (Exception ex)
        {
            if (!shutdown.IsCancellationRequested)
            {
                Append("切换节点失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "切换节点失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    menu.Items.Add("启动", null, async (_, _) => await TrackOperation(() => StartSession(entry)));
                    menu.Items.Add("编辑程序与地区…", null, (_, _) => EditSoftware(entry));
                    menu.Items.Add("打开所在文件夹", null, (_, _) => RevealApplication(entry));
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("删除软件…", null, (_, _) => RemoveSoftware(entry));
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
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存软件列表", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void RevealApplication(LaunchApplication entry)
    {
        try {
            string path = ApplicationLibrary.ResolvePath(entry.Application);
            if (!File.Exists(path)) throw new FileNotFoundException("程序不存在，请先编辑程序路径。");
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        } catch (Exception ex) {
            Append("无法打开所在文件夹：" + ex.Message);
            MessageBox.Show(this, ex.Message, "无法打开所在文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    void RemoveSoftware(LaunchApplication entry)
    {
        if (preparing || applicationList == null) return;
        if (MessageBox.Show(this, $"从列表中删除“{entry.Name}”？这不会卸载软件或删除其文件。", "删除软件", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
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
            : "开始检测并修复：固定出口规则 → 代理实际联网及必要时重连 → 环境内实际联网。");
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
        string title = error != null ? "检测并修复：未能恢复连接" : recovered ? "检测并修复：连接已恢复" : "检测并修复：连接正常";
        string message = error != null ? "本次检测或修复未通过。\n\n" + error + "\n\n请从右上角菜单打开日志查看详情。" : detail;
        Append(title);
        RestoreWindow();
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, error != null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }
    async Task StartSession(LaunchApplication selected)
    {
        if (!BeginPreparation(out long operation, selected.Id)) return;
        bool join = session != null;
        try
        {
            ApplicationLibrary.ValidateEntry(selected);
            string app = ApplicationLibrary.ResolvePath(selected.Application); if (!File.Exists(app)) throw new FileNotFoundException("程序不存在，请右键应用图标，编辑程序路径。");
            if (applicationList is { } list && list.SelectedId != selected.Id) {
                var updated = list with { SelectedId = selected.Id };
                try { library.Save(updated); applicationList = updated; }
                catch (Exception ex) { Append("无法保存上次启动的软件：" + ex.Message); }
            }
            string appArgs = selected.Arguments;
            Append($"请求启动软件：{selected.Name}；程序={app}；模式={(join ? "加入当前环境" : "新建环境")}。");
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
            string ini = $"[session]\r\nJob={currentJob}\r\nOwnerPid={Environment.ProcessId}\r\nApplication={app}\r\nArguments={appArgs}\r\nProxyHost={profile.ProxyHost}\r\nProxyPort={profile.ProxyPort}\r\nLocale={profile.Locale}\r\nRegion={region.Code}\r\nGeo={profile.GeoId}\r\nTimeZone={profile.TimeZone}\r\nAutoDst={(profile.AutoDst ? 1 : 0)}\r\n";
            File.WriteAllText(config, ini, Encoding.Unicode);
            File.AppendAllText(config, $"DnsAddress={dnsAddress}\r\n", Encoding.Unicode);
            var browserPlan = LinkBrowser.Prepare(data, currentJob!, settings, applicationList!);
            File.AppendAllText(config, browserPlan.Ini, Encoding.Unicode);
            Append(browserPlan.Executable == "" ? "未找到 Chrome / Edge；环境内打开网页将提示失败，请安装浏览器后重试。" : "授权链接使用环境内浏览器：" + Path.GetFileName(browserPlan.Executable) + (browserPlan.Profile == "" ? "；临时无痕资料。" : "；优先使用软件列表的独立资料。"));
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(join ? "--join" : "--run"); psi.ArgumentList.Add(config);
            var candidate = new Process { StartInfo = psi };
            try { candidate.Start(); } catch { candidate.Dispose(); throw; }
            if (join)
            {
                using (candidate) { await MonitorLaunch(candidate, () => EndPreparation(operation)); Append($"加入程序已结束，退出码 {candidate.ExitCode}。"); }
                return;
            }
            session = candidate;
            Append("正在准备本次进程组；目标程序将在网络引擎就绪后运行。");
            await MonitorLaunch(session, () => EndPreparation(operation)); diagnostics?.CaptureNetworkLog("会话 " + id); Append($"本次运行已结束，退出码 {session.ExitCode}。日志：{folder}"); session.Dispose(); session = null;
        }
        catch (Exception ex) {
            Append("未启动或运行失败：" + ex.Message);
            if (session is { HasExited: true }) { session.Dispose(); session = null; }
            NotifyLaunchFailure(selected.Name);
        }
        finally { EndPreparation(operation); }
    }
    async Task MonitorLaunch(Process process, Action? onStarted = null) {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task logs = Task.WhenAll(
            ProcessLog.ReadAsync(process.StandardOutput.BaseStream, line => {
                if (line.StartsWith("STARTED PID=", StringComparison.Ordinal) && uint.TryParse(line[12..], out uint pid) && pid > 0) started.TrySetResult();
                Append(line);
            }), ProcessLog.ReadAsync(process.StandardError.BaseStream, Append));
        await Task.WhenAny(started.Task, logs);
        if (started.Task.IsCompletedSuccessfully) onStarted?.Invoke();
        await process.WaitForExitAsync(); await logs;
        // Session exit codes also include the application's own exit code; only missing startup is a launch failure.
        if (!started.Task.IsCompletedSuccessfully) throw new IOException($"运行环境未能启动目标程序，退出码 {process.ExitCode}。具体原因见日志。");
    }
    void NotifyLaunchFailure(string applicationName) {
        if (exiting || shutdown.IsCancellationRequested || IsDisposed) return;
        string message = $"“{applicationName}”未能启动。请从启动器右上角菜单打开日志查看失败原因。";
        if (Visible && WindowState != FormWindowState.Minimized)
            MessageBox.Show(this, message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
