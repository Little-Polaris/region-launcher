using System.Text.Json.Nodes;

static class Tests
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string engine = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "proxy"));
        File.Copy(engine, Path.Combine(AppContext.BaseDirectory, "proxy", "mihomo.exe"), true);
        string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "proxy.json");
        Task.Run(() => SubscriptionTests.Run(engine, folder)).GetAwaiter().GetResult();
        SubscriptionTests.Selection(folder);
        Task.Run(() => SubscriptionTests.Latency(engine, folder)).GetAwaiter().GetResult();
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (string type in ProxyConfiguration.Protocols)
        {
            var node = new JsonObject { ["type"] = type, ["server"] = "192.0.2.1", ["port"] = 443 };
            if (type is "vless" or "vmess" or "tuic") node["uuid"] = "78ac0fdc-9041-4b28-9c55-167886f88751";
            if (type is not ("vless" or "vmess")) node["password"] = "fixture-password";
            if (type == "ss") node["cipher"] = "aes-128-gcm";
            if (type == "vmess") { node["cipher"] = "auto"; node["alterId"] = 0; }
            if (type is "vless" or "vmess") { node["tls"] = true; node["servername"] = "example.com"; }
            node["udp"] = true;
            var config = ProxyConfiguration.Build(node, 17890);
            Assert(node["name"] == null, "Build mutated input");
            Assert(config["rules"]![0]!.GetValue<string>() == "MATCH,RegionExit", "Fixed exit lost");
            Assert(config["dns"]!["nameserver"]![0]!.GetValue<string>().EndsWith("#RegionExit"), "DNS bypass");
            Assert(!config["tun"]!["enable"]!.GetValue<bool>() && !config["allow-lan"]!.GetValue<bool>(), "Host exposure");
            using var proxy = new DedicatedProxy(); using var form = new ProxySettingsDialog(proxy);
            form.LoadText(config.ToJsonString());
            var roundtrip = form.ReadConfig();
            Assert(roundtrip["proxies"]![0]!["udp"]!.GetValue<bool>(), "Advanced field lost");
            Assert(roundtrip["proxies"]![0]!["type"]!.GetValue<string>() == type, "Protocol lost");
            if (type == "hysteria2")
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "proxy-settings.png"));
                // Exercise the real async save handler without contacting a proxy server.
                var apply = typeof(ProxySettingsDialog).GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var saveTask = (Task)apply.Invoke(form, [true])!;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!saveTask.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(saveTask.IsCompletedSuccessfully && form.DialogResult == DialogResult.OK, "GUI save handler did not close successfully");
            }
            Task.Run(() => ProxyConfiguration.ValidateAndSave(roundtrip, path, engine, true)).GetAwaiter().GetResult();
            Assert(File.Exists(path), "Not saved");
            Console.WriteLine($"PASS: {type} form round-trip and actual Mihomo config validation");
        }
        string original = File.ReadAllText(path);
        var invalid = JsonNode.Parse(original)!.AsObject(); invalid["proxies"]![0]!["type"] = "nonexistent-protocol";
        try { Task.Run(() => ProxyConfiguration.ValidateAndSave(invalid, path, engine, true)).GetAwaiter().GetResult(); throw new Exception("Invalid config accepted"); }
        catch (IOException) { }
        Assert(File.ReadAllText(path) == original, "Failed validation overwrote saved config");
        Assert(File.Exists(path + ".bak"), "Backup missing");
        Assert(Directory.GetFiles(folder, "validate-*.json").Length == 0, "Temporary credentials left behind");
        foreach (var badNode in new[] {
            new JsonObject { ["type"]="socks5", ["server"]="127.0.0.1", ["port"]=7890 },
            new JsonObject { ["type"]="trojan", ["server"]="127.0.0.1", ["port"]=443, ["password"]="fixture" },
            new JsonObject { ["type"]="trojan", ["server"]="192.0.2.1", ["port"]=443, ["password"]="fixture", ["dialer-proxy"]="DIRECT" }
        }) {
            try { ProxyConfiguration.Build(badNode, 17890); throw new Exception("Unsafe node accepted"); }
            catch (IOException) { }
        }
        Console.WriteLine("PASS: rejected invalid/loopback/chained nodes, atomic save, backup and temporary cleanup");
        if (args.Length > 2)
        {
            string privateText = File.ReadAllText(args[2]);
            var originalNode = ProxyConfiguration.ReadNode(privateText, out var privatePort);
            using var proxy = new DedicatedProxy(); using var form = new ProxySettingsDialog(proxy);
            form.LoadText(privateText); var config = form.ReadConfig();
            var resultingNode = config["proxies"]![0]!.AsObject();
            foreach (var property in originalNode)
                Assert(JsonNode.DeepEquals(property.Value, resultingNode[property.Key]), "Existing private node field changed");
            Assert(config["mixed-port"]!.GetValue<int>() == privatePort, "Existing port changed");
            Task.Run(() => ProxyConfiguration.ValidateAndSave(config, Path.Combine(folder, "private-compatibility.json"), engine, false)).GetAwaiter().GetResult();
            Console.WriteLine("PASS: existing private node fields preserved and engine validated; original file untouched");
        }
    }
}
