using System.Text.Json;
using System.Text.Json.Nodes;

sealed class SubscriptionDialog : Form
{
    readonly TextBox address = new() { AccessibleName = "订阅链接", Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "粘贴机场的 HTTPS 订阅链接" };
    readonly CheckBox viaProxy = new() { Text = "通过本机代理下载（127.0.0.1:7890）", Checked = true, AutoSize = true };
    readonly Button fetch = new() { Text = "获取 / 更新节点", AutoSize = true };
    readonly TextBox filter = new() { AccessibleName = "筛选节点", Dock = DockStyle.Fill, PlaceholderText = "筛选节点名称或协议，例如：美国、US、hysteria2" };
    readonly ListBox nodes = new() { AccessibleName = "订阅节点列表", Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    readonly Label status = new() { AutoSize = true, MaximumSize = new(650, 0), Dock = DockStyle.Fill };
    readonly Button use = new() { Text = "使用所选节点", AutoSize = true, Enabled = false };
    readonly string cachePath;
    List<SubscriptionNode> all = [];
    CancellationTokenSource? download;
    string loadedAddress = "";
    bool loadedViaProxy = true;
    public JsonObject? SelectedNode { get; private set; }

    public SubscriptionDialog(string privateFolder, string? fileText = null)
    {
        cachePath = Path.Combine(privateFolder, "subscription.json");
        Text = "机场订阅与节点"; Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(760, 650); MinimumSize = new(680, 530); MinimizeBox = false;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 7 };
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.AutoSize)); grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "粘贴订阅链接后获取节点，选择一个作为环境的固定出口。\n再次打开可切换已保存的节点；更新订阅需要点击“获取 / 更新节点”。", AutoSize = true, Padding = new(0, 0, 0, 12) }, 0, 0);
        grid.Controls.Add(address, 0, 1);
        var downloadBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        downloadBar.Controls.AddRange([viaProxy, fetch]); grid.Controls.Add(downloadBar, 0, 2);
        filter.Margin = new(3, 12, 3, 8); grid.Controls.Add(filter, 0, 3); grid.Controls.Add(nodes, 0, 4);
        status.Padding = new(0, 10, 0, 10); grid.Controls.Add(status, 0, 5);
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; actions.Controls.AddRange([use, cancel]); grid.Controls.Add(actions, 0, 6);
        Controls.Add(grid); CancelButton = cancel;
        filter.TextChanged += (_, _) => Filter();
        nodes.SelectedIndexChanged += (_, _) => use.Enabled = download == null && nodes.SelectedItem is SubscriptionNode;
        fetch.Click += async (_, _) => await Fetch();
        use.Click += (_, _) => Choose();
        FormClosing += (_, _) => download?.Cancel();
        if (fileText != null) { SetNodes(Subscription.Parse(fileText)); return; }
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 12 * 1024 * 1024)
            {
                var cache = JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
                address.Text = loadedAddress = cache["url"]?.GetValue<string>() ?? "";
                viaProxy.Checked = loadedViaProxy = cache["viaProxy"]?.GetValue<bool>() ?? true;
                SetNodes(Subscription.Parse(cache["nodes"]!.ToJsonString()));
                int selected = cache["selected"]?.GetValue<int>() ?? -1;
                if (selected >= 0 && selected < all.Count) nodes.SelectedItem = all[selected];
                status.Text = $"已载入本机保存的 {all.Count} 个节点；未自动访问订阅。";
            }
        }
        catch { status.Text = "已有订阅缓存无法读取，请重新获取。当前代理配置未修改。"; }
    }

    internal void SetNodes(SubscriptionResult result)
    {
        all = result.Nodes; filter.Clear(); Filter();
        status.Text = $"已解析 {all.Count} 个可选节点，跳过 {result.Skipped} 个不支持或无效项。请选择节点；保存前仍会由内核校验。";
    }
    void Filter()
    {
        var selected = nodes.SelectedItem;
        nodes.BeginUpdate(); nodes.Items.Clear();
        foreach (var node in all) if (node.ToString().Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)) nodes.Items.Add(node);
        if (selected != null && nodes.Items.Contains(selected)) nodes.SelectedItem = selected;
        nodes.EndUpdate(); use.Enabled = download == null && nodes.SelectedItem is SubscriptionNode;
    }
    async Task Fetch()
    {
        if (download != null) return;
        using var cancellation = new CancellationTokenSource(); download = cancellation;
        fetch.Enabled = address.Enabled = viaProxy.Enabled = use.Enabled = false;
        status.Text = "正在下载并解析订阅…";
        string requested = address.Text.Trim(); bool requestedViaProxy = viaProxy.Checked;
        try
        {
            string text = await Subscription.Download(requested, requestedViaProxy, cancellation.Token);
            var result = await Task.Run(() => Subscription.Parse(text), cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            SetNodes(result); loadedAddress = requested; loadedViaProxy = requestedViaProxy;
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "已取消下载。"; }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex is IOException ? ex.Message : "订阅解析失败，请检查链接或改用 Clash/Mihomo 格式。"; }
        finally
        {
            download = null;
            if (!IsDisposed) { fetch.Enabled = address.Enabled = viaProxy.Enabled = true; use.Enabled = nodes.SelectedItem is SubscriptionNode; }
        }
    }
    void Choose()
    {
        if (nodes.SelectedItem is not SubscriptionNode selected) return;
        string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cache = new JsonObject { ["url"] = loadedAddress, ["viaProxy"] = loadedViaProxy, ["selected"] = all.IndexOf(selected), ["nodes"] = new JsonArray(all.Select(n => (JsonNode?)n.Node.DeepClone()).ToArray()) };
            File.WriteAllText(temporary, cache.ToJsonString()); File.Move(temporary, cachePath, true);
            SelectedNode = (JsonObject)selected.Node.DeepClone(); DialogResult = DialogResult.OK; Close();
        }
        catch { status.Text = "无法保存本机订阅缓存，请检查目录权限；当前代理配置未修改。"; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
