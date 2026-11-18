using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

static class SubscriptionTests
{
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    static async Task Reject(Func<Task> action, string label)
    { try { await action(); } catch (IOException) { return; } throw new Exception("Accepted " + label); }

    public static async Task Run(string engine, string folder)
    {
        var yaml = """
        # provider comment https://example.invalid
        mixed-port: 7890
        tun: {enable: true}
        rules: [MATCH,DIRECT]
        proxies:
          - name: US 一号
            type: hysteria2
            server: 192.0.2.1
            port: 443
            password: 001234
            skip-cert-verify: false
            alpn: [h3]
          - name: US 二号
            type: vless
            server: 192.0.2.2
            port: 443
            uuid: 78ac0fdc-9041-4b28-9c55-167886f88751
            tls: true
            network: ws
            ws-opts: {path: /websocket, headers: {Host: example.com}}
          - name: unsupported
            type: wireguard
        """;
        var result = Subscription.Parse(yaml);
        Assert(result.Nodes.Count == 2 && result.Skipped == 1, "YAML selection count");
        Assert(result.Nodes[0].Node["password"]!.GetValue<string>() == "001234", "Numeric credential lost");
        Assert(result.Nodes[1].Node["ws-opts"]!["headers"]!["Host"]!.GetValue<string>() == "example.com", "Nested options lost");
        var protectedConfig = ProxyConfiguration.Build(result.Nodes[0].Node, 17890);
        Assert(protectedConfig["rules"]![0]!.GetValue<string>() == "MATCH,RegionExit" && !protectedConfig["tun"]!["enable"]!.GetValue<bool>(), "Subscription changed host routing");
        var grouped = Subscription.Parse(yaml + "\n" + """
        proxy-groups:
          - {name: US Auto, type: fallback, proxies: [US 二号, US 一号], url: http://example.invalid}
          - {name: Direct fallback, type: fallback, proxies: [US 一号, DIRECT]}
          - {name: Nested fallback, type: fallback, proxies: [US 一号, US Auto]}
          - {name: Unsupported member, type: fallback, proxies: [US 一号, unsupported]}
          - {name: Provider fallback, type: fallback, proxies: [US 一号, US 二号], use: [provider]}
        """);
        Assert(grouped.Nodes.Count == 3 && grouped.Skipped == 5, "Unexpected failover group eligibility");
        var importedGroup = grouped.Nodes.Single(n => n.Name == "US Auto");
        var failover = ProxyConfiguration.Build(importedGroup.Node, 17911);
        var groupPath = Path.Combine(folder, "failover-check.json");
        await ProxyConfiguration.ValidateAndSave(failover, groupPath, engine, true);
        ProxyConfiguration.ValidateRuntimePolicy(groupPath);
        var runtime = (JsonObject)failover.DeepClone();
        ProxyConfiguration.PrepareRuntime(runtime);
        var resolvers = runtime["dns"]!["nameserver"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert(resolvers.SequenceEqual(["https://1.1.1.1/dns-query#RegionExit", "https://8.8.8.8/dns-query#RegionExit"]), "Failover DoH changed proxy routing");
        Assert(failover["dns"]!["nameserver"]!.AsArray().Count == 1, "Saved config was changed");
        await ProxyConfiguration.ValidateAndSave(runtime, Path.Combine(folder, "failover-runtime.json"), engine, true);
        using (var exit = new ProxyRuntimeExit(runtime, folder))
        {
            exit.WriteNode(null);
            await ProxyConfiguration.ValidateAndSave(runtime, Path.Combine(folder, "switchable-auto.json"), engine, false);
            Assert(runtime["rules"]![0]!.GetValue<string>() == "MATCH,RegionExit", "Hot switch changed the protected route");
            Assert(runtime["dns"]!["nameserver"]!.AsArray().Count == resolvers.Length, "Hot switch removed DNS redundancy");
        }
        var fixedRuntime = (JsonObject)protectedConfig.DeepClone();
        ProxyConfiguration.PrepareRuntime(fixedRuntime);
        Assert(fixedRuntime["dns"]!["nameserver"]!.AsArray().Count == 1, "Fixed-node configuration changed");
        var savedGroup = ProxyConfiguration.ReadNode(File.ReadAllText(groupPath), out int port);
        Assert(port == 17911 && JsonNode.DeepEquals(failover, ProxyConfiguration.Build(savedGroup, port)), "Group round-trip or local port changed");
        Assert(failover["proxies"]![0]!["server"]!.GetValue<string>() == "192.0.2.2", "Import order lost");
        Assert(failover["proxy-groups"]![0]!["proxies"]![0]!.GetValue<string>() == "RegionMember1", "Group order lost");
        Assert(!failover.ToJsonString().Contains("example.invalid"), "Subscription health check leaked into runtime policy");
        var invalidGroup = (JsonObject)failover.DeepClone();
        invalidGroup["proxy-groups"]![0]!["proxies"]![0] = "DIRECT";
        string invalidPath = Path.Combine(folder, "failover-rejected.json");
        File.WriteAllText(invalidPath, invalidGroup.ToJsonString());
        await Reject(() => Task.Run(() => ProxyConfiguration.ValidateRuntimePolicy(invalidPath)), "direct fallback in runtime");
        Console.WriteLine("PASS: US Auto order, live Mihomo config validation, and rejection of unsafe or unsupported group members");
        string id = "78ac0fdc-9041-4b28-9c55-167886f88751";
        var vmess = new JsonObject { ["v"]="2", ["ps"]="VMess 测试", ["add"]="192.0.2.1", ["port"]="443", ["id"]=id, ["aid"]="0", ["scy"]="auto", ["net"]="ws", ["type"]="none", ["host"]="example.com", ["path"]="/ws", ["tls"]="tls", ["sni"]="example.com" };
        string links = string.Join('\n',
            "hysteria2://fixture%3Apassword@192.0.2.1:443?sni=example.com&obfs=salamander&obfs-password=test#HY2",
            $"vless://{id}@192.0.2.1:443?security=tls&type=ws&host=example.com&path=%2Fws&sni=example.com#VLESS",
            "vmess://" + B64(vmess.ToJsonString()),
            "trojan://fixture@192.0.2.1:443?sni=example.com#Trojan",
            "ss://" + B64("aes-128-gcm:fixture-password") + "@192.0.2.1:443#SS",
            $"tuic://{id}:fixture@192.0.2.1:443?sni=example.com&congestion_control=bbr#TUIC",
            "anytls://fixture@192.0.2.1:443?sni=example.com#AnyTLS",
            "ssr://unsupported");
        var decoded = Subscription.Parse(B64(links));
        Assert(decoded.Nodes.Count == 7 && decoded.Skipped == 1, "Base64 or URI list count");
        Assert(decoded.Nodes[0].Node["password"]!.GetValue<string>() == "fixture:password", "URL decoding lost password");
        Assert(Subscription.Parse(links).Nodes.Count == 7, "Plain link list");
        foreach (var entry in result.Nodes.Concat(decoded.Nodes))
            await ProxyConfiguration.ValidateAndSave(ProxyConfiguration.Build(entry.Node, 17890), Path.Combine(folder, "subscription-check.json"), engine, false);
        Assert(Subscription.Parse("{\"proxies\":" + new JsonArray(result.Nodes.Select(n => (JsonNode?)n.Node.DeepClone()).ToArray()).ToJsonString() + "}").Nodes.Count == 2, "JSON nodes");
        Assert(Subscription.Parse("ss://" + B64("aes-128-gcm:fixture@192.0.2.1:443") + "#Legacy").Nodes.Count == 1, "Legacy SS");
        await Reject(() => Task.Run(() => Subscription.Parse(new string('x', Subscription.MaxBytes + 1))), "large subscription");
        await Reject(() => Task.Run(() => Subscription.Parse("<html>login</html>")), "HTML login");
        await Reject(() => Task.Run(() => Subscription.Parse("proxy-providers: {secret: {url: https://example.invalid/token}}")), "external provider recursion");
        await Reject(() => Task.Run(() => Subscription.Parse("proxies: &cycle [*cycle]")), "cyclic YAML");
        await Reject(() => Task.Run(() => Subscription.Parse($"vless://{id}@192.0.2.1:443?type=xhttp")), "unknown transport");

        var response = new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(yaml) }));
        string downloaded = await Subscription.Download("https://example.invalid/sub?token=PRIVATE", false, CancellationToken.None, response);
        Assert(Subscription.Parse(downloaded).Nodes.Count == 2 && response.Calls == 1, "Download flow");
        var error = new Stub((_, _) => throw new HttpRequestException("https://example.invalid/PRIVATE"));
        try { await Subscription.Download("https://example.invalid/PRIVATE", false, CancellationToken.None, error); throw new Exception("Failure swallowed"); }
        catch (IOException ex) { Assert(!ex.Message.Contains("PRIVATE"), "Credential in error"); }
        var redirect = new Stub((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("http://example.invalid/PRIVATE"); return Task.FromResult(r); });
        await Reject(() => Subscription.Download("https://example.invalid/sub", false, CancellationToken.None, redirect), "HTTPS downgrade");
        var large = new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Subscription.MaxBytes + 1]) }));
        await Reject(() => Subscription.Download("https://example.invalid/sub", false, CancellationToken.None, large), "large response");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = new Stub(async (_, token) => { await Task.Delay(1000, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        try { await Subscription.Download("https://example.invalid/sub", false, cancel.Token, cancelled); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: multi-node YAML/JSON/Base64/URI, seven protocols via Mihomo, bounded download, cancellation, redirect and credential-safe errors");
    }

    public static void Selection(string folder)
    {
        var nodes = Subscription.Parse("""
        proxies:
          - {name: US first, type: hysteria2, server: 192.0.2.1, port: 443, password: fixture}
          - {name: JP second, type: trojan, server: 192.0.2.2, port: 443, password: fixture}
        proxy-groups:
          - {name: US Auto, type: fallback, proxies: [US first, JP second]}
        """);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var form = new SubscriptionDialog(folder))
        {
            form.SetNodes(nodes);
            var filter = (TextBox)typeof(SubscriptionDialog).GetField("filter", flags)!.GetValue(form)!;
            var list = (ListBox)typeof(SubscriptionDialog).GetField("nodes", flags)!.GetValue(form)!;
            filter.Text = "Auto"; Assert(list.Items.Count == 1, "Group search result"); list.SelectedIndex = 0;
            typeof(SubscriptionDialog).GetMethod("Choose", flags)!.Invoke(form, null);
            var config = ProxyConfiguration.Build(form.SelectedNode!, 17911);
            using var proxy = new DedicatedProxy(configPath: Path.Combine(folder, "nonexistent.json"));
            using var settings = new ProxySettingsDialog(proxy);
            settings.LoadText(config.ToJsonString());
            Assert(JsonNode.DeepEquals(settings.ReadConfig(), config), "Settings cannot show or save the group");
            var protocol = (ComboBox)typeof(ProxySettingsDialog).GetField("protocol", flags)!.GetValue(settings)!;
            Assert(protocol.SelectedItem!.ToString()!.Contains("US Auto"), "Missing group name in settings");
            protocol.SelectedIndex = 0;
            settings.LoadText(ProxyConfiguration.Build(nodes.Nodes[0].Node, 17911).ToJsonString());
            Assert(settings.ReadConfig()["proxy-groups"] == null, "Cannot return to a single node");
        }
        using var reopened = new SubscriptionDialog(folder);
        var savedList = (ListBox)typeof(SubscriptionDialog).GetField("nodes", flags)!.GetValue(reopened)!;
        Assert(savedList.Items.Count == 3 && ((SubscriptionNode)savedList.SelectedItem!).Name == "US Auto", "Group cache selection not restored");
        reopened.StartPosition = FormStartPosition.Manual; reopened.Location = new(-32000, -32000); reopened.ShowInTaskbar = false;
        reopened.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(reopened.Width, reopened.Height);
        reopened.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "subscription.png"));
        reopened.Close();
        Console.WriteLine("PASS: filter, choose correct node and restore cached multi-node selection");
    }

    sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return response(request, cancellationToken); }
    }
}
