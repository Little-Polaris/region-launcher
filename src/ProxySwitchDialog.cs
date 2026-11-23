using System.Text.Json.Nodes;

sealed class ProxySwitchDialog : Form
{
    sealed record Choice(string Name, JsonObject? Node)
    {
        public override string ToString() => Name;
    }

    readonly ListBox choices = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    readonly Button apply = new() { Text = "立即切换", Enabled = false, AutoSize = true, DialogResult = DialogResult.OK };
    public JsonObject? SelectedNode => (choices.SelectedItem as Choice)?.Node;
    public string SelectedName => (choices.SelectedItem as Choice)?.Name ?? "";

    public ProxySwitchDialog(string configPath)
    {
        Text = "切换节点"; Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(620, 455); MinimumSize = new(540, 380);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "切换会关闭本环境的旧连接，正在运行的程序可重新连接。", AutoSize = true }, 0, 0);
        var sources = new ComboBox { AccessibleName = "选择订阅来源", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        layout.Controls.Add(sources, 0, 1);
        layout.Controls.Add(choices, 0, 2);
        layout.Controls.Add(status, 0, 3);
        var import = new Button { Text = "从文件添加…", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([import, apply, cancel]); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
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
                var source = sources.SelectedItem as SavedSubscription;
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
        sources.SelectedItem = library.Items.FirstOrDefault(x => x.Id == library.LastUsed);
        if (sources.SelectedIndex < 0) sources.SelectedIndex = 0;

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
