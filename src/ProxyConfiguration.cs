using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

// Only the node is editable. Routing, DNS and listeners belong to the environment.
static class ProxyConfiguration
{
    public static readonly string[] Protocols = ["hysteria2", "vless", "vmess", "trojan", "ss", "tuic", "anytls"];
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void ValidateRuntimePolicy(string path)
    {
        string text = File.ReadAllText(path);
        var actual = JsonNode.Parse(text)!.AsObject();
        var expected = Build(ReadNode(text, out int port), port);
        foreach (string key in new[] { "mode", "rules", "tun", "external-controller", "bind-address", "allow-lan", "ipv6" })
            if (!JsonNode.DeepEquals(actual[key], expected[key])) throw new IOException($"独立代理的 {key} 不符合固定代理规则，请在“独立代理配置”中重新校验并保存。");
        foreach (string key in new[] { "enable", "listen", "nameserver", "respect-rules", "ipv6" })
            if (!JsonNode.DeepEquals(actual["dns"]?[key], expected["dns"]?[key])) throw new IOException($"专用 DNS 的 {key} 配置不符合要求，请重新校验并保存。");
        if (actual["proxies"]?[0]?["name"]?.GetValue<string>() != "RegionExit") throw new IOException("固定出口节点名称不正确，请重新校验并保存。");
    }

    public static JsonObject ReadNode(string text, out int port)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new IOException("请选择 JSON 节点或独立代理配置文件。");
        port = 17890;
        if (root["proxies"] is JsonArray nodes)
        {
            if (nodes.Count != 1 || nodes[0] is not JsonObject) throw new IOException("请选择只含一个节点的 JSON 文件。");
            if (root["mixed-port"] != null) port = root["mixed-port"]!.GetValue<int>();
            root = (JsonObject)nodes[0]!.DeepClone();
        }
        return root;
    }

    public static JsonObject Build(JsonObject input, int port)
    {
        var node = (JsonObject)input.DeepClone();
        string type = node["type"]?.GetValue<string>() ?? "";
        if (!Protocols.Contains(type)) throw new IOException("请选择支持的代理协议。");
        string server = node["server"]?.GetValue<string>()?.Trim() ?? "";
        if (server.Length == 0 || Uri.CheckHostName(server) == UriHostNameType.Unknown || server.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请填写有效的远程服务器 IP 或域名。");
        if (IPAddress.TryParse(server, out var ip) && (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)))
            throw new IOException("服务器需要远程 IPv4 地址或域名，不能使用本机回环地址。");
        if (node["port"]?.GetValue<int>() is not int remotePort || remotePort < 1 || remotePort > 65535)
            throw new IOException("服务器端口必须在 1 到 65535 之间。");
        if (port < 1024 || port > 65535 || port is 7890 or 34010 or 34011)
            throw new IOException("本地端口需在 1024 到 65535 之间，且不能占用 7890、34010、34011。");
        foreach (string key in new[] { "dialer-proxy", "interface-name", "routing-mark" })
            if (node.ContainsKey(key)) throw new IOException($"节点不能包含 {key}；请使用独立的远程节点。");
        if (node["ip-version"] is not null && node["ip-version"]!.GetValue<string>() != "ipv4")
            throw new IOException("节点 IP 版本只能选择 ipv4。");
        bool tokenTuic = type == "tuic" && !string.IsNullOrWhiteSpace(node["token"]?.GetValue<string>());
        if (type is "vless" or "vmess" || type == "tuic" && !tokenTuic)
            if (!Guid.TryParse(node["uuid"]?.GetValue<string>(), out _)) throw new IOException("请填写有效的 UUID。");
        if (type is "hysteria2" or "trojan" or "ss" or "anytls" || type == "tuic" && !tokenTuic)
            if (string.IsNullOrEmpty(node["password"]?.GetValue<string>())) throw new IOException("请填写节点密码。");
        if (type == "ss" && string.IsNullOrWhiteSpace(node["cipher"]?.GetValue<string>())) throw new IOException("请选择 Shadowsocks 加密方式。");
        node["name"] = "RegionExit"; node["server"] = server;
        // Pin outbound resolution to IPv4 without changing the host adapter.
        node["ip-version"] = "ipv4";
        var config = JsonNode.Parse("""
        {
          "mixed-port":17890,"bind-address":"127.0.0.1","allow-lan":false,
          "mode":"rule","ipv6":false,"log-level":"warning","external-controller":"",
          "tun":{"enable":false},"profile":{"store-selected":false,"store-fake-ip":false},
          "dns":{"enable":true,"listen":"127.0.0.153:53","ipv6":false,"enhanced-mode":"redir-host",
            "use-hosts":false,"use-system-hosts":false,"respect-rules":true,
            "default-nameserver":["1.1.1.1"],"proxy-server-nameserver":["https://1.1.1.1/dns-query"],
            "nameserver":["https://1.1.1.1/dns-query#RegionExit"]},
          "rules":["MATCH,RegionExit"]
        }
        """)!.AsObject();
        config["mixed-port"] = port; config["proxies"] = new JsonArray(node);
        return config;
    }

    public static async Task ValidateAndSave(JsonObject config, string path, string engine, bool save)
    {
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "validate-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, config.ToJsonString(JsonOptions));
            var psi = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-t", "-d", folder, "-f", temporary }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new IOException("无法启动代理内核进行校验。");
            // Consume output, but never put server credentials into UI logs.
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("配置校验超时，原配置保持不变。"); }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new IOException("代理内核未通过校验，请检查协议、必填项和高级选项。原配置保持不变。");
            if (save)
            {
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
