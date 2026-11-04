using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

ApplicationConfiguration.Initialize();
if (args.Length == 2 && args[0] == "--render-preview")
{
    using var preview = new Launcher(); preview.StartPosition = FormStartPosition.Manual;
    preview.Location = new(-32000, -32000); preview.ShowInTaskbar = false; preview.Show(); Application.DoEvents();
    using var bitmap = new Bitmap(preview.Width, preview.Height);
    preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); preview.Close(); return;
}
Application.Run(new Launcher());

sealed class Profile
{
    public string Application { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 7890;
    public bool AutoDst { get; set; } = true;
}

sealed class Launcher : Form
{
    readonly TextBox executable = new() { Dock = DockStyle.Fill };
    readonly TextBox arguments = new() { Dock = DockStyle.Fill };
    readonly TextBox proxyHost = new() { Text = "127.0.0.1", Width = 180 };
    readonly NumericUpDown proxyPort = new() { Minimum = 1, Maximum = 65535, Value = 7890, Width = 90 };
    readonly CheckBox dst = new() { Text = "自动切换夏令时与标准时间", Checked = true, AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "启动所选程序", AutoSize = true };
    readonly Button stop = new() { Text = "结束本次运行", Enabled = false, AutoSize = true };
    readonly string data = Path.Combine(AppContext.BaseDirectory, "data");
    Process? session;
    string? currentJob;
    public Launcher()
    {
        Text = "Region Launcher · 环境验证版"; ClientSize = new(800, 590); MinimumSize = new(700, 540);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 3, RowCount = 10 };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 100));
        for (int i = 0; i < 9; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        var title = new Label { Text = "美国 · 洛杉矶 · English (United States)", AutoSize = true, Font = new(Font, FontStyle.Bold), Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(title, 0, 0); grid.SetColumnSpan(title, 3);
        var notice = new Label { Text = "验证版：Claude Desktop 尚未适配验证。此工具不能保证账号状态，也不能完整接管外部浏览器、系统服务和所有 DNS 路径。", AutoSize = true, MaximumSize = new(730, 0), ForeColor = Color.DarkRed, Padding = new(0, 0, 0, 12) };
        grid.Controls.Add(notice, 0, 1); grid.SetColumnSpan(notice, 3);
        AddLabel(grid, "运行程序", 2); grid.Controls.Add(executable, 1, 2);
        var browse = new Button { Text = "选择…", AutoSize = true };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe" }; if (d.ShowDialog() == DialogResult.OK) executable.Text = d.FileName; };
        grid.Controls.Add(browse, 2, 2);
        AddLabel(grid, "启动参数", 3); grid.Controls.Add(arguments, 1, 3); grid.SetColumnSpan(arguments, 2);
        AddLabel(grid, "本机代理", 4);
        var proxy = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; proxy.Controls.Add(proxyHost); proxy.Controls.Add(proxyPort);
        var check = new Button { Text = "检测代理", AutoSize = true }; check.Click += async (_, _) => { try { await CheckProxy(); Append("SOCKS5 握手通过；尚不代表所有目标网站可访问。"); } catch (Exception ex) { Append("代理检测失败：" + ex.Message); } };
        proxy.Controls.Add(check); grid.Controls.Add(proxy, 1, 4); grid.SetColumnSpan(proxy, 2);
        AddLabel(grid, "时区", 5); grid.Controls.Add(dst, 1, 5); grid.SetColumnSpan(dst, 2);
        var timeNote = new Label { Text = "自动：夏令时 UTC−7 / 标准时间 UTC−8；关闭后固定 UTC−8。", AutoSize = true, Padding = new(0, 6, 0, 6) };
        grid.Controls.Add(timeNote, 1, 6); grid.SetColumnSpan(timeNote, 2);
        var access = new Label { Text = "文件使用原有权限；结束运行会关闭组内所有程序，请先保存文件。", AutoSize = true, Padding = new(0, 4, 0, 8) };
        grid.Controls.Add(access, 0, 7); grid.SetColumnSpan(access, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; buttons.Controls.Add(start); buttons.Controls.Add(stop);
        var probe = new Button { Text = "选择环境测试程序", AutoSize = true }; probe.Click += (_, _) => { executable.Text = Path.Combine(AppContext.BaseDirectory, "RegionProbe.exe"); arguments.Text = ""; };
        buttons.Controls.Add(probe); grid.Controls.Add(buttons, 0, 8); grid.SetColumnSpan(buttons, 3);
        var browser = new Button { Text = "打开独立浏览器", AutoSize = true };
        browser.Click += async (_, _) => await OpenBrowser(); buttons.Controls.Add(browser);
        grid.Controls.Add(log, 0, 9); grid.SetColumnSpan(log, 3); Controls.Add(grid);
        start.Click += async (_, _) => await StartSession(); stop.Click += (_, _) => StopSession();
        FormClosing += (_, e) =>
        {
            if (session != null && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "关闭启动器会结束环境内的全部程序。请先保存文件。\n现在关闭？", "结束运行", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            { e.Cancel = true; return; }
            StopSession();
        };
        Directory.CreateDirectory(data);
        try { var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(data, "profile.json"))); if (p != null) { executable.Text = p.Application; arguments.Text = p.Arguments; proxyHost.Text = p.ProxyHost; proxyPort.Value = Math.Clamp(p.ProxyPort, 1, 65535); dst.Checked = p.AutoDst; } } catch (FileNotFoundException) {} catch (Exception ex) { Append("配置读取失败，使用默认值：" + ex.Message); }
        Append("默认配置：美国 / en-US / 洛杉矶 / 自动夏令时。尚未安装或登录 Claude。");
    }
    static void AddLabel(TableLayoutPanel grid, string text, int row) => grid.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new(0, 6, 0, 0) }, 0, row);
    void Append(string text) { if (IsDisposed) return; if (InvokeRequired) { BeginInvoke(() => Append(text)); return; } log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); }
    async Task CheckProxy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync(proxyHost.Text.Trim(), (int)proxyPort.Value, cts.Token);
        var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
        byte[] answer = new byte[2]; await stream.ReadExactlyAsync(answer, cts.Token);
        if (answer[0] != 5 || answer[1] != 0) throw new IOException("此版本需要无需认证的 SOCKS5 代理。");
    }
    static string? FindBrowser()
    {
        foreach (string name in new[] { "chrome.exe", "msedge.exe" })
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                if (key?.GetValue("") is string path && File.Exists(path)) return path;
            }
        return null;
    }
    async Task OpenBrowser()
    {
        try
        {
            var path = FindBrowser() ?? throw new FileNotFoundException("未找到 Chrome 或 Edge，请先安装其中一个浏览器。");
            if (!System.Net.IPAddress.TryParse(proxyHost.Text.Trim(), out var ip) || !System.Net.IPAddress.IsLoopback(ip))
                throw new ArgumentException("独立浏览器入口目前使用本机代理，请填 127.0.0.1。");
            string browserData = Path.Combine(data, "browser", Path.GetFileNameWithoutExtension(path));
            Directory.CreateDirectory(browserData);
            string browserArgs = $"--user-data-dir=\"{browserData}\" --no-first-run --no-default-browser-check --lang=en-US --proxy-server=\"socks5://{proxyHost.Text.Trim()}:{proxyPort.Value}\" --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE {proxyHost.Text.Trim()}, EXCLUDE localhost\" --force-webrtc-ip-handling-policy=disable_non_proxied_udp about:blank";
            Append("独立浏览器使用专用资料目录；不会复用外面的浏览器窗口。");
            await StartSession(path, browserArgs, session != null);
        }
        catch (Exception ex) { Append("浏览器未启动：" + ex.Message); }
    }
    async Task StartSession(string? applicationOverride = null, string? argumentsOverride = null, bool join = false)
    {
        if (session != null && !join) return;
        try
        {
            string app = Path.GetFullPath(applicationOverride ?? executable.Text.Trim()); if (!File.Exists(app)) throw new FileNotFoundException("请选择存在的 .exe 程序。");
            string appArgs = argumentsOverride ?? arguments.Text;
            foreach (string s in new[] { app, appArgs, proxyHost.Text }) if (s.Contains('\n') || s.Contains('\r') || s.Contains('\0')) throw new ArgumentException("配置不能包含换行符或空字符。");
            start.Enabled = false; await CheckProxy();
            var profile = new Profile { Application = executable.Text, Arguments = arguments.Text, ProxyHost = proxyHost.Text.Trim(), ProxyPort = (int)proxyPort.Value, AutoDst = dst.Checked };
            File.WriteAllText(Path.Combine(data, "profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            string id = Guid.NewGuid().ToString("N"); if (!join) currentJob = @"Local\RegionLauncher." + id;
            string folder = Path.Combine(data, "sessions", id); Directory.CreateDirectory(folder); string config = Path.Combine(folder, "session.ini");
            string ini = $"[session]\r\nJob={currentJob}\r\nOwnerPid={Environment.ProcessId}\r\nApplication={app}\r\nArguments={appArgs}\r\nProxyHost={profile.ProxyHost}\r\nProxyPort={profile.ProxyPort}\r\nLocale=en-US\r\nRegion=US\r\nGeo=244\r\nTimeZone=Pacific Standard Time\r\nAutoDst={(dst.Checked ? 1 : 0)}\r\n";
            File.WriteAllText(config, ini, Encoding.Unicode);
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RegionSession.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(join ? "--join" : "--run"); psi.ArgumentList.Add(config);
            var candidate = new Process { StartInfo = psi }; candidate.OutputDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); }; candidate.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(e.Data); };
            try { candidate.Start(); } catch { candidate.Dispose(); throw; }
            if (join)
            {
                using (candidate) { candidate.BeginOutputReadLine(); candidate.BeginErrorReadLine(); await candidate.WaitForExitAsync(); Append($"浏览器启动进程已结束，退出码 {candidate.ExitCode}。"); }
                return;
            }
            session = candidate; session.BeginOutputReadLine(); session.BeginErrorReadLine(); stop.Enabled = true;
            proxyHost.Enabled = proxyPort.Enabled = dst.Enabled = false;
            Append("正在准备本次进程组；目标程序将在网络引擎就绪后运行。");
            await session.WaitForExitAsync(); Append($"本次运行已结束，退出码 {session.ExitCode}。日志：{folder}"); session.Dispose(); session = null;
        }
        catch (Exception ex) { Append("未启动或运行失败：" + ex.Message); if (session is { HasExited: true }) { session.Dispose(); session = null; } }
        finally { start.Enabled = session == null; stop.Enabled = session != null; proxyHost.Enabled = proxyPort.Enabled = dst.Enabled = session == null; }
    }
    void StopSession()
    {
        if (currentJob == null) return;
        try { using var evt = EventWaitHandle.OpenExisting(currentJob + ".stop"); evt.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }
}
