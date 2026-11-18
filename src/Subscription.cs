using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

sealed record SubscriptionNode(string Name, JsonObject Node)
{
    public override string ToString() => ProxyConfiguration.IsFailover(Node)
        ? $"{Name}  [自动故障切换 · {Node["members"]!.AsArray().Count} 个节点]"
        : $"{Name}  [{Node["type"]}]";
}
sealed record SubscriptionResult(List<SubscriptionNode> Nodes, int Skipped);

static class Subscription
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public static SubscriptionResult Parse(string text, bool cached = false)
    {
        if (Encoding.UTF8.GetByteCount(text) > (cached ? 12 * 1024 * 1024 : MaxBytes))
            throw new IOException("订阅内容超过大小限制，请使用节点订阅而非大型规则配置。");
        text = text.Trim().TrimStart('\uFEFF');
        if (text.Length == 0) throw new IOException("订阅内容为空。");
        if (!text.Contains("://") && !text.Contains(':') && !text.StartsWith('{') && !text.StartsWith('['))
            try { text = Decode64(text); } catch { throw new IOException("无法识别订阅格式，请使用 Clash/Mihomo 订阅链接。"); }
        var candidates = new List<JsonObject>(); int skipped = 0;
        JsonArray? groups = null;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\A[a-zA-Z0-9]+://"))
        {
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#')) continue;
                if (candidates.Count + skipped >= 5000) throw new IOException("订阅节点超过 5000 个。");
                try { candidates.Add(ParseLink(line)); } catch { skipped++; }
            }
        }
        else
        {
            JsonNode? root;
            try
            {
                if (text.StartsWith('{') || text.StartsWith('[')) root = JsonNode.Parse(text);
                else
                {
                    var parser = new Parser(new StringReader(text)); int events = 0, nesting = 0;
                    while (parser.MoveNext())
                    {
                        if (++events > 100000) throw new IOException();
                        if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart) nesting++;
                        if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd) nesting--;
                        if (nesting > 40) throw new IOException();
                    }
                    var yaml = new YamlStream(); yaml.Load(new StringReader(text));
                    if (yaml.Documents.Count != 1) throw new IOException();
                    int budget = 100000;
                    root = ToJson(yaml.Documents[0].RootNode, 0, ref budget);
                }
            }
            catch { throw new IOException("订阅不是有效的 YAML / JSON 配置，请确认链接返回节点而非登录网页。"); }
            JsonArray? nodes = root as JsonArray ?? (root as JsonObject)?["proxies"] as JsonArray;
            if (nodes == null && root is JsonObject single && single["type"] != null) nodes = new JsonArray(single.DeepClone());
            if (nodes == null) throw new IOException("未找到 proxies 节点列表。请使用机场的 Clash/Mihomo 节点订阅，暂不递归下载 proxy-providers。");
            groups = (root as JsonObject)?["proxy-groups"] as JsonArray;
            if (nodes.Count > (cached ? 10000 : 5000)) throw new IOException("订阅条目过多。");
            foreach (var entry in nodes) { if (entry is JsonObject obj) candidates.Add((JsonObject)obj.DeepClone()); else skipped++; }
        }
        var result = new List<SubscriptionNode>();
        foreach (var node in candidates)
        {
            try
            {
                // Validate selection eligibility without loading the subscription's host settings.
                ProxyConfiguration.Build(node, 17890);
                string name = node["name"]?.GetValue<string>() ?? $"节点 {result.Count + 1}";
                result.Add(new(name, node));
            }
            catch { skipped++; }
        }
        if (groups != null)
        {
            var sourceNames = candidates.Select(n => (n["name"] as JsonValue)?.ToString())
                .Where(n => !string.IsNullOrEmpty(n)).ToLookup(n => n!, StringComparer.Ordinal);
            var byName = result.Where(n => !ProxyConfiguration.IsFailover(n.Node) && sourceNames[n.Name].Count() == 1)
                .ToDictionary(n => n.Name, n => n.Node, StringComparer.Ordinal);
            var groupNames = new HashSet<string>(StringComparer.Ordinal);
            int remainingMembers = 5000, remainingBytes = MaxBytes;
            foreach (var entry in groups.OfType<JsonObject>())
            {
                if ((entry["type"] as JsonValue)?.ToString() != "fallback") continue;
                try
                {
                    string name = entry["name"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(name) || sourceNames.Contains(name) || groupNames.Contains(name) ||
                        entry.ContainsKey("use") || entry.ContainsKey("filter") || entry.ContainsKey("exclude-filter") ||
                        entry.ContainsKey("exclude-type") || entry.ContainsKey("override") ||
                        new[] { "include-all", "include-all-proxies", "include-all-providers" }.Any(key => entry[key]?.ToString() == "true") ||
                        entry["proxies"] is not JsonArray refs || refs.Count < 2 || refs.Count > remainingMembers)
                        throw new IOException();
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    var members = new JsonArray();
                    int groupBytes = Encoding.UTF8.GetByteCount(name);
                    foreach (var reference in refs)
                    {
                        string member = reference?.GetValue<string>() ?? "";
                        if (member is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "GLOBAL" ||
                            !unique.Add(member) || !byName.TryGetValue(member, out var node))
                            throw new IOException();
                        groupBytes += Encoding.UTF8.GetByteCount(node.ToJsonString());
                        if (groupBytes > remainingBytes) throw new IOException();
                        members.Add(node.DeepClone());
                    }
                    var group = new JsonObject { ["type"] = "fallback", ["name"] = name, ["members"] = members };
                    groupNames.Add(name);
                    remainingMembers -= members.Count;
                    remainingBytes -= groupBytes;
                    result.Add(new(name, group));
                }
                catch { skipped++; }
            }
        }
        if (result.Count == 0) throw new IOException($"没有可用节点（跳过 {skipped} 项）。请使用包含所支持协议的 Clash/Mihomo 订阅。");
        return new(result, skipped);
    }

    static JsonNode? ToJson(YamlNode node, int depth, ref int budget, string key = "")
    {
        if (depth > 40 || --budget < 0) throw new IOException("订阅结构过于复杂。");
        if (node is YamlMappingNode map)
        {
            var obj = new JsonObject();
            foreach (var item in map.Children)
            {
                if (item.Key is not YamlScalarNode scalar || scalar.Value == null || obj.ContainsKey(scalar.Value)) throw new IOException();
                obj.Add(scalar.Value, ToJson(item.Value, depth + 1, ref budget, scalar.Value));
            }
            return obj;
        }
        if (node is YamlSequenceNode sequence)
        {
            var array = new JsonArray(); foreach (var item in sequence.Children) array.Add(ToJson(item, depth + 1, ref budget, key == "proxies" && item is YamlScalarNode ? "name" : key)); return array;
        }
        var value = (YamlScalarNode)node; string s = value.Value ?? "";
        // Numeric-looking credentials, hostnames and fingerprints must remain strings.
        if (new[] { "name", "type", "server", "password", "uuid", "token", "cipher", "sni", "servername", "short-id", "public-key", "obfs-password" }.Contains(key) || value.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
            return JsonValue.Create(s);
        if (bool.TryParse(s, out bool boolean)) return JsonValue.Create(boolean);
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return JsonValue.Create(number);
        if (s is "null" or "~") return null;
        return JsonValue.Create(s);
    }

    static string Decode64(string s)
    {
        s = string.Concat(s.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')));
    }
    static JsonObject ParseLink(string link)
    {
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return Vmess(Decode64(link[8..]));
        if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) && !link.Split('#')[0].Contains('@'))
        {
            int hash = link.IndexOf('#'); string tail = hash >= 0 ? link[hash..] : "";
            link = "ss://" + Decode64(link[5..(hash < 0 ? link.Length : hash)]) + tail;
        }
        var uri = new Uri(link); string type = uri.Scheme == "hy2" ? "hysteria2" : uri.Scheme;
        if (!ProxyConfiguration.Protocols.Contains(type)) throw new IOException();
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        { var pair = part.Split('=', 2); q.Add(Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : ""); }
        string Take(string key, string fallback = "") { if (q.Remove(key, out var value)) return value; return fallback; }
        string name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var node = new JsonObject { ["name"] = name.Length > 0 ? name : type + " 节点", ["type"] = type, ["server"] = uri.Host, ["port"] = uri.Port > 0 ? uri.Port : 443 };
        string auth = Uri.UnescapeDataString(uri.UserInfo);
        if (type == "ss")
        {
            if (!auth.Contains(':')) auth = Decode64(auth);
            var pair = auth.Split(':', 2); node["cipher"] = pair[0]; node["password"] = pair[1];
            // Plugin URLs require transport-specific interpretation; use Clash YAML instead.
        }
        else if (type == "tuic") { var pair = auth.Split(':', 2); node["uuid"] = pair[0]; node["password"] = pair[1]; }
        else if (type == "vless") node["uuid"] = auth;
        else node["password"] = auth;
        string security = Take("security", type == "vless" ? "none" : "tls");
        if (security is not ("none" or "tls" or "reality")) throw new IOException();
        if (type == "vless") node["tls"] = security != "none";
        string sni = Take("sni", Take("peer"));
        if (sni.Length > 0) node[type == "vless" ? "servername" : "sni"] = sni;
        string insecure = Take("insecure", Take("allowInsecure"));
        if (insecure.Length > 0) node["skip-cert-verify"] = ParseBoolean(insecure);
        string alpn = Take("alpn"); if (alpn.Length > 0) node["alpn"] = new JsonArray(alpn.Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        string fp = Take("fp"); if (fp.Length > 0) node["client-fingerprint"] = fp;
        if (security == "reality") node["reality-opts"] = new JsonObject { ["public-key"] = Take("pbk"), ["short-id"] = Take("sid") };
        string flow = Take("flow"); if (flow.Length > 0) node["flow"] = flow;
        string encryption = Take("encryption", "none"); if (encryption != "none") throw new IOException();
        Transport(node, Take("type", "tcp"), Take("host"), Take("path"), Take("serviceName"));
        foreach (var key in new[] { "obfs", "obfs-password", "congestion_control", "udp_relay_mode" })
        {
            string value = Take(key); if (value.Length > 0) node[key switch { "congestion_control" => "congestion-controller", "udp_relay_mode" => "udp-relay-mode", _ => key }] = value;
        }
        if (q.Count > 0) throw new IOException("分享链接含未支持参数，请改用 Clash/Mihomo 格式。");
        node["udp"] = true;
        return node;
    }

    static void Transport(JsonObject node, string network, string host, string path, string service)
    {
        if (network is "tcp" or "") { if (host.Length > 0 || path.Length > 0 || service.Length > 0) throw new IOException(); return; }
        node["network"] = network;
        if (network == "ws")
        {
            var options = new JsonObject { ["path"] = path.Length > 0 ? path : "/" };
            if (host.Length > 0) options["headers"] = new JsonObject { ["Host"] = host };
            node["ws-opts"] = options;
        }
        else if (network == "grpc") node["grpc-opts"] = new JsonObject { ["grpc-service-name"] = service.Length > 0 ? service : path };
        else throw new IOException("未支持的分享链接传输方式，请改用 Clash/Mihomo 格式。");
    }

    static JsonObject Vmess(string json)
    {
        var raw = JsonNode.Parse(json)!.AsObject();
        string[] supported = ["v", "ps", "add", "port", "id", "aid", "scy", "net", "type", "host", "path", "tls", "sni", "fp", "alpn", "allowInsecure"];
        if (raw.Any(p => !supported.Contains(p.Key))) throw new IOException("VMess 分享链接含未支持参数。");
        string Get(string key, string fallback = "") => raw[key]?.ToString() ?? fallback;
        if (Get("type", "none") is not ("none" or "")) throw new IOException();
        var node = new JsonObject { ["name"] = Get("ps", "VMess 节点"), ["type"] = "vmess", ["server"] = Get("add"), ["port"] = int.Parse(Get("port")), ["uuid"] = Get("id"), ["alterId"] = int.Parse(Get("aid", "0")), ["cipher"] = Get("scy", "auto"), ["tls"] = Get("tls") == "tls", ["udp"] = true };
        if (Get("sni").Length > 0) node["servername"] = Get("sni");
        if (Get("fp").Length > 0) node["client-fingerprint"] = Get("fp");
        if (Get("allowInsecure").Length > 0) node["skip-cert-verify"] = ParseBoolean(Get("allowInsecure"));
        if (Get("alpn").Length > 0) node["alpn"] = new JsonArray(Get("alpn").Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        Transport(node, Get("net", "tcp"), Get("host"), Get("path"), Get("path"));
        return node;
    }

    static bool ParseBoolean(string value) => value switch {
        "1" => true, "0" => false, _ => bool.Parse(value)
    };

    public static async Task<string> Download(string address, bool localProxy, CancellationToken cancellation, HttpMessageHandler? testHandler = null)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0)
            throw new IOException("请填写有效的 HTTPS 订阅链接。");
        using var handler = testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseProxy = localProxy, Proxy = localProxy ? new WebProxy("http://127.0.0.1:7890") : null };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("clash.meta/1.19.33");
        try
        {
            for (int hop = 0; hop < 6; hop++)
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location ?? throw new IOException("订阅重定向无效。");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme != "https" || url.UserInfo.Length > 0) throw new IOException("订阅重定向必须保持 HTTPS。");
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new IOException($"订阅下载失败（HTTP {(int)response.StatusCode}），请检查链接或下载方式。");
                if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("订阅超过 4 MB。");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var output = new MemoryStream(); byte[] buffer = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                { if (output.Length + count > MaxBytes) throw new IOException("订阅超过 4 MB。"); await output.WriteAsync(buffer.AsMemory(0, count), token); }
                return Encoding.UTF8.GetString(output.ToArray());
            }
            throw new IOException("订阅重定向次数过多。");
        }
        catch (HttpRequestException) { throw new IOException("无法下载订阅，请检查网络、订阅链接和本机 7890 代理。"); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new IOException("订阅下载超时，请检查下载方式或稍后重试。"); }
    }
}
