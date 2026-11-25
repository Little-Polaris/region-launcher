using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

sealed record UpdateInfo(Version Version, string Tag, string Notes, string Page, string? SetupUrl, string? SumsUrl, string SetupName);

sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public DateTime? LastCheck { get; set; }

    static string PathIn(string data) => Path.Combine(data, "update.json");

    public static UpdateSettings Load(string data)
    {
        try { return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(PathIn(data))) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string data)
    {
        try { Directory.CreateDirectory(data); File.WriteAllText(PathIn(data), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Checks GitHub Releases for a newer version and downloads its verified installer.</summary>
static class Updater
{
    internal static HttpClient CreateClient(int? proxyPort)
    {
        var client = proxyPort is int port
            ? new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}") })
            : new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RegionLauncher", Current.ToString()));
        return client;
    }

    public static Version Current => Version.TryParse(Application.ProductVersion.Split('+')[0], out var v) ? v : new Version(0, 0, 0);

    public static string ReleasesPage => $"https://github.com/{Installation.Repository}/releases/latest";

    /// <summary>Returns the latest release when it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Installation.Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub 返回 {(int)response.StatusCode}。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= Current) return null;
        string setupName = $"RegionLauncher-Setup-{latest}-win-x64.exe";
        string? setup = null, sums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name == setupName) setup = url;
            else if (name == $"RegionLauncher-{latest}-SHA256SUMS.txt") sums = url;
        }
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(latest, tag, notes.Trim(), page, setup, sums, setupName);
    }

    /// <summary>Downloads the installer and verifies it against the release's SHA-256 file. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (update.SetupUrl == null || update.SumsUrl == null) throw new IOException("该版本没有提供安装程序或校验文件，请从发布页手动下载。");
        string sums = await http.GetStringAsync(update.SumsUrl, cancellationToken);
        string? expected = sums.Split('\n').Select(line => line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].Trim().TrimStart('*') == update.SetupName).Select(parts => parts[0]).FirstOrDefault();
        if (expected == null || expected.Length != 64) throw new IOException("校验文件中没有安装程序的 SHA-256。");
        string folder = Path.Combine(Path.GetTempPath(), "RegionLauncherUpdate");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, update.SetupName), partial = target + ".part";
        using (var response = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)) {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(partial);
            var buffer = new byte[81920]; long done = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total is > 0) progress.Report((double)done / total.Value);
            }
        }
        string actual;
        await using (var file = File.OpenRead(partial)) actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
            File.Delete(partial);
            throw new IOException("下载的安装程序校验失败，已删除。请稍后重试或从发布页手动下载。");
        }
        File.Move(partial, target, true);
        return target;
    }

    /// <summary>Starts the verified installer in update mode; it waits for this process to exit before replacing files.</summary>
    public static void StartInstaller(string setup, string root)
    {
        var psi = new ProcessStartInfo(setup) { UseShellExecute = false };
        foreach (string arg in new[] { "/update", root, "/pid", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(arg);
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>Download progress for an update, cancellable.</summary>
sealed class UpdateDownloadDialog : Form
{
    readonly AntdUI.Progress bar = new() { Dock = DockStyle.Top, Height = 28, Radius = 6 };
    readonly Label status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 30, ForeColor = HomeTheme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly CancellationTokenSource cancel = new();
    public string? Result { get; private set; }
    public Exception? Error { get; private set; }

    public UpdateDownloadDialog(UpdateInfo update, HttpClient http)
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new(22, 18, 22, 8), BackColor = HomeTheme.Surface };
        var heading = new Label { Text = $"正在下载 Region Launcher {update.Version}", Dock = DockStyle.Top, AutoSize = false, Height = 32,
            Font = new(HomeTheme.FontName, 11, FontStyle.Bold), ForeColor = HomeTheme.Text };
        status.Text = "正在连接 GitHub…";
        body.Controls.Add(bar); body.Controls.Add(status); body.Controls.Add(heading);
        var stop = HomeTheme.Action("取消");
        stop.Click += (_, _) => cancel.Cancel();
        CancelButton = stop;
        var footer = HomeTheme.Footer(stop); footer.Dock = DockStyle.Bottom;
        Controls.Add(body); Controls.Add(footer);
        HomeTheme.Dialog(this, "下载更新", new(440, 190), new(380, 180));
        Icon = AppIcon.Load(new Size(32, 32));
        Shown += async (_, _) => {
            var progress = new Progress<double>(value => { bar.Value = (float)value; status.Text = $"已下载 {value:P0}"; });
            try { Result = await Updater.DownloadAsync(http, update, progress, cancel.Token); status.Text = "校验通过"; DialogResult = DialogResult.OK; }
            catch (OperationCanceledException) { DialogResult = DialogResult.Cancel; }
            catch (Exception ex) { Error = ex; DialogResult = DialogResult.Abort; }
        };
        FormClosing += (_, e) => { if (DialogResult == DialogResult.None) { e.Cancel = true; cancel.Cancel(); } };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancel.Dispose();
        base.Dispose(disposing);
    }
}
