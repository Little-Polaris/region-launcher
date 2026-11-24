using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

static class NodeLatency
{
    const string Target = "https://www.gstatic.com/generate_204";

    public static async Task MeasureAsync(string engine, string folder, IReadOnlyList<JsonObject> nodes,
        IProgress<(int Index, int? DelayMs)> progress, CancellationToken token)
    {
        if (nodes.Count == 0) return;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var candidates = new JsonArray();
        for (int i = 0; i < nodes.Count; i++) {
            token.ThrowIfCancellationRequested();
            var node = (JsonObject)ProxyConfiguration.Build(nodes[i], 17890)["proxies"]![0]!.DeepClone();
            node["name"] = "Probe" + i;
            candidates.Add(node);
        }
        var config = new JsonObject {
            ["external-controller"] = "127.0.0.1:" + port, ["secret"] = secret,
            ["allow-lan"] = false, ["ipv6"] = false, ["mode"] = "rule", ["log-level"] = "silent",
            ["rules"] = new JsonArray(JsonValue.Create("MATCH,REJECT")), ["proxies"] = candidates
        };
        // Use a separate Mihomo process with no traffic/DNS listener; never touch the live selector.
        string path = Path.Combine(folder, $"latency-{Guid.NewGuid():N}.json");
        try {
            await File.WriteAllTextAsync(path, config.ToJsonString(), token).ConfigureAwait(false);
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-d", folder, "-f", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("无法启动测速内核。");
            try {
                EngineLifetime.Bind(process, _ => { });
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) {
                    BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(7)
                };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                bool ready = false;
                for (int i = 0; i < 30 && !process.HasExited; i++) {
                    token.ThrowIfCancellationRequested();
                    try {
                        using var response = await client.GetAsync("/version", token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) { ready = true; break; }
                    } catch (HttpRequestException) { }
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                if (!ready) throw new IOException("测速内核未就绪。");
                await ProbeAsync(nodes.Count, client, progress, token).ConfigureAwait(false);
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); } }
        }
        finally { File.Delete(path); }
    }

    internal static Task ProbeAsync(int count, HttpClient client, IProgress<(int Index, int? DelayMs)> progress, CancellationToken token) =>
        Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = token },
            async (i, ct) => {
                int? delay = null;
                try {
                    string endpoint = $"/proxies/Probe{i}/delay?timeout=5000&expected=204&url={Uri.EscapeDataString(Target)}";
                    using var response = await client.GetAsync(endpoint, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) {
                        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct).ConfigureAwait(false);
                        if (json?["delay"] is JsonValue value && value.TryGetValue<int>(out int ms) && ms >= 0) delay = ms;
                    }
                } catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
                ct.ThrowIfCancellationRequested();
                progress.Report((i, delay));
            });
}
