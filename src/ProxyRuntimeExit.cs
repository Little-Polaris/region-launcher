using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

// The launcher owns this provider and controller for one Mihomo process.
sealed class ProxyRuntimeExit : IDisposable
{
    const string Provider = "RegionActive";
    const string Manual = "RegionManual";
    const string Automatic = "RegionAutomatic";
    readonly string path;
    readonly HttpClient client;
    readonly JsonObject defaultNode;
    readonly bool group;

    public ProxyRuntimeExit(JsonObject config, string directory)
    {
        string file = $"runtime-node-{Environment.ProcessId}-{Guid.NewGuid():N}.json";
        path = Path.Combine(directory, file);
        var nodes = config["proxies"]!.AsArray();
        defaultNode = (JsonObject)nodes[0]!.DeepClone();
        var groups = config["proxy-groups"] as JsonArray;
        group = groups != null;
        if (groups != null) groups[0]!["name"] = Automatic;
        else
        {
            config.Remove("proxies");
            groups = new JsonArray();
            config["proxy-groups"] = groups;
        }
        var selector = new JsonObject { ["name"] = "RegionExit", ["type"] = "select", ["use"] = new JsonArray(JsonValue.Create(Provider)) };
        // A provider that has not loaded yet must still have a fail-closed route.
        selector["proxies"] = new JsonArray(JsonValue.Create(group ? Automatic : "REJECT"));
        groups.Add(selector);
        config["proxy-providers"] = new JsonObject
        {
            [Provider] = new JsonObject { ["type"] = "file", ["path"] = "./" + file }
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string endpoint = $"127.0.0.1:{port}";
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        config["external-controller"] = endpoint;
        config["secret"] = secret;
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("http://" + endpoint),
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public bool HasAutomatic => group;

    public void WriteNode(JsonObject? node)
    {
        // A different document also makes the provider rebuild the current node on reconnect.
        var entry = (JsonObject)(node ?? defaultNode).DeepClone();
        entry["name"] = Manual;
        var content = new JsonObject { ["generation"] = Guid.NewGuid().ToString("N"), ["proxies"] = new JsonArray(entry) };
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content.ToJsonString());
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task RefreshNodeAsync(CancellationToken token) => RequestAsync(HttpMethod.Put, "/providers/proxies/" + Provider, token);
    public Task CloseConnectionsAsync(CancellationToken token) => RequestAsync(HttpMethod.Delete, "/connections", token);
    public Task SelectAsync(bool automatic, CancellationToken token) =>
        RequestAsync(HttpMethod.Put, "/proxies/RegionExit", token, automatic ? Automatic : Manual);

    async Task RequestAsync(HttpMethod method, string route, CancellationToken token, string? node = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (node != null) request.Content = JsonContent.Create(new { name = node });
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException($"本地代理控制请求失败（HTTP {(int)response.StatusCode}）。");
        }
        catch (HttpRequestException ex) { throw new IOException("无法连接本地代理控制接口。", ex); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new IOException("本地代理控制请求超时。", ex);
        }
    }

    public void Dispose()
    {
        client.Dispose();
        File.Delete(path);
    }
}
