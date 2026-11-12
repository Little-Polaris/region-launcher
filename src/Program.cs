using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
    readonly ComboBox software = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button addSoftware = new() { Text = "添加…", AutoSize = true };
    readonly Button editSoftware = new() { Text = "编辑…", AutoSize = true };
    readonly Button removeSoftware = new() { Text = "删除", AutoSize = true };
    readonly TextBox executable = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox arguments = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox proxyHost = new() { Text = "127.0.0.1", Width = 180 };
    readonly NumericUpDown proxyPort = new() { Minimum = 1, Maximum = 65535, Value = 7890, Width = 90 };
    readonly SearchComboBox country = new() { Name = "Country", Dock = DockStyle.Fill };
    readonly SearchComboBox language = new() { Name = "Language", Dock = DockStyle.Fill };
    readonly SearchComboBox timeZone = new() { Name = "TimeZone", Dock = DockStyle.Fill };
    readonly Label timeNote = new() { AutoSize = true, Padding = new(0, 6, 0, 6) };
    readonly CheckBox dst = new() { Text = "自动切换夏令时与标准时间", Checked = true, AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "启动所选程序", AutoSize = true };
    readonly Button configureProxy = new() { Text = "独立代理配置…", AutoSize = true };
    readonly Button checkProxy = new() { Text = "检测并修复", AutoSize = true };
    readonly Button linkBrowser = new() { Text = "授权浏览器…", AutoSize = true };
    bool preparing;
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
    bool loadingSoftware;
    bool loadingSettings;
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
        proxyRecovery = new(dedicatedProxy.EnsureStarted, dedicatedProxy.RestartAsync,
            ct => ProxyHealth.ProbeAsync(dedicatedProxy.Port, s => Append("[指定代理] " + s), cancellationToken: ct), Append);
        Text = "Region Launcher · 环境验证版"; ClientSize = new(840, 760); MinimumSize = new(750, 710);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 3, RowCount = 14 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 100));
        for (int i = 0; i < 13; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        var title = new Label { Text = "独立运行环境", AutoSize = true, Font = new(Font, FontStyle.Bold), Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(title, 0, 0); grid.SetColumnSpan(title, 3);
        var notice = new Label { Text = "网络验证原型：Claude 已验证启动及启动阶段联网；登录后功能仍待验证。单实例程序可能把请求交给外部已有进程。", AutoSize = true, MaximumSize = new(730, 0), ForeColor = Color.DarkRed, Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(notice, 0, 1); grid.SetColumnSpan(notice, 3);
        AddLabel(grid, "运行软件", 2);
        var apps = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        apps.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 3; ++i) apps.ColumnStyles.Add(new(SizeType.AutoSize));
        apps.Controls.Add(software, 0, 0); apps.Controls.Add(addSoftware, 1, 0); apps.Controls.Add(editSoftware, 2, 0); apps.Controls.Add(removeSoftware, 3, 0);
        grid.Controls.Add(apps, 1, 2); grid.SetColumnSpan(apps, 2);
        software.SelectedIndexChanged += (_, _) => SelectSoftware();
        addSoftware.Click += (_, _) => EditSoftware(false); editSoftware.Click += (_, _) => EditSoftware(true);
        removeSoftware.Click += (_, _) => RemoveSoftware();
        AddLabel(grid, "程序路径", 3); grid.Controls.Add(executable, 1, 3); grid.SetColumnSpan(executable, 2);
        AddLabel(grid, "启动参数", 4); grid.Controls.Add(arguments, 1, 4); grid.SetColumnSpan(arguments, 2);
        AddLabel(grid, "独立代理", 5);
        var proxy = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; proxy.Controls.Add(proxyHost); proxy.Controls.Add(proxyPort);
        checkProxy.Click += async (_, _) => await TrackOperation(async () => {
            preparing = true; UpdateActions();
            try { await CheckProxy(); }
            catch (Exception ex) { Append("代理检测失败：" + ex.Message); }
            finally { preparing = false; UpdateActions(); }
        });
        configureProxy.Click += (_, _) => {
            using var dialog = new ProxySettingsDialog(dedicatedProxy);
            if (dialog.ShowDialog(this) == DialogResult.OK) {
                dedicatedProxy.Dispose(); RefreshProxy();
                Append(dedicatedProxy.CleanupComplete ? "独立代理配置已保存；旧内核及端口已清理，下次检测或启动使用新节点。" : "独立代理配置已保存，但旧内核或端口尚未释放；未启动新节点，详情见退出检查日志。");
            }
        };
        proxy.Controls.Add(checkProxy); proxy.Controls.Add(configureProxy); grid.Controls.Add(proxy, 1, 5); grid.SetColumnSpan(proxy, 2);
        country.SetOptions(WindowsSettings.Regions().Cast<object>().ToArray());
        language.SetOptions(WindowsSettings.Languages().Cast<object>().ToArray());
        timeZone.SetOptions(WindowsSettings.TimeZones().Cast<object>().ToArray());
        AddLabel(grid, "国家或地区", 6); grid.Controls.Add(country, 1, 6); grid.SetColumnSpan(country, 2);
        AddLabel(grid, "语言", 7); grid.Controls.Add(language, 1, 7); grid.SetColumnSpan(language, 2);
        AddLabel(grid, "时区", 8); grid.Controls.Add(timeZone, 1, 8); grid.SetColumnSpan(timeZone, 2);
        grid.Controls.Add(dst, 1, 9); grid.SetColumnSpan(dst, 2);
        grid.Controls.Add(timeNote, 1, 10); grid.SetColumnSpan(timeNote, 2);
        timeZone.SelectedIndexChanged += (_, _) => UpdateTimeNote(); dst.CheckedChanged += (_, _) => UpdateTimeNote();
        UpdateTimeNote();
        var access = new Label { Text = "关闭窗口后在托盘运行；从托盘退出会结束全部运行，请先保存文件。", AutoSize = true, Padding = new(0, 4, 0, 8) };
        grid.Controls.Add(access, 0, 11); grid.SetColumnSpan(access, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; buttons.Controls.Add(start);
        var probe = new Button { Text = "选择环境测试程序", AutoSize = true }; probe.Click += (_, _) => {
            if (preparing || applicationList == null) return;
            var entry = applicationList.Applications.FirstOrDefault(a => a.Application == "RegionProbe.exe" && a.Arguments == "")
                ?? new LaunchApplication { Name = "环境测试程序", Application = "RegionProbe.exe", Environment = ReadSettings() };
            SaveSoftware(entry);
        };
        buttons.Controls.Add(probe); grid.Controls.Add(buttons, 0, 12); grid.SetColumnSpan(buttons, 3);
        var openLogs = new Button { Text = "打开日志", AutoSize = true };
        openLogs.Click += (_, _) => {
            try { var folder = Path.Combine(data, "logs"); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception ex) { Append("无法打开日志目录：" + ex.Message); }
        };
        buttons.Controls.Add(openLogs);
        buttons.Controls.Add(linkBrowser);
        linkBrowser.Click += (_, _) => {
            try { using var dialog = new LinkBrowserDialog(data, applicationList); if (dialog.ShowDialog(this) == DialogResult.OK) Append("授权链接浏览器已保存，下次启动程序生效。"); }
            catch (Exception ex) { Append("授权浏览器配置读取失败：" + ex.Message); }
        };
        grid.Controls.Add(log, 0, 13); grid.SetColumnSpan(log, 3); Controls.Add(grid);
        ApplySettings(new LaunchEnvironment());
        start.Click += async (_, _) => await TrackOperation(StartSession);
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
        try { var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(data, "profile.json"))); if (p != null) { executable.Text = p.Application; arguments.Text = p.Arguments; proxyHost.Text = p.ProxyHost; proxyPort.Value = Math.Clamp(p.ProxyPort, 1, 65535); ApplySettings(new LaunchEnvironment { GeoId = p.GeoId, Locale = p.Locale, TimeZone = p.TimeZone, AutoDst = p.AutoDst }); } } catch (FileNotFoundException) {} catch (Exception ex) { Append("配置读取失败，使用默认值：" + ex.Message); }
        try { applicationList = library.Load(executable.Text, arguments.Text, ReadSettings()); RefreshSoftware(); }
        catch (Exception ex) { Append($"软件列表读取失败，原文件已保留：{ex.Message} 请检查 {Path.Combine(data, "applications.json")} 后重启。"); }
        country.SelectedIndexChanged += (_, _) => SaveSettings();
        language.SelectedIndexChanged += (_, _) => SaveSettings();
        timeZone.SelectedIndexChanged += (_, _) => SaveSettings();
        dst.CheckedChanged += (_, _) => SaveSettings();
        RefreshProxy();
        UpdateTimeNote();
        Append($"启动器版本 {Application.ProductVersion}，PID={Environment.ProcessId}；日志目录：{Path.Combine(data, "logs")}");
        if (logError != null) Append("日志文件不可写：" + logError);
        Append("地区、语言、时区按软件自动保存，下次启动生效；已运行的程序不变。代理由本次运行共用。");
    }
    void RefreshProxy()
    {
        proxyHost.Text = "127.0.0.1"; proxyHost.ReadOnly = true; proxyHost.Enabled = proxyPort.Enabled = false;
        try {
            proxyPort.Value = dedicatedProxy.Configured ? dedicatedProxy.Port : 17890;
            Append(dedicatedProxy.Configured ? "已加载独立代理配置；公网使用固定节点，局域网直连，DNS 使用专用解析器。" : "请点击“独立代理配置”填写或导入节点。");
        }
        catch { Append("独立代理配置无法读取，请点击“独立代理配置”修正。"); }
        UpdateActions();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); windowIcon.Dispose(); trayIcon.Dispose(); shutdown.Dispose(); dedicatedProxy?.Dispose(); diagnostics?.Dispose(); }
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
        start.Enabled = !preparing && software.SelectedItem is LaunchApplication && applicationList != null;
        software.Enabled = addSoftware.Enabled = !preparing && applicationList != null;
        editSoftware.Enabled = removeSoftware.Enabled = start.Enabled;
        configureProxy.Enabled = session == null && !preparing;
        linkBrowser.Enabled = session == null && !preparing;
        checkProxy.Enabled = !preparing;
        country.Enabled = language.Enabled = timeZone.Enabled = dst.Enabled = start.Enabled;
    }
    void RefreshSoftware()
    {
        loadingSoftware = true;
        software.Items.Clear();
        software.Items.AddRange(applicationList!.Applications.Cast<object>().ToArray());
        software.SelectedItem = applicationList.Applications.FirstOrDefault(a => a.Id == applicationList.SelectedId) ?? applicationList.Applications.FirstOrDefault();
        loadingSoftware = false; SelectSoftware();
    }
    void SelectSoftware()
    {
        if (loadingSoftware) return;
        var entry = software.SelectedItem as LaunchApplication;
        executable.Text = entry?.Application ?? ""; arguments.Text = entry?.Arguments ?? "";
        if (entry != null) ApplySettings(entry.Environment ?? new LaunchEnvironment());
        if (applicationList != null && applicationList.SelectedId != entry?.Id) {
            var updated = applicationList with { SelectedId = entry?.Id };
            try { library.Save(updated); applicationList = updated; }
            catch (Exception ex) { Append("无法保存软件选择：" + ex.Message); }
        }
        UpdateActions();
    }
    void EditSoftware(bool edit)
    {
        if (preparing || applicationList == null) return;
        using var dialog = new ApplicationEditor(edit ? software.SelectedItem as LaunchApplication : null, ReadSettings());
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
    void RemoveSoftware()
    {
        if (preparing || applicationList == null || software.SelectedItem is not LaunchApplication entry) return;
        if (MessageBox.Show(this, $"从列表中删除“{entry.Name}”？这不会卸载软件或删除其文件。", "删除软件", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var entries = applicationList.Applications.Where(a => a.Id != entry.Id).ToList();
        SaveSoftwareList(applicationList with { SelectedId = entries.FirstOrDefault()?.Id, Applications = entries });
    }
    LaunchEnvironment ReadSettings() => new() {
        GeoId = (country.Value as RegionOption)?.GeoId ?? 244,
        Locale = (language.Value as LanguageOption)?.Name ?? "en-US",
        TimeZone = (timeZone.Value as TimeZoneInfo)?.Id ?? "Pacific Standard Time", AutoDst = dst.Checked
    };
    void SaveSettings()
    {
        if (loadingSettings || loadingSoftware || preparing || applicationList == null || software.SelectedItem is not LaunchApplication entry) return;
        var changed = entry with { Environment = ReadSettings() };
        if (changed == entry) return;
        var updated = applicationList with { Applications = applicationList.Applications.Select(a => a.Id == entry.Id ? changed : a).ToList() };
        try {
            library.Save(updated); applicationList = updated;
            loadingSoftware = true;
            int index = software.SelectedIndex; software.Items[index] = changed; software.SelectedIndex = index;
        } catch (Exception ex) { ApplySettings(entry.Environment ?? new LaunchEnvironment()); Append("地区设置未保存：" + ex.Message); }
        finally { loadingSoftware = false; UpdateActions(); }
    }
    void ApplySettings(LaunchEnvironment profile)
    {
        loadingSettings = true;
        country.ResetFilter(); language.ResetFilter(); timeZone.ResetFilter();
        country.SelectedItem = country.Items.Cast<RegionOption>().FirstOrDefault(r => r.GeoId == profile.GeoId) ?? country.Items.Cast<RegionOption>().First(r => r.GeoId == 244);
        language.SelectedItem = language.Items.Cast<LanguageOption>().FirstOrDefault(l => l.Name.Equals(profile.Locale, StringComparison.OrdinalIgnoreCase)) ?? language.Items.Cast<LanguageOption>().First(l => l.Name == "en-US");
        timeZone.SelectedItem = timeZone.Items.Cast<TimeZoneInfo>().FirstOrDefault(z => z.Id == profile.TimeZone) ?? timeZone.Items.Cast<TimeZoneInfo>().First(z => z.Id == "Pacific Standard Time");
        dst.Checked = profile.AutoDst; UpdateTimeNote();
        loadingSettings = false;
    }
    void UpdateTimeNote()
    {
        if (timeZone.Value is not TimeZoneInfo zone) return;
        TimeSpan offset = dst.Checked ? zone.GetUtcOffset(DateTime.UtcNow) : zone.BaseUtcOffset;
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        timeNote.Text = $"{(dst.Checked ? "按 Windows 时区规则自动切换" : "全年使用标准时间")}；当前 UTC{sign}{offset.Duration():hh\\:mm}。";
    }
    static void AddLabel(TableLayoutPanel grid, string text, int row) => grid.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 6, 0, 0) }, 0, row);
    void Append(string text)
    {
        string line = diagnostics?.Write(text) ?? $"[{DateTime.Now:HH:mm:ss}] {text}";
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(() => ShowLog(line)); } catch (InvalidOperationException) { } return; }
        ShowLog(line);
    }
    void ShowLog(string line)
    {
        if (IsDisposed || Disposing) return;
        if (log.TextLength > 120000) log.Text = log.Text[^60000..];
        log.AppendText(line + Environment.NewLine);
    }
    async Task CheckProxy()
    {
        if (!dedicatedProxy.Configured) throw new IOException("请先点击“独立代理配置”填写或导入节点。");
        Append("开始检测并修复：固定出口规则 → 代理实际联网及必要时重连 → 环境内实际联网。");
        Append($"检测通道：启动器独立代理 127.0.0.1:{dedicatedProxy.Port}，专用 DNS {dedicatedProxy.DnsAddress}:53。");
        var throughProxy = await proxyRecovery.CheckAsync(shutdown.Token);
        shutdown.Token.ThrowIfCancellationRequested();
        ProxyHealth.RequireInternet(throughProxy);
        var inside = await EnvironmentCheck.RunAsync(AppContext.BaseDirectory, data, dedicatedProxy.Port, dedicatedProxy.DnsAddress, session == null ? null : currentJob, Append, diagnostics, shutdown.Token);
        ProxyHealth.RequireMatchingExit(throughProxy, inside);
        Append($"实际联网及环境出口校验通过：代理 {throughProxy.Count(r => r.Ok)}/{throughProxy.Length}，环境 {inside.Count(r => r.Ok)}/{inside.Length} 个站点成功。同一站点两层出口一致；失败站点见上方日志。");
        Append("此结果验证检测程序当前的 TCP/HTTPS 路径，不代表所有应用、UDP 或所有网站始终可用；不能用国内网站加载快慢判断是否直连。");
    }
    async Task StartSession()
    {
        if (preparing || exiting) return;
        bool join = session != null;
        string applicationName = (software.SelectedItem as LaunchApplication)?.Name ?? "所选程序";
        preparing = true; UpdateActions();
        try
        {
            if (software.SelectedItem is not LaunchApplication selected) throw new IOException("请先添加并选择要启动的软件。");
            ApplicationLibrary.ValidateEntry(selected);
            string app = ApplicationLibrary.ResolvePath(selected.Application); if (!File.Exists(app)) throw new FileNotFoundException("程序不存在，请点击“编辑”更新程序路径。");
            string appArgs = selected.Arguments;
            Append($"请求启动软件：{selected.Name}；程序={app}；模式={(join ? "加入当前环境" : "新建环境")}。");
            foreach (string s in new[] { app, appArgs, proxyHost.Text }) if (s.Contains('\n') || s.Contains('\r') || s.Contains('\0')) throw new ArgumentException("配置不能包含换行符或空字符。");
            if (!dedicatedProxy.Configured) throw new IOException("请先配置独立代理和专用 DNS，程序尚未启动。");
            string dnsAddress = dedicatedProxy.DnsAddress;
            start.Enabled = false; await CheckProxy();
            shutdown.Token.ThrowIfCancellationRequested();
            var settings = selected.Environment ?? throw new IOException("所选软件缺少地区设置，请重新编辑并保存。");
            settings.Validate();
            var region = WindowsSettings.Regions().Single(r => r.GeoId == settings.GeoId);
            var profile = new Profile { GeoId = settings.GeoId, Locale = settings.Locale, TimeZone = settings.TimeZone, Application = selected.Application, Arguments = selected.Arguments, ProxyHost = proxyHost.Text.Trim(), ProxyPort = (int)proxyPort.Value, AutoDst = settings.AutoDst };
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
                preparing = false; UpdateActions();
                using (candidate) { await MonitorLaunch(candidate); Append($"加入程序已结束，退出码 {candidate.ExitCode}。"); }
                return;
            }
            session = candidate;
            preparing = false; UpdateActions();
            Append("正在准备本次进程组；目标程序将在网络引擎就绪后运行。");
            await MonitorLaunch(session); diagnostics?.CaptureNetworkLog("会话 " + id); Append($"本次运行已结束，退出码 {session.ExitCode}。日志：{folder}"); session.Dispose(); session = null;
        }
        catch (Exception ex) {
            Append("未启动或运行失败：" + ex.Message);
            if (session is { HasExited: true }) { session.Dispose(); session = null; }
            NotifyLaunchFailure(applicationName);
        }
        finally { preparing = false; UpdateActions(); }
    }
    async Task MonitorLaunch(Process process) {
        bool started = false;
        Task logs = Task.WhenAll(
            ProcessLog.ReadAsync(process.StandardOutput.BaseStream, line => {
                if (line.StartsWith("STARTED PID=", StringComparison.Ordinal) && uint.TryParse(line[12..], out uint pid) && pid > 0) started = true;
                Append(line);
            }), ProcessLog.ReadAsync(process.StandardError.BaseStream, Append));
        await process.WaitForExitAsync(); await logs;
        // Session exit codes also include the application's own exit code; only missing startup is a launch failure.
        if (!started) throw new IOException($"运行环境未能启动目标程序，退出码 {process.ExitCode}。具体原因见前面的日志。");
    }
    void NotifyLaunchFailure(string applicationName) {
        if (exiting || shutdown.IsCancellationRequested || IsDisposed) return;
        string message = $"“{applicationName}”未能启动。请在启动器中点击“打开日志”查看失败原因。";
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
