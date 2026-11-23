using System.Text.Json.Nodes;

sealed class SubscriptionDialog : Form
{
    readonly ComboBox sources = new() { AccessibleName = "已保存的订阅", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox name = new() { AccessibleName = "订阅名称", Dock = DockStyle.Fill, PlaceholderText = "订阅名称（可选）", MaxLength = 60 };
    readonly TextBox address = new() { AccessibleName = "订阅链接", Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "粘贴机场的 HTTPS 订阅链接" };
    readonly CheckBox viaProxy = new() { Text = "通过本机代理下载（127.0.0.1:7890）", Checked = true, AutoSize = true };
    readonly Button fetch = new() { Text = "获取 / 更新节点", AutoSize = true };
    readonly Button remove = new() { Text = "删除订阅…", AutoSize = true, Enabled = false };
    readonly TextBox filter = new() { AccessibleName = "筛选节点", Dock = DockStyle.Fill, PlaceholderText = "筛选节点名称或协议，例如：美国、US、hysteria2" };
    readonly ListBox nodes = new() { AccessibleName = "订阅节点列表", Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    readonly Label status = new() { AutoSize = true, MaximumSize = new(650, 0), Dock = DockStyle.Fill };
    readonly Button use = new() { Text = "使用所选项", AutoSize = true, Enabled = false };
    readonly SubscriptionLibrary library;
    readonly bool fileImport;
    List<SubscriptionNode> all = [];
    CancellationTokenSource? download;
    bool loadingSources, cacheReadable = true;
    public JsonObject? SelectedNode { get; private set; }

    public SubscriptionDialog(string privateFolder, string? fileText = null)
    {
        library = new(privateFolder); fileImport = fileText != null;
        Text = fileImport ? "导入文件与选节点" : "订阅管理与选节点";
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(760, 650); MinimumSize = new(680, 560); MinimizeBox = false;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 9 };
        for (int i = 0; i < 6; i++) grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.AutoSize)); grid.RowStyles.Add(new(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "选择已有订阅，或选“＋ 新增订阅”后填写链接。\n获取成功即保存该订阅；选择节点或组后，还需要在代理配置中校验并保存。", AutoSize = true, Padding = new(0, 0, 0, 8) }, 0, 0);
        var sourceBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        sourceBar.ColumnStyles.Add(new(SizeType.Percent, 100));
        sourceBar.ColumnStyles.Add(new(SizeType.AutoSize));
        sourceBar.Controls.Add(sources, 0, 0); sourceBar.Controls.Add(remove, 1, 0);
        grid.Controls.Add(sourceBar, 0, 1);
        grid.Controls.Add(name, 0, 2); grid.Controls.Add(address, 0, 3);
        var downloadBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        downloadBar.Controls.AddRange([viaProxy, fetch]); grid.Controls.Add(downloadBar, 0, 4);
        filter.Margin = new(3, 12, 3, 8); grid.Controls.Add(filter, 0, 5); grid.Controls.Add(nodes, 0, 6);
        status.Padding = new(0, 10, 0, 10); grid.Controls.Add(status, 0, 7);
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; actions.Controls.AddRange([use, cancel]); grid.Controls.Add(actions, 0, 8);
        Controls.Add(grid); CancelButton = cancel;
        filter.TextChanged += (_, _) => Filter();
        nodes.SelectedIndexChanged += (_, _) => use.Enabled = download == null && nodes.SelectedItem is SubscriptionNode;
        sources.SelectedIndexChanged += (_, _) => { if (!loadingSources) OpenSource(); };
        fetch.Click += async (_, _) => await Fetch();
        remove.Click += (_, _) => RemoveSource();
        use.Click += (_, _) => Choose();
        FormClosing += (_, _) => download?.Cancel();
        foreach (Control control in new Control[] { sourceBar, name, address, downloadBar }) control.Visible = !fileImport;
        try { library.Load(); }
        catch { cacheReadable = false; }
        if (fileText != null) { SetNodes(Subscription.Parse(fileText)); return; }

        ReloadSources(library.LastUsed);
        OpenSource();
        if (!cacheReadable)
        {
            fetch.Enabled = false;
            status.Text = "已有订阅缓存无法读取。原缓存已保留；仍可单独导入本地文件。";
        }
    }

    void ReloadSources(Guid? selected)
    {
        loadingSources = true;
        try
        {
            sources.Items.Clear(); sources.Items.Add("＋ 新增订阅");
            sources.Items.AddRange(library.Items.Cast<object>().ToArray());
            sources.SelectedItem = library.Items.FirstOrDefault(x => x.Id == selected);
            if (sources.SelectedIndex < 0) sources.SelectedIndex = 0;
        }
        finally { loadingSources = false; }
        remove.Enabled = sources.SelectedItem is SavedSubscription;
    }

    void OpenSource()
    {
        var item = sources.SelectedItem as SavedSubscription;
        remove.Enabled = cacheReadable && item != null;
        name.Text = item?.Name ?? ""; address.Text = item?.Url ?? ""; viaProxy.Checked = item?.ViaProxy ?? true;
        SetNodes(new([], 0));
        if (item == null) { status.Text = "输入链接后点击“获取 / 更新节点”。"; return; }
        try
        {
            SetNodes(item.ReadNodes());
            if (item.Selected >= 0 && item.Selected < all.Count) nodes.SelectedItem = all[item.Selected];
            status.Text = $"已从该订阅的缓存载入 {all.Count} 个可选项。";
        }
        catch { status.Text = "该订阅的节点缓存无法读取，可以重新获取。"; }
    }

    void RemoveSource()
    {
        if (sources.SelectedItem is not SavedSubscription item ||
            MessageBox.Show(this, "删除这份订阅和缓存？当前代理配置不会改变。", "删除订阅", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { library.Remove(item.Id); ReloadSources(library.LastUsed); OpenSource(); }
        catch { status.Text = "无法删除订阅。原缓存未改变。"; }
    }

    internal void SetNodes(SubscriptionResult result)
    {
        all = result.Nodes; filter.Clear(); Filter();
        status.Text = $"已解析 {all.Count} 个可选项，跳过 {result.Skipped} 个不支持或无效项。保存前会由内核校验。";
    }

    void Filter()
    {
        var selected = nodes.SelectedItem;
        nodes.BeginUpdate(); nodes.Items.Clear();
        foreach (var node in all) if (node.ToString().Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)) nodes.Items.Add(node);
        if (selected != null && nodes.Items.Contains(selected)) nodes.SelectedItem = selected;
        nodes.EndUpdate(); use.Enabled = download == null && nodes.SelectedItem is SubscriptionNode;
    }

    // Persist only a successful result; a failed refresh leaves the previous cache and selection intact.
    internal void SaveFetched(string requested, bool requestedViaProxy, SubscriptionResult result)
    {
        if (result.Nodes.Count == 0) throw new IOException("这份订阅没有可用节点，原缓存未改变。");
        var item = sources.SelectedItem as SavedSubscription;
        var old = nodes.SelectedItem as SubscriptionNode;
        int selected = old == null ? -1 : result.Nodes.FindIndex(n => JsonNode.DeepEquals(n.Node, old.Node));
        var saved = library.Save(item?.Id, name.Text, requested, requestedViaProxy, selected, result.Nodes);
        ReloadSources(saved.Id);
        name.Text = saved.Name;
        SetNodes(result);
        if (selected >= 0) nodes.SelectedItem = all[selected];
        status.Text = $"订阅已保存：{all.Count} 个可选项，跳过 {result.Skipped} 项。";
    }

    async Task Fetch()
    {
        if (download != null || !cacheReadable) return;
        using var cancellation = new CancellationTokenSource(); download = cancellation;
        sources.Enabled = name.Enabled = remove.Enabled = fetch.Enabled = address.Enabled = viaProxy.Enabled = use.Enabled = false;
        status.Text = "正在下载并解析订阅…";
        string requested = address.Text.Trim(); bool requestedViaProxy = viaProxy.Checked;
        try
        {
            string text = await Subscription.Download(requested, requestedViaProxy, cancellation.Token);
            var result = await Task.Run(() => Subscription.Parse(text), cancellation.Token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            SaveFetched(requested, requestedViaProxy, result);
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "已取消下载。"; }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex.GetType() == typeof(IOException) ? ex.Message : "无法更新订阅。请检查链接或订阅缓存权限。"; }
        finally
        {
            download = null;
            if (!IsDisposed) {
                sources.Enabled = name.Enabled = fetch.Enabled = address.Enabled = viaProxy.Enabled = true;
                remove.Enabled = sources.SelectedItem is SavedSubscription;
                use.Enabled = nodes.SelectedItem is SubscriptionNode;
            }
        }
    }

    void Choose()
    {
        if (nodes.SelectedItem is not SubscriptionNode selected) return;
        try
        {
            var item = sources.SelectedItem as SavedSubscription;
            if (cacheReadable)
                library.Save(item?.Id, name.Text, item?.Url ?? "", item?.ViaProxy ?? viaProxy.Checked, all.IndexOf(selected), all);
            SelectedNode = (JsonObject)selected.Node.DeepClone(); DialogResult = DialogResult.OK; Close();
        }
        catch { status.Text = "无法保存本机订阅缓存，请检查目录权限；当前代理配置未修改。"; }
    }
}
