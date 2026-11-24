using System.Text.Json.Nodes;

sealed class ProxySwitchDialog : Form
{
    internal sealed record Choice(string Name, JsonObject? Node, bool IsCurrent = false, int? DelayMs = null, bool Measured = false)
    {
        public override string ToString() => Name + (Measured ? DelayMs is int ms ? $"  ·  {ms} ms" : "  ·  不可用" : "");
    }

    internal static JsonObject NormalizedNode(JsonObject node)
    {
        var normalized = (JsonObject)ProxyConfiguration.Build(node, 17890)["proxies"]![0]!.DeepClone();
        return normalized;
    }
    internal static bool SameNode(JsonObject? left, JsonObject? right) =>
        left == null || right == null ? left == right : JsonNode.DeepEquals(NormalizedNode(left), NormalizedNode(right));

    readonly ModernList choices = new() { Dock = DockStyle.Fill, AccessibleName = "可切换的节点", EmptyText = "没有可切换的节点", EmptyHint = "可从文件添加，或在订阅管理中获取" };
    readonly AntdUI.Select sources = HomeTheme.Select();
    readonly Label status = HomeTheme.Hint("");
    readonly AntdUI.Button apply = HomeTheme.Action("立即切换", AntdUI.TTypeMini.Primary, "SwapOutlined");
    public JsonObject? SelectedNode => (choices.SelectedItem as Choice)?.Node;
    public string SelectedName => (choices.SelectedItem as Choice)?.Name ?? "";

    public ProxySwitchDialog(string configPath, JsonObject? active = null,
        Func<IReadOnlyList<JsonObject>, IProgress<(int Index, int? DelayMs)>, CancellationToken, Task>? measure = null)
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
        var latency = HomeTheme.Action("测延迟"); latency.Enabled = measure != null;
        CancellationTokenSource? measuring = null;
        var measured = new Dictionary<string, int?>();
        var buttons = HomeTheme.Footer(cancel, apply, import, latency); buttons.Dock = DockStyle.Bottom;
        Controls.Add(layout); Controls.Add(buttons);
        AcceptButton = apply; CancelButton = cancel;
        choices.SelectedIndexChanged += (_, _) => apply.Enabled = measuring == null && choices.SelectedItem is Choice { IsCurrent: false };

        var saved = ProxyConfiguration.ReadNode(File.ReadAllText(configPath), out _);
        bool automatic = ProxyConfiguration.IsFailover(saved);
        if (active == null && !automatic) active = saved;
        void AddChoice(Choice entry)
        {
            bool current = SameNode(entry.Node, active);
            int? delay = null;
            bool hasDelay = entry.Node != null && measured.TryGetValue(entry.Node.ToJsonString(), out delay);
            choices.Items.Add(entry with { IsCurrent = current, Measured = hasDelay, DelayMs = delay });
        }
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
            AddChoice(restore);
            try
            {
                var source = sources.SelectedValue as SavedSubscription;
                var entries = source?.ReadNodes().Nodes ?? fromFile;
                if (source == null && automatic)
                    entries = entries.Concat(Subscription.Parse(saved["members"]!.ToJsonString(), cached: true).Nodes).ToList();
                foreach (var entry in entries.Where(entry => !ProxyConfiguration.IsFailover(entry.Node))
                    .DistinctBy(entry => entry.Node.ToJsonString()))
                    AddChoice(new Choice(entry.ToString(), entry.Node));
                if (active != null && !choices.Items.Cast<Choice>().Any(c => c.IsCurrent))
                    AddChoice(new Choice($"当前节点 · {active["name"] ?? active["type"]}", active));
                status.Text = unreadable ? "订阅缓存无法读取，可以从本地文件临时选择。" :
                    "运行中可切换固定节点；要启用其他自动组，请在独立代理配置中保存。";
            }
            catch { status.Text = "该订阅的缓存无法读取，请在订阅管理中重新获取。"; }
            finally {
                var rows = choices.Items.Cast<Choice>().ToList();
                int currentRow = rows.FindIndex(1, choice => choice.IsCurrent);
                choices.SelectedIndex = currentRow >= 0 ? currentRow : rows.FindIndex(choice => choice.IsCurrent);
                choices.EndUpdate();
            }
        }
        sources.SelectedIndexChanged += (_, _) => Populate();
        var last = library.Items.FirstOrDefault(x => x.Id == library.LastUsed);
        sources.SelectedIndex = last == null ? 0 : sources.Items.IndexOf(last);
        Populate();

        FormClosing += (_, _) => measuring?.Cancel();
        latency.Click += async (_, _) =>
        {
            if (measure == null || measuring != null) return;
            var rows = choices.Items.Cast<Choice>().Select((choice, index) => (Choice: choice, Index: index)).Where(row => row.Choice.Node != null).ToArray();
            if (rows.Length == 0) return;
            using var tokenSource = new CancellationTokenSource();
            measuring = tokenSource;
            latency.Enabled = import.Enabled = sources.Enabled = apply.Enabled = false;
            status.Text = "正在测量当前列表的节点…";
            try {
                var progress = new Progress<(int Index, int? DelayMs)>(result => {
                    if (IsDisposed || tokenSource.IsCancellationRequested) return;
                    var row = rows[result.Index];
                    measured[row.Choice.Node!.ToJsonString()] = result.DelayMs;
                    int selected = choices.SelectedIndex;
                    choices.Items[row.Index] = row.Choice with { Measured = true, DelayMs = result.DelayMs };
                    choices.SelectedIndex = selected;
                });
                await measure(rows.Select(row => row.Choice.Node!).ToArray(), progress, tokenSource.Token);
                if (!IsDisposed) status.Text = "节点测速完成。";
            } catch (OperationCanceledException) when (tokenSource.IsCancellationRequested) { }
            catch { if (!IsDisposed) status.Text = "无法启动测速，请重试。"; }
            finally {
                measuring = null;
                if (!IsDisposed) {
                    latency.Enabled = import.Enabled = sources.Enabled = true;
                    apply.Enabled = choices.SelectedItem is Choice { IsCurrent: false };
                }
            }
        };

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
