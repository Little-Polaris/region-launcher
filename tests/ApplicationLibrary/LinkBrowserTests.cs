using System.Text.Json;

static class LinkBrowserTests {
    public static void Run() {
        string data = Path.Combine(AppContext.BaseDirectory, "test-data", "links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        void Check(bool ok) { if (!ok) throw new Exception("Link browser selection regression"); }
        string chrome = Path.Combine(data, "chrome.exe"), edge = Path.Combine(data, "msedge.exe");
        File.WriteAllText(chrome, "fixture"); File.WriteAllText(edge, "fixture");
        string saved = Path.Combine(data, "中文 saved profile");
        var app = new LaunchApplication { Name = "Chrome", Application = chrome, Arguments = $"--user-data-dir=\"{saved}\" --profile-directory=\"Profile 2\" --proxy-server=ignored" };
        Check(LinkBrowser.IndependentProfile(app)?.Profile == saved);
        Check(LinkBrowser.IndependentProfile(app with { Arguments = "--user-data-dir=relative" }) == null);
        string normal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        Check(LinkBrowser.IndependentProfile(app with { Arguments = $"--user-data-dir=\"{normal}\"" }) == null);
        var library = new ApplicationList { Applications = [app, app with { Id = "edge", Application = edge }] };
        string job = @"Local\RegionLauncher." + Guid.NewGuid().ToString("N");
        var plan = LinkBrowser.Prepare(data, job, new(), library);
        Check(plan.Executable == chrome && plan.Profile == saved && plan.ProfileName == "Profile 2");
        Check(!plan.Ini.Contains("proxy-server"));
        File.WriteAllText(Path.Combine(data, "link-browser.json"), JsonSerializer.Serialize(new LinkBrowserSelection { Choice = "edge" }));
        Check(LinkBrowser.Prepare(data, job, new(), library).Executable == edge);
        File.WriteAllText(Path.Combine(data, "link-browser.json"), JsonSerializer.Serialize(new LinkBrowserSelection { Choice = "auto" }));
        Check(LinkBrowser.Prepare(data, job, new(), new()).Profile == "");
        string otherJob = @"Local\RegionLauncher." + Guid.NewGuid().ToString("N");
        Check(LinkBrowser.Prepare(data, otherJob, new(), library).Temporary != plan.Temporary);
        Console.WriteLine("PASS: saved browser priority, Chrome/Edge selection, default-profile rejection, independent temporary fallback");
    }
}
