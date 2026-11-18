using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

static class RuntimeExitTests
{
    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task Run(string engine, string folder)
    {
        using var first = new Upstream("first");
        using var second = new Upstream("second");
        foreach (bool automatic in new[] { false, true })
        {
            int port = FreePort();
            var rules = new JsonObject
            {
                ["mixed-port"] = port, ["allow-lan"] = false, ["bind-address"] = "127.0.0.1",
                ["mode"] = "rule", ["rules"] = new JsonArray("MATCH,RegionExit"),
                ["profile"] = new JsonObject { ["store-selected"] = false },
                ["proxies"] = new JsonArray(first.Node(automatic ? "First" : "RegionExit"))
            };
            if (automatic)
            {
                rules["proxies"]!.AsArray().Add(second.Node("Second"));
                rules["proxy-groups"] = new JsonArray(new JsonObject
                {
                    ["name"] = "RegionExit", ["type"] = "fallback", ["proxies"] = new JsonArray("First", "Second")
                });
            }
            using var exit = new ProxyRuntimeExit(rules, folder);
            exit.WriteNode(null);
            string file = Path.Combine(folder, $"switch-runtime-{automatic}.json");
            await File.WriteAllTextAsync(file, rules.ToJsonString());
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-d", folder, "-f", file }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var throughProxy = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}") });
            async Task Expect(string body)
            {
                string answer = await throughProxy.GetStringAsync("http://fixture.invalid/", timeout.Token);
                if (answer != body || process.HasExited) throw new Exception("Unexpected route or proxy process exit");
            }
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    if (process.HasExited) throw new Exception(await output + await errors);
                    try { await exit.SelectAsync(automatic, timeout.Token); break; }
                    catch (IOException) when (attempt < 30) { await Task.Delay(100, timeout.Token); }
                }
                using var unauthorized = new HttpClient(new HttpClientHandler { UseProxy = false });
                using var response = await unauthorized.GetAsync($"http://{rules["external-controller"]}/proxies/RegionExit", timeout.Token);
                if (response.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Controller accepted unauthenticated access");

                await Expect("first");
                if (!automatic)
                {
                    first.Online = false;
                    using var failed = await throughProxy.GetAsync("http://fixture.invalid/", timeout.Token);
                    if (failed.IsSuccessStatusCode) throw new Exception("Unavailable node unexpectedly succeeded");
                }
                exit.WriteNode(second.Node("ignored"));
                await exit.RefreshNodeAsync(timeout.Token);
                await exit.SelectAsync(false, timeout.Token);
                await exit.CloseConnectionsAsync(timeout.Token);
                await Expect("second");
                first.Online = true;
                exit.WriteNode(second.Node("ignored"));
                await exit.RefreshNodeAsync(timeout.Token);
                await exit.CloseConnectionsAsync(timeout.Token);
                await Expect("second");
                if (automatic)
                {
                    await exit.SelectAsync(true, timeout.Token);
                    await exit.CloseConnectionsAsync(timeout.Token);
                    await Expect("first");
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                File.Delete(file);
            }
        }
        Console.WriteLine("PASS: real Mihomo hot switch, in-place reconnect, return to automatic, authenticated localhost API, same core");
    }

    sealed class Upstream : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task accept;
        readonly string body;
        public volatile bool Online = true;

        public Upstream(string body) { this.body = body; listener.Start(); accept = AcceptAsync(); }
        public JsonObject Node(string name) => new()
        {
            ["name"] = name, ["type"] = "http", ["server"] = "127.0.0.1",
            ["port"] = ((IPEndPoint)listener.LocalEndpoint).Port
        };

        async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
                _ = RespondAsync(await listener.AcceptTcpClientAsync(stop.Token));
        }
        async Task RespondAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII))
            {
                async Task ReadRequest()
                {
                    for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token));) { }
                }
                try
                {
                    await ReadRequest();
                    if (!Online)
                    {
                        await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), stop.Token);
                        return;
                    }
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop.Token);
                    await ReadRequest();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}"), stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        public void Dispose()
        {
            stop.Cancel();
            try { accept.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            listener.Stop();
            stop.Dispose();
        }
    }
}
