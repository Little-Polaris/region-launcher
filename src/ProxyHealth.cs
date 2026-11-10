using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

sealed record ProbeSite(string Name, string Url, bool Trace);
sealed record ProbeResult(string Name, string Host, bool Ok, long Milliseconds, int? Status, string? Ip, string? Country, string? Error);

static class ProxyHealth
{
    public static readonly ProbeSite[] Sites = [
        new("Cloudflare", "https://www.cloudflare.com/cdn-cgi/trace", true),
        new("国内 IP 检测", "https://my.ip.cn/", false)
    ];
    public static async Task<ProbeResult[]> ProbeAsync(int? port, Action<string> log, ProbeSite[]? sites = null, TimeSpan? timeout = null)
    {
        using var handler = new SocketsHttpHandler {
            UseProxy = port.HasValue, Proxy = port.HasValue ? new WebProxy($"socks5://127.0.0.1:{port}") : null,
            AllowAutoRedirect = false, ConnectTimeout = timeout ?? TimeSpan.FromSeconds(15), UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65536 };
        return await Task.WhenAll((sites ?? Sites).Select(async site => {
            var timer = Stopwatch.StartNew(); int? status = null;
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, site.Url);
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                request.Headers.UserAgent.ParseAdd("RegionLauncher-Connectivity/1.0");
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                status = (int)response.StatusCode;
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"HTTP {status}，未收到预期检测内容");
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                string? ip = null, country = null;
                if (site.Trace) {
                    foreach (string line in text.Split('\n')) {
                        if (line.StartsWith("ip=")) ip = line[3..].Trim();
                        if (line.StartsWith("loc=")) country = line[4..].Trim();
                    }
                } else ip = Regex.Match(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b").Value;
                if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    throw new IOException("响应中没有有效 IPv4 出口地址");
                log($"{site.Name}：HTTPS 实际请求成功，HTTP {status}，{timer.ElapsedMilliseconds} ms，出口 {ip}" + (country == null ? "" : $"，地区 {country}"));
                return new ProbeResult(site.Name, new Uri(site.Url).Host, true, timer.ElapsedMilliseconds, status, ip, country, null);
            } catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) {
                string error = ex is OperationCanceledException ? "请求超时" : ex.Message + (ex.InnerException == null ? "" : "；" + ex.GetBaseException().Message);
                log($"{site.Name}：失败，{timer.ElapsedMilliseconds} ms，{error}");
                return new ProbeResult(site.Name, new Uri(site.Url).Host, false, timer.ElapsedMilliseconds, status, null, null, error);
            }
        })).ConfigureAwait(false);
    }
    public static void RequireInternet(ProbeResult[] results)
    {
        if (!results.Any(r => r.Ok)) throw new IOException("SOCKS5 可用，但实际 HTTPS 联网检测未通过；程序没有启动。请检查节点、订阅余额或节点可用性。");
    }
    public static void RequireMatchingExit(ProbeResult[] proxy, ProbeResult[] environment)
    {
        RequireInternet(environment);
        var comparable = proxy.Where(p => p.Ok).Join(environment.Where(p => p.Ok), p => p.Host, p => p.Host, (p, e) => (p, e)).ToArray();
        if (comparable.Length == 0) throw new IOException("两层检测没有共同成功的网站，暂时无法确认环境出口；程序没有启动。");
        if (comparable.Any(pair => pair.p.Ip != pair.e.Ip)) throw new IOException("运行环境与指定代理的出口 IP 不一致，无法确认接管，程序没有启动。请查看日志；轮换出口的节点也可能触发此提示。");
    }
}
