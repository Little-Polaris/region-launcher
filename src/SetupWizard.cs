using System.Text.Json;

/// <summary>What the first-run wizard needs from the launcher.</summary>
sealed record WizardHost(
    string? InstallRoot,
    Func<bool> ProxyConfigured, Action ConfigureProxy,
    Func<int> ApplicationCount, Action AddApplication,
    Func<bool> AutoUpdate, Action<bool> SetAutoUpdate);

/// <summary>New-user guide: background service, proxy, first program, updates.</summary>
sealed class SetupWizard : Form
{
    static string FlagPath(string data) => Path.Combine(data, "onboarding.json");
    public static bool Completed(string data) => File.Exists(FlagPath(data));
    public static void MarkCompleted(string data)
    {
        try { Directory.CreateDirectory(data); File.WriteAllText(FlagPath(data), JsonSerializer.Serialize(new { completed = DateTime.Now.ToString("o") })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    readonly WizardHost host;
    readonly string[] titles = ["欢迎", "后台服务", "代理节点", "添加程序", "完成"];
    readonly AntdUI.Steps steps = new() { Dock = DockStyle.Top, Height = 64 };
    readonly Panel page = new() { Dock = DockStyle.Fill, Padding = new(30, 14, 30, 10), BackColor = HomeTheme.Surface };
    readonly AntdUI.Button back = HomeTheme.Action("上一步");
    readonly AntdUI.Button next = HomeTheme.Action("下一步", AntdUI.TTypeMini.Primary);
    readonly AntdUI.Button skip = HomeTheme.Link("跳过向导");
    int index;
    bool serviceBusy;

    public SetupWizard(WizardHost host)
    {
        this.host = host;
        foreach (string title in titles) steps.Items.Add(new AntdUI.StepsItem { Title = title });
        var top = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new(24, 12, 24, 0), BackColor = HomeTheme.Surface };
        top.Controls.Add(steps);
        back.Click += (_, _) => Show(index - 1);
        next.Click += (_, _) => { if (index == titles.Length - 1) DialogResult = DialogResult.OK; else Show(index + 1); };
        skip.Click += (_, _) => DialogResult = DialogResult.Cancel;
        skip.Dock = DockStyle.Left;
        var footer = HomeTheme.Footer(next, back); footer.Dock = DockStyle.Bottom;
        footer.Controls.Add(skip);
        Controls.Add(page); Controls.Add(top); Controls.Add(footer);
        HomeTheme.Dialog(this, "欢迎使用 Region Launcher", new(640, 460), new(560, 420));
        Icon = AppIcon.Load(new Size(32, 32));
        Show(0);
    }

    void Show(int target)
    {
        if (target < 0 || target >= titles.Length || serviceBusy) return;
        index = target;
        steps.Current = index;
        back.Visible = index > 0;
        next.Text = index == titles.Length - 1 ? "开始使用" : "下一步";
        skip.Visible = index < titles.Length - 1;
        page.SuspendLayout();
        while (page.Controls.Count != 0) page.Controls[0].Dispose();
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = HomeTheme.Surface };
        page.Controls.Add(content);
        switch (index) {
            case 0: Welcome(content); break;
            case 1: Service(content); break;
            case 2: Proxy(content); break;
            case 3: Applications(content); break;
            default: Finish(content); break;
        }
        page.ResumeLayout(true);
    }

    Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new(HomeTheme.FontName, 13, FontStyle.Bold), ForeColor = HomeTheme.Text, Margin = new(0, 4, 0, 10) };
    Label Body(string text, Color? color = null) => new() { Text = text, AutoSize = true, MaximumSize = new(LogicalToDeviceUnits(560), 0), ForeColor = color ?? HomeTheme.Muted, Margin = new(0, 0, 0, 10) };

    AntdUI.Alert State(bool ok, string title, string text) => new() {
        TextTitle = title, Text = text, Icon = ok ? AntdUI.TType.Success : AntdUI.TType.Warn, Radius = 8,
        Width = LogicalToDeviceUnits(560), Height = LogicalToDeviceUnits(text.Length > 40 ? 84 : 66), Margin = new(0, 4, 0, 12) };

    void Welcome(FlowLayoutPanel content)
    {
        content.Controls.Add(Heading("欢迎使用 Region Launcher"));
        content.Controls.Add(Body("Region Launcher 为你选择的程序提供独立的地区、语言和时区环境，并让这些程序通过你自己的代理节点联网，不影响电脑上的其他程序。"));
        content.Controls.Add(Body("接下来用几步完成首次设置：\n\n①  确认后台网络服务已安装\n②  配置你的代理节点或导入订阅\n③  添加第一个要启动的程序", HomeTheme.Text));
        content.Controls.Add(Body("每一步都可以稍后在右上角“更多”菜单中重新设置。"));
    }

    void Service(FlowLayoutPanel content)
    {
        content.Controls.Add(Heading("后台网络服务"));
        content.Controls.Add(Body("后台服务负责接管所选程序的网络连接。安装或更新需要一次 Windows 管理员确认，之后日常使用无需管理员权限。"));
        if (host.InstallRoot is not { } root || !File.Exists(Path.Combine(root, "install", "request_install.ps1"))) {
            content.Controls.Add(State(false, "无法在这里安装服务", "当前副本不含服务安装文件。请使用安装程序安装，或运行程序目录中的“安装或更新后台服务.cmd”。"));
            return;
        }
        var state = Installation.Service(root);
        bool current = state == Installation.ServiceState.Current;
        content.Controls.Add(State(current, current ? "已就绪" : state == Installation.ServiceState.Outdated ? "需要更新" : "尚未安装", Installation.ServiceText(state)));
        if (current) return;
        var install = HomeTheme.Action(state == Installation.ServiceState.Outdated ? "更新后台服务" : "安装后台服务", AntdUI.TTypeMini.Primary, "SafetyCertificateOutlined");
        install.Margin = new(0, 0, 0, 8);
        install.Click += async (_, _) => {
            serviceBusy = true; install.Loading = true; next.Enabled = back.Enabled = false;
            string? error = await Task.Run(() => Installation.RequestService(root, uninstall: false));
            serviceBusy = false; next.Enabled = back.Enabled = true;
            if (IsDisposed) return;
            if (error != null) MessageDialog.Show(this, "后台网络服务没有安装成功：" + error, "后台网络服务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Show(index);
        };
        content.Controls.Add(install);
    }

    void Proxy(FlowLayoutPanel content)
    {
        content.Controls.Add(Heading("代理节点"));
        content.Controls.Add(Body("所选程序会通过你配置的节点联网。可以手动填写节点，也可以在代理配置中导入订阅链接。Region Launcher 不提供任何节点。"));
        bool configured = host.ProxyConfigured();
        content.Controls.Add(State(configured, configured ? "已配置" : "尚未配置", configured ? "已保存独立代理配置，可以随时在“更多 → 独立代理配置”中修改。" : "配置并通过校验后即可启动程序。"));
        var configure = HomeTheme.Action(configured ? "修改代理配置…" : "配置代理…", configured ? AntdUI.TTypeMini.Default : AntdUI.TTypeMini.Primary, "CloudServerOutlined");
        configure.Click += (_, _) => { host.ConfigureProxy(); Show(index); };
        content.Controls.Add(configure);
    }

    void Applications(FlowLayoutPanel content)
    {
        content.Controls.Add(Heading("添加程序"));
        content.Controls.Add(Body("为要使用的程序选择地区、语言和时区。也可以直接把程序或快捷方式拖到主窗口。"));
        int count = host.ApplicationCount();
        content.Controls.Add(State(count > 0, count > 0 ? $"已添加 {count} 个程序" : "还没有程序", count > 0 ? "点击主窗口中的程序卡片即可在对应环境中启动。" : "添加后会显示在主窗口中。"));
        var add = HomeTheme.Action("添加程序…", count > 0 ? AntdUI.TTypeMini.Default : AntdUI.TTypeMini.Primary, "PlusOutlined");
        add.Click += (_, _) => { host.AddApplication(); Show(index); };
        content.Controls.Add(add);
    }

    void Finish(FlowLayoutPanel content)
    {
        content.Controls.Add(Heading("设置完成"));
        content.Controls.Add(Body("几个常用操作：\n\n•  点击程序卡片：在独立环境中启动该程序\n•  网络检测：检查节点连通性，并在断开时自动恢复\n•  切换节点：运行中更换出口节点\n•  右侧“正在运行”：查看、切换或关闭已启动的程序", HomeTheme.Text));
        var auto = new AntdUI.Checkbox { Text = "启动时自动检查更新（访问 GitHub）", Checked = host.AutoUpdate(), AutoSizeMode = AntdUI.TAutoSize.Auto, Margin = new(0, 6, 0, 0) };
        auto.CheckedChanged += (_, _) => host.SetAutoUpdate(auto.Checked);
        content.Controls.Add(auto);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (serviceBusy) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
