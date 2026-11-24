using System.Text.Json.Nodes;

sealed class ProxySwitchDialog : Form
{
    sealed record Choice(string Name, JsonObject? Node)
    {
        public override string ToString() => Name;
    }

    readonly ModernList choices = new() { Dock = DockStyle.Fill, AccessibleName = "可切换的节点", EmptyText = "没有可切换的节点", EmptyHint = "可从文件添加，或在订阅管理中获取" };
    readonly AntdUI.Select sources = HomeTheme.Select();
    readonly Label status = HomeTheme.Hint("");
    readonly AntdUI.Button apply = HomeTheme.Action("立即切换", AntdUI.TTypeMini.Primary, "SwapOutlined");
    public JsonObject? SelectedNode => (choices.SelectedItem as Choice)?.Node;
    public string SelectedName => (choices.SelectedItem as Choice)?.Name ?? "";

    public ProxySwitchDialog(string configPath)
    {
        HomeTheme.Dialog(this, "切换节点", new(620, 500), new(540, 400));
        apply.Enabled = false; apply.DialogResult = DialogResult.OK;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20, 18, 20, 4), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var intro = new AntdUI.Alert { Text = "切换会关闭本环境的旧连接，正在运行的程序可重新连接。", Icon = AntdUI.TType.Info,
            Dock = DockStyle.Fill, Radius = 8, Margin = new(3, 0, 3, 8), Height = LogicalToDeviceUnits(40) };
        layout.Controls.Add(intro, 0, 0);
        sources.AccessibleName = "选择订阅来源"; sources.PrefixSvg = "CloudOutlined";
        layout.Controls.Add(sources, 0, 1);
        var frame = new LogFrame(3, 6) { Dock = DockStyle.Fill, Fill = HomeTheme.Surface, Margin = Padding.Empty };
        frame.Controls.Add(choices);
        layout.Controls.Add(frame, 0, 2);
        layout.Controls.Add(status, 0, 3);
        var import = HomeTheme.Action("从文件添加…", AntdUI.TTypeMini.Default, "FileAddOutlined");
        var cancel = HomeTheme.Action("取消"); cancel.DialogResult = DialogResult.Cancel;
        var buttons = HomeTheme.Footer(cancel, apply, import); buttons.Dock = DockStyle.Bottom;
        Controls.Add(layout); Controls.Add(buttons);
        AcceptButton = apply; CancelButton = cancel;
        choices.SelectedIndexChanged += (_, _) => apply.Enabled = choices.SelectedItem is Choice;

        var saved = ProxyConfiguration.ReadNode(File.ReadAllText(configPath), out _);
        bool automatic = ProxyConfiguration.IsFailover(saved);
        var restore = automatic
            ? new Choice($"恢复自动切换 · {saved["name"]}", null)
            : new Choice($"恢复启动时的节点 · {saved["type"]}", saved);
        var fromFile = new List<SubscriptionNode>();
        var library = new SubscriptionLibrary(Path.GetDirectoryName(configPath)!);
        bool unreadable = false;
        try { library.Load(); } catch { unreadable = true; }
        sources.Items.Add("启动配置和临时导入");
        sources.Items.AddRange(library.Items.Cast<object>().ToArray());

        void Populate()
        {
            choices.BeginUpdate();
            choices.Items.Clear();
            choices.Items.Add(restore);
            try
            {
                var source = sources.SelectedValue as SavedSubscription;
                var entries = source?.ReadNodes().Nodes ?? fromFile;
                if (source == null && automatic)
                    entries = entries.Concat(Subscription.Parse(saved["members"]!.ToJsonString(), cached: true).Nodes).ToList();
                foreach (var entry in entries.Where(entry => !ProxyConfiguration.IsFailover(entry.Node))
                    .DistinctBy(entry => entry.Node.ToJsonString()))
                    choices.Items.Add(new Choice(entry.ToString(), entry.Node));
                status.Text = unreadable ? "订阅缓存无法读取，可以从本地文件临时选择。" :
                    "运行中可切换固定节点；要启用其他自动组，请在独立代理配置中保存。";
            }
            catch { status.Text = "该订阅的缓存无法读取，请在订阅管理中重新获取。"; }
            finally { choices.EndUpdate(); }
        }
        sources.SelectedIndexChanged += (_, _) => Populate();
        var last = library.Items.FirstOrDefault(x => x.Id == library.LastUsed);
        sources.SelectedIndex = last == null ? 0 : sources.Items.IndexOf(last);
        Populate();

        import.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "订阅或节点 (*.yaml;*.yml;*.json;*.txt)|*.yaml;*.yml;*.json;*.txt|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > Subscription.MaxBytes) throw new IOException("订阅文件不能超过 4 MB。");
                fromFile.AddRange(Subscription.Parse(File.ReadAllText(dialog.FileName)).Nodes);
                if (sources.SelectedIndex != 0) sources.SelectedIndex = 0; else Populate();
                status.Text = "已读取文件中的可用节点。";
            }
            catch (IOException ex) { status.Text = ex.Message; }
            catch (Exception) { status.Text = "无法读取节点文件。"; }
        };
    }
}
