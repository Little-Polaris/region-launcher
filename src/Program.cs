using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--render-preview")
        {
            using var preview = new Launcher(); preview.StartPosition = FormStartPosition.Manual;
            preview.Location = new(-32000, -32000); preview.ShowInTaskbar = false; preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); preview.Close(); return;
        }
        Application.Run(new Launcher());
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
    readonly TextBox executable = new() { Dock = DockStyle.Fill };
    readonly TextBox arguments = new() { Dock = DockStyle.Fill };
    readonly TextBox proxyHost = new() { Text = "127.0.0.1", Width = 180 };
    readonly NumericUpDown proxyPort = new() { Minimum = 1, Maximum = 65535, Value = 7890, Width = 90 };
    readonly ComboBox country = new() { Name = "Country", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox language = new() { Name = "Language", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox timeZone = new() { Name = "TimeZone", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Label timeNote = new() { AutoSize = true, Padding = new(0, 6, 0, 6) };
    readonly CheckBox dst = new() { Text = "自动切换夏令时与标准时间", Checked = true, AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "启动所选程序", AutoSize = true };
    readonly Button stop = new() { Text = "结束本次运行", Enabled = false, AutoSize = true };
    readonly string data = Path.Combine(AppContext.BaseDirectory, "data");
    readonly DedicatedProxy dedicatedProxy = new();
    Process? session;
    string? currentJob;
    public Launcher()
    {
        Text = "Region Launcher · 环境验证版"; ClientSize = new(840, 720); MinimumSize = new(750, 650);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 3, RowCount = 13 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 100));
        for (int i = 0; i < 12; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        var title = new Label { Text = "独立运行环境", AutoSize = true, Font = new(Font, FontStyle.Bold), Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(title, 0, 0); grid.SetColumnSpan(title, 3);
        var notice = new Label { Text = "网络验证原型：Claude Desktop 尚未验证；单实例程序可能把请求交给外部已有进程。完整桌面应用隔离仍在验证中。", AutoSize = true, MaximumSize = new(730, 0), ForeColor = Color.DarkRed, Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(notice, 0, 1); grid.SetColumnSpan(notice, 3);
        AddLabel(grid, "运行程序", 2); grid.Controls.Add(executable, 1, 2);
        var browse = new Button { Text = "选择…", AutoSize = true };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe" }; if (d.ShowDialog(this) == DialogResult.OK) executable.Text = d.FileName; };
        grid.Controls.Add(browse, 2, 2);
        AddLabel(grid, "启动参数", 3); grid.Controls.Add(arguments, 1, 3); grid.SetColumnSpan(arguments, 2);
        AddLabel(grid, "本机代理", 4);
        var proxy = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; proxy.Controls.Add(proxyHost); proxy.Controls.Add(proxyPort);
        var check = new Button { Text = "检测代理", AutoSize = true }; check.Click += async (_, _) => { try { await CheckProxy(); Append("SOCKS5 握手通过；尚不代表所有目标网站可访问。"); } catch (Exception ex) { Append("代理检测失败：" + ex.Message); } };
        proxy.Controls.Add(check); grid.Controls.Add(proxy, 1, 4); grid.SetColumnSpan(proxy, 2);
        country.Items.AddRange(WindowsSettings.Regions().Cast<object>().ToArray());
        language.Items.AddRange(WindowsSettings.Languages().Cast<object>().ToArray());
        timeZone.Items.AddRange(WindowsSettings.TimeZones().Cast<object>().ToArray());
        AddLabel(grid, "国家或地区", 5); grid.Controls.Add(country, 1, 5); grid.SetColumnSpan(country, 2);
        AddLabel(grid, "语言", 6); grid.Controls.Add(language, 1, 6); grid.SetColumnSpan(language, 2);
        AddLabel(grid, "时区", 7); grid.Controls.Add(timeZone, 1, 7); grid.SetColumnSpan(timeZone, 2);
        grid.Controls.Add(dst, 1, 8); grid.SetColumnSpan(dst, 2);
        grid.Controls.Add(timeNote, 1, 9); grid.SetColumnSpan(timeNote, 2);
        timeZone.SelectedIndexChanged += (_, _) => UpdateTimeNote(); dst.CheckedChanged += (_, _) => UpdateTimeNote();
        UpdateTimeNote();
        var access = new Label { Text = "文件使用原有权限；结束运行会关闭组内所有程序，请先保存文件。", AutoSize = true, Padding = new(0, 4, 0, 8) };
        grid.Controls.Add(access, 0, 10); grid.SetColumnSpan(access, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; buttons.Controls.Add(start); buttons.Controls.Add(stop);
        var probe = new Button { Text = "选择环境测试程序", AutoSize = true }; probe.Click += (_, _) => { executable.Text = Path.Combine(AppContext.BaseDirectory, "RegionProbe.exe"); arguments.Text = ""; };
        buttons.Controls.Add(probe); grid.Controls.Add(buttons, 0, 11); grid.SetColumnSpan(buttons, 3);
        var join = new Button { Text = "加入所选程序", AutoSize = true };
        join.Click += async (_, _) => await StartSession(session != null); buttons.Controls.Add(join);
        grid.Controls.Add(log, 0, 12); grid.SetColumnSpan(log, 3); Controls.Add(grid);
        ApplySettings(new Profile());
        start.Click += async (_, _) => await StartSession(); stop.Click += (_, _) => StopSession();
        FormClosing += (_, e) =>
        {
            if (session != null && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "关闭启动器会结束环境内的全部程序。请先保存文件。\n现在关闭？", "结束运行", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            { e.Cancel = true; return; }
            StopSession();
        };
        FormClosed += (_, _) => dedicatedProxy.Dispose();
        dedicatedProxy.UnexpectedExit += () => { StopSession(); Append("独立代理已退出，正在结束本次运行；不会切回 7890。"); };
        Directory.CreateDirectory(data);
        try { var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(data, "profile.json"))); if (p != null) { executable.Text = p.Application; arguments.Text = p.Arguments; proxyHost.Text = p.ProxyHost; proxyPort.Value = Math.Clamp(p.ProxyPort, 1, 65535); ApplySettings(p); } } catch (FileNotFoundException) {} catch (Exception ex) { Append("配置读取失败，使用默认值：" + ex.Message); }
        if (dedicatedProxy.Configured)
        {
            proxyHost.Text = "127.0.0.1"; proxyPort.Value = dedicatedProxy.Port;
            proxyHost.Enabled = proxyPort.Enabled = false;
            Append("专用全代理通道：全部目标经固定节点，网站 DNS 经节点内的加密解析；不使用 7890 的国内直连规则。");
        }
        UpdateTimeNote();
        Append("地区、语言和时区选项来自 Windows；仅对本次环境生效。尚未安装或登录 Claude。");
    }
    void ApplySettings(Profile profile)
    {
        country.SelectedItem = country.Items.Cast<RegionOption>().FirstOrDefault(r => r.GeoId == profile.GeoId) ?? country.Items.Cast<RegionOption>().First(r => r.GeoId == 244);
        language.SelectedItem = language.Items.Cast<LanguageOption>().FirstOrDefault(l => l.Name == profile.Locale) ?? language.Items.Cast<LanguageOption>().First(l => l.Name == "en-US");
        timeZone.SelectedItem = timeZone.Items.Cast<TimeZoneInfo>().FirstOrDefault(z => z.Id == profile.TimeZone) ?? timeZone.Items.Cast<TimeZoneInfo>().First(z => z.Id == "Pacific Standard Time");
        dst.Checked = profile.AutoDst; UpdateTimeNote();
    }
    void UpdateTimeNote()
    {
        if (timeZone.SelectedItem is not TimeZoneInfo zone) return;
        TimeSpan offset = dst.Checked ? zone.GetUtcOffset(DateTime.UtcNow) : zone.BaseUtcOffset;
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        timeNote.Text = $"{(dst.Checked ? "按 Windows 时区规则自动切换" : "全年使用标准时间")}；当前 UTC{sign}{offset.Duration():hh\\:mm}。";
    }
    static void AddLabel(TableLayoutPanel grid, string text, int row) => grid.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 6, 0, 0) }, 0, row);
    void Append(string text) { if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(() => Append(text)); return; } log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); }
    async Task CheckProxy()
    {
        if (dedicatedProxy.Configured) await dedicatedProxy.EnsureStarted();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(proxyHost.Text.Trim(), (int)proxyPort.Value, cts.Token);
        var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
        byte[] answer = new byte[2]; await stream.ReadExactlyAsync(answer, cts.Token);
        if (answer[0] != 5 || answer[1] != 0) throw new IOException("此版本需要无需认证的 SOCKS5 代理。");
    }
    async Task StartSession(bool join = false)
    {
        if (session != null && !join) return;
        try
        {
            string app = Path.GetFullPath(executable.Text.Trim()); if (!File.Exists(app)) throw new FileNotFoundException("请选择存在的 .exe 程序。");
            string appArgs = arguments.Text;
            foreach (string s in new[] { app, appArgs, proxyHost.Text }) if (s.Contains('\n') || s.Contains('\r') || s.Contains('\0')) throw new ArgumentException("配置不能包含换行符或空字符。");
            if (!dedicatedProxy.Configured) throw new IOException("请先配置独立代理和专用 DNS，程序尚未启动。");
            string dnsAddress = dedicatedProxy.DnsAddress;
            start.Enabled = false; await CheckProxy();
            var region = country.SelectedItem as RegionOption ?? throw new IOException("请选择国家或地区。");
            var selectedLanguage = language.SelectedItem as LanguageOption ?? throw new IOException("请选择语言。");
            var selectedZone = timeZone.SelectedItem as TimeZoneInfo ?? throw new IOException("请选择时区。");
            var profile = new Profile { GeoId = region.GeoId, Locale = selectedLanguage.Name, TimeZone = selectedZone.Id, Application = executable.Text, Arguments = arguments.Text, ProxyHost = proxyHost.Text.Trim(), ProxyPort = (int)proxyPort.Value, AutoDst = dst.Checked };
            File.WriteAllText(Path.Combine(data, "profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            string id = Guid.NewGuid().ToString("N"); if (!join) currentJob = @"Local\RegionLauncher." + id;
            string folder = Path.Combine(data, "sessions", id); Directory.CreateDirectory(folder); string config = Path.Combine(folder, "session.ini");
            string ini = $"[session]\r\nJob={currentJob}\r\nOwnerPid={Environment.ProcessId}\r\nApplication={app}\r\nArguments={appArgs}\r\nProxyHost={profile.ProxyHost}\r\nProxyPort={profile.ProxyPort}\r\nLocale={profile.Locale}\r\nRegion={region.Code}\r\nGeo={profile.GeoId}\r\nTimeZone={profile.TimeZone}\r\nAutoDst={(dst.Checked ? 1 : 0)}\r\n";
            File.WriteAllText(config, ini, Encoding.Unicode);
            File.AppendAllText(config, $"DnsAddress={dnsAddress}\r\n", Encoding.Unicode);
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(join ? "--join" : "--run"); psi.ArgumentList.Add(config);
            var candidate = new Process { StartInfo = psi }; candidate.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); }; candidate.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
            try { candidate.Start(); } catch { candidate.Dispose(); throw; }
            if (join)
            {
                using (candidate) { candidate.BeginOutputReadLine(); candidate.BeginErrorReadLine(); await candidate.WaitForExitAsync(); Append($"加入程序已结束，退出码 {candidate.ExitCode}。"); }
                return;
            }
            session = candidate; session.BeginOutputReadLine(); session.BeginErrorReadLine(); stop.Enabled = true;
            proxyHost.Enabled = proxyPort.Enabled = dst.Enabled = country.Enabled = language.Enabled = timeZone.Enabled = false;
            Append("正在准备本次进程组；目标程序将在网络引擎就绪后运行。");
            await session.WaitForExitAsync(); Append($"本次运行已结束，退出码 {session.ExitCode}。日志：{folder}"); session.Dispose(); session = null;
        }
        catch (Exception ex) { Append("未启动或运行失败：" + ex.Message); if (session is { HasExited: true }) { session.Dispose(); session = null; } }
        finally { start.Enabled = session == null; stop.Enabled = session != null; dst.Enabled = country.Enabled = language.Enabled = timeZone.Enabled = session == null; proxyHost.Enabled = proxyPort.Enabled = session == null && !dedicatedProxy.Configured; }
    }
    void StopSession()
    {
        if (currentJob == null) return;
        try { using var evt = EventWaitHandle.OpenExisting(currentJob + ".stop"); evt.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }
}
