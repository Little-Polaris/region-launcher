using System.Text.Json.Nodes;

sealed class ProxySettingsDialog : Form
{
    readonly ComboBox protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox server = new() { Dock = DockStyle.Fill };
    readonly NumericUpDown remotePort = new() { Minimum = 1, Maximum = 65535, Value = 443, Dock = DockStyle.Fill };
    readonly NumericUpDown localPort = new() { Minimum = 1024, Maximum = 65535, Value = 17890, Dock = DockStyle.Fill };
    readonly TextBox uuid = new() { Dock = DockStyle.Fill };
    readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    readonly TextBox sni = new() { Dock = DockStyle.Fill };
    readonly ComboBox cipher = new() { Dock = DockStyle.Fill };
    readonly CheckBox tls = new() { Text = "启用 TLS", AutoSize = true, Checked = true };
    readonly CheckBox skipVerify = new() { Text = "跳过证书验证（仅节点明确要求时使用）", AutoSize = true };
    readonly TextBox advanced = new() { Multiline = true, ScrollBars = ScrollBars.Both, AcceptsTab = true, WordWrap = false, Dock = DockStyle.Fill, Text = "{}" };
    readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new(620, 0) };
    readonly Button save = new() { Text = "校验并保存", AutoSize = true };
    readonly Button validate = new() { Text = "校验配置", AutoSize = true };
    readonly Button import = new() { Text = "导入文件…", AutoSize = true };
    readonly Button subscription = new() { Text = "订阅 / 选节点或组…", AutoSize = true };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly Dictionary<Control, Label> fieldLabels = new();
    readonly DedicatedProxy proxy;
    bool working;
    bool loading;
    JsonObject? failover;
    string Type => ProxyConfiguration.Protocols[protocol.SelectedIndex];
    static readonly string[] FormKeys = ["name", "type", "server", "port", "uuid", "password", "sni", "servername", "cipher", "tls", "skip-cert-verify"];

    public ProxySettingsDialog(DedicatedProxy proxy)
    {
        this.proxy = proxy;
        Text = "独立代理配置"; Font = new("Microsoft YaHei UI", 10);
        ClientSize = new(760, 760); MinimumSize = new(680, 690); StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "选择固定节点，或导入自动故障切换组作为环境的独立出口。\n保存后下次启动生效；节点凭据仅保存在本机，请勿分享配置文件。", AutoSize = true, Padding = new(0, 0, 0, 12) }, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
        var basic = new TabPage("节点设置"); var extra = new TabPage("高级选项"); tabs.TabPages.AddRange([basic, extra]);
        tabs.Selecting += (_, e) => { if (failover != null && e.TabPage == extra) e.Cancel = true; };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), ColumnCount = 2, AutoScroll = true };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 165)); grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string label, Control control)
        {
            int row = grid.RowCount++; grid.RowStyles.Add(new(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Padding = new(0, 7, 0, 7) };
            fieldLabels.Add(control, caption); grid.Controls.Add(caption, 0, row);
            control.Margin = new(3, 5, 3, 5); grid.Controls.Add(control, 1, row); control.AccessibleName = label;
        }
        protocol.Items.AddRange(["Hysteria2", "VLESS", "VMess", "Trojan", "Shadowsocks", "TUIC", "AnyTLS"]);
        cipher.Items.AddRange(["aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305"]);
        cipher.Text = "aes-128-gcm";
        Row("代理出口", protocol); Row("服务器地址", server); Row("服务器端口", remotePort);
        Row("UUID", uuid); Row("密码", password); Row("加密方式", cipher);
        Row("TLS", tls); Row("服务器名 (SNI)", sni); Row("证书", skipVerify); Row("独立本地端口", localPort);
        basic.Controls.Add(grid);
        var advancedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), RowCount = 2, ColumnCount = 1 };
        advancedLayout.RowStyles.Add(new(SizeType.AutoSize)); advancedLayout.RowStyles.Add(new(SizeType.Percent, 100));
        advancedLayout.Controls.Add(new Label { Text = "填写额外节点参数的 JSON 对象，例如 Reality、WebSocket、gRPC、ALPN、混淆或 TUIC token。\n基础字段请在“节点设置”中填写；不要粘贴整份路由或订阅配置。", AutoSize = true, MaximumSize = new(620, 0), Padding = new(0, 0, 0, 10) }, 0, 0);
        advancedLayout.Controls.Add(advanced, 0, 1); extra.Controls.Add(advancedLayout);
        status.Padding = new(0, 8, 0, 8); layout.Controls.Add(status, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([subscription, import, validate, save, cancel]); layout.Controls.Add(actions, 0, 3); Controls.Add(layout);
        CancelButton = cancel;
        protocol.SelectedIndexChanged += (_, _) =>
        {
            if (loading || protocol.SelectedIndex < 0) return;
            if (failover != null)
            {
                failover = null;
                protocol.Items.RemoveAt(ProxyConfiguration.Protocols.Length);
                status.Text = "";
            }
            advanced.Text = "{}"; uuid.Clear(); password.Clear(); cipher.Text = Type == "vmess" ? "auto" : "aes-128-gcm"; tls.Checked = true; skipVerify.Checked = false;
            UpdateFields();
        };
        protocol.SelectedIndex = 0;
        import.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "订阅或节点 (*.yaml;*.yml;*.json;*.txt)|*.yaml;*.yml;*.json;*.txt|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try {
                if (new FileInfo(dialog.FileName).Length > Subscription.MaxBytes) throw new IOException("订阅文件不能超过 4 MB。");
                SelectSubscription(File.ReadAllText(dialog.FileName));
            }
            catch (Exception ex) { status.Text = ex is IOException ? ex.Message : "导入失败，请检查订阅格式；原配置未修改。"; }
        };
        subscription.Click += (_, _) => SelectSubscription();
        validate.Click += async (_, _) => await Apply(false);
        save.Click += async (_, _) => await Apply(true);
        FormClosing += (_, e) => { if (working) e.Cancel = true; };
        try { if (proxy.Configured) LoadText(File.ReadAllText(proxy.ConfigPath)); }
        catch { status.Text = "已有配置无法读取，可以重新填写或导入；保存前不会覆盖它。"; }
    }

    void SelectSubscription(string? text = null)
    {
        using var selector = new SubscriptionDialog(Path.GetDirectoryName(proxy.ConfigPath)!, text);
        if (selector.ShowDialog(this) != DialogResult.OK || selector.SelectedNode == null) return;
        var selected = selector.SelectedNode;
        // The provider's inbound port never replaces the launcher's dedicated port.
        try
        {
            LoadText(ProxyConfiguration.Build(selected, (int)localPort.Value).ToJsonString());
            if (failover == null) status.Text = $"已选择 {selected["name"]}。校验并保存后生效。";
        }
        catch { status.Text = "无法载入所选配置，请检查参数和本地端口。原配置未修改。"; }
    }

    void UpdateFields()
    {
        foreach (var control in new Control[] { server, remotePort, uuid, password, cipher, sni, skipVerify, tls })
            control.Visible = fieldLabels[control].Visible = failover == null;
        if (failover != null) return;
        string type = Type;
        uuid.Enabled = type is "vless" or "vmess" or "tuic";
        password.Enabled = type is not ("vless" or "vmess");
        cipher.Enabled = type is "ss" or "vmess";
        tls.Enabled = type is "vless" or "vmess";
        if (!tls.Enabled) tls.Checked = type != "ss";
        sni.Enabled = skipVerify.Enabled = type != "ss";
        foreach (var control in new Control[] { uuid, password, cipher, sni, skipVerify })
            control.Visible = fieldLabels[control].Visible = control.Enabled;
        tls.Visible = fieldLabels[tls].Visible = type != "ss";
    }

    internal void LoadText(string text)
    {
        var node = ProxyConfiguration.ReadNode(text, out int port);
        bool grouped = ProxyConfiguration.IsFailover(node);
        int index = grouped ? ProxyConfiguration.Protocols.Length : Array.IndexOf(ProxyConfiguration.Protocols, node["type"]?.GetValue<string>());
        if (index < 0 || port < localPort.Minimum || port > localPort.Maximum) throw new IOException("不支持的节点或本地端口。");
        loading = true;
        try
        {
            if (protocol.Items.Count > ProxyConfiguration.Protocols.Length) protocol.Items.RemoveAt(ProxyConfiguration.Protocols.Length);
            failover = grouped ? node : null;
            localPort.Value = port;
            if (grouped)
            {
                protocol.Items.Add($"{node["name"]} · 自动故障切换（{node["members"]!.AsArray().Count} 个节点）");
                protocol.SelectedIndex = index;
                tabs.SelectedIndex = 0;
                server.Clear(); uuid.Clear(); password.Clear(); advanced.Text = "{}";
                status.Text = "Mihomo 按导入顺序选择可用节点。校验并保存后生效。";
                UpdateFields();
                return;
            }
            protocol.SelectedIndex = index; server.Text = node["server"]?.GetValue<string>() ?? "";
            remotePort.Value = node["port"]?.GetValue<int>() ?? 443;
            uuid.Text = node["uuid"]?.GetValue<string>() ?? ""; password.Text = node["password"]?.GetValue<string>() ?? "";
            sni.Text = (node["sni"] ?? node["servername"])?.GetValue<string>() ?? "";
            cipher.Text = node["cipher"]?.GetValue<string>() ?? (Type == "vmess" ? "auto" : "aes-128-gcm");
            tls.Checked = node["tls"]?.GetValue<bool>() ?? false; skipVerify.Checked = node["skip-cert-verify"]?.GetValue<bool>() ?? false;
            foreach (string key in FormKeys) node.Remove(key);
            advanced.Text = node.ToJsonString(ProxyConfiguration.JsonOptions); UpdateFields();
        }
        finally { loading = false; }
    }

    internal JsonObject ReadConfig()
    {
        if (failover != null) return ProxyConfiguration.Build(failover, (int)localPort.Value);
        JsonObject node;
        try { node = JsonNode.Parse(advanced.Text) as JsonObject ?? throw new IOException(); }
        catch { throw new IOException("高级选项必须是 JSON 对象，例如 {}。"); }
        if (FormKeys.Any(node.ContainsKey)) throw new IOException("高级选项含有基础字段，请移到“节点设置”中填写。");
        string type = Type;
        node["type"] = type; node["server"] = server.Text.Trim(); node["port"] = (int)remotePort.Value;
        if (uuid.Enabled && uuid.Text.Length > 0) node["uuid"] = uuid.Text.Trim();
        if (password.Enabled && password.Text.Length > 0) node["password"] = password.Text;
        if (cipher.Enabled) node["cipher"] = cipher.Text.Trim();
        if (tls.Enabled) node["tls"] = tls.Checked;
        if (sni.Enabled && sni.Text.Trim().Length > 0) node[type is "vless" or "vmess" ? "servername" : "sni"] = sni.Text.Trim();
        if (skipVerify.Enabled) node["skip-cert-verify"] = skipVerify.Checked;
        return ProxyConfiguration.Build(node, (int)localPort.Value);
    }

    async Task Apply(bool commit)
    {
        bool saved = false;
        working = true;
        status.Text = "正在校验…";
        try
        {
            var config = ReadConfig();
            tabs.Enabled = subscription.Enabled = import.Enabled = validate.Enabled = save.Enabled = false;
            await ProxyConfiguration.ValidateAndSave(config, proxy.ConfigPath, proxy.EnginePath, commit);
            if (commit) { proxy.Dispose(); saved = true; }
            else status.Text = "配置校验通过；尚未连接服务器。保存后可在主窗口检测代理。";
        }
        catch (Exception ex) { status.Text = ex is IOException ? ex.Message : "校验或保存失败，未应用新配置；请检查字段、文件权限和代理内核。"; }
        finally { working = false; tabs.Enabled = subscription.Enabled = import.Enabled = validate.Enabled = save.Enabled = true; }
        if (saved) { DialogResult = DialogResult.OK; Close(); }
    }
}
