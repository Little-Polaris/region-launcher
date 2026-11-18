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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(14), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "切换会关闭本环境的旧连接，正在运行的程序可重新连接。", AutoSize = true }, 0, 0);
        layout.Controls.Add(choices, 0, 1);
        layout.Controls.Add(status, 0, 2);
        var import = new Button { Text = "从文件添加…", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([import, apply, cancel]); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        AcceptButton = apply; CancelButton = cancel;
        choices.SelectedIndexChanged += (_, _) => apply.Enabled = choices.SelectedItem is Choice;

        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        void Add(SubscriptionResult result)
        {
            foreach (var entry in result.Nodes.Where(entry => !ProxyConfiguration.IsFailover(entry.Node)))
                if (nodeKeys.Add(entry.Node.ToJsonString()))
                    choices.Items.Add(new Choice(entry.ToString(), entry.Node));
        }

        var saved = ProxyConfiguration.ReadNode(File.ReadAllText(configPath), out _);
        bool automatic = ProxyConfiguration.IsFailover(saved);
        if (automatic)
            choices.Items.Add(new Choice($"恢复自动切换 · {saved["name"]}", null));
        else choices.Items.Add(new Choice($"恢复启动时的节点 · {saved["type"]}", saved));
        string cache = Path.Combine(Path.GetDirectoryName(configPath)!, "subscription.json");
        try
        {
            if (File.Exists(cache) && new FileInfo(cache).Length <= 12 * 1024 * 1024)
            {
                var savedNodes = JsonNode.Parse(File.ReadAllText(cache))?["nodes"];
                if (savedNodes != null) Add(Subscription.Parse(savedNodes.ToJsonString(), cached: true));
            }
        }
        catch (Exception) { status.Text = "本机节点缓存无法读取。你可以导入本地文件。"; }
        if (choices.Items.Count <= 1)
        {
            if (automatic) Add(Subscription.Parse(saved["members"]!.ToJsonString(), cached: true));
            else status.Text = "从本地配置文件添加其他节点。";
        }

        import.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "订阅或节点 (*.yaml;*.yml;*.json;*.txt)|*.yaml;*.yml;*.json;*.txt|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > Subscription.MaxBytes) throw new IOException("订阅文件不能超过 4 MB。");
                Add(Subscription.Parse(File.ReadAllText(dialog.FileName)));
                status.Text = "已读取文件中的可用节点。";
            }
            catch (IOException ex) { status.Text = ex.Message; }
            catch (Exception) { status.Text = "无法读取节点文件。"; }
        };
    }
}
