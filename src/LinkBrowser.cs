using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed record LinkBrowserChoice(string Id, string Name) { public override string ToString() => Name; }
sealed record LinkBrowserSelection { public string Choice { get; init; } = "auto"; }
sealed record LinkBrowserPlan(string Executable, string Profile, string ProfileName, string Temporary)
{
    public string Ini => $"LinkBrowser_ENABLED=1\r\nLinkBrowser_EXE={Executable}\r\nLinkBrowser_PROFILE={Profile}\r\nLinkBrowser_PROFILE_NAME={ProfileName}\r\nLinkBrowser_TEMP={Temporary}\r\n";
}
static class LinkBrowser
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Split(string arguments) {
        var pointer = CommandLineToArgvW("browser.exe " + arguments, out int count);
        if (pointer == IntPtr.Zero) throw new IOException("无法读取浏览器启动参数。");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    public static string Kind(string path) => Path.GetFileName(path).ToLowerInvariant() switch { "chrome.exe" => "chrome", "msedge.exe" => "edge", _ => "" };
    static string Flag(string[] arguments, string name) {
        string value = "";
        for (int i = 0; i < arguments.Length; ++i) {
            if (arguments[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) value = arguments[i][(name.Length + 1)..];
            else if (arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Length) value = arguments[++i];
        }
        return value;
    }
    public static (string Profile, string Name)? IndependentProfile(LaunchApplication app) {
        if (Kind(app.Application) == "") return null;
        var arguments = Split(app.Arguments);
        string profile = Environment.ExpandEnvironmentVariables(Flag(arguments, "--user-data-dir"));
        if (!Path.IsPathFullyQualified(profile) || profile.IndexOfAny(['\r','\n','\0']) >= 0) return null;
        profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        foreach (string name in new[] { "Google/Chrome/User Data", "Microsoft/Edge/User Data" }) {
            string normal = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name));
            if (profile.Equals(normal, StringComparison.OrdinalIgnoreCase) || profile.StartsWith(normal + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        }
        string sub = Flag(arguments, "--profile-directory");
        if (sub.IndexOfAny(['\r','\n','\0','/','\\',':']) >= 0 || sub is "." or "..") return null;
        return (profile, sub);
    }
    public static List<LinkBrowserChoice> Choices(ApplicationList? library) {
        var choices = new List<LinkBrowserChoice> { new("auto", "自动选择（优先已有独立配置）"), new("chrome", "Chrome（优先已有独立配置）"), new("edge", "Edge（优先已有独立配置）") };
        if (library != null) foreach (var app in library.Applications)
            if (IndependentProfile(app) != null) choices.Add(new("app:" + app.Id, app.Name + " · 已保存的独立配置"));
        return choices;
    }
    public static string FindInstalled(string kind) {
        string suffix = kind == "edge" ? "Microsoft/Edge/Application/msedge.exe" : "Google/Chrome/Application/chrome.exe";
        foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }) {
            string candidate = Path.GetFullPath(Path.Combine(basePath, suffix));
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
    public static LinkBrowserPlan Prepare(string data, string job, LaunchEnvironment environment, ApplicationList library) {
        string file = Path.Combine(data, "link-browser.json");
        var choice = File.Exists(file) ? JsonSerializer.Deserialize<LinkBrowserSelection>(File.ReadAllText(file)) ?? throw new IOException("授权浏览器配置无效。") : new();
        if (!Choices(library).Any(c => c.Id == choice.Choice)) throw new IOException("授权浏览器条目已不存在，请重新选择。");
        var selected = library.Applications.FirstOrDefault(a => IndependentProfile(a) != null && File.Exists(ApplicationLibrary.ResolvePath(a.Application)) &&
            (choice.Choice == "auto" || choice.Choice == Kind(a.Application) || choice.Choice == "app:" + a.Id));
        if (choice.Choice.StartsWith("app:") && selected == null) throw new IOException("选定的授权浏览器不存在，请编辑软件路径。");
        string executable = selected != null ? ApplicationLibrary.ResolvePath(selected.Application) :
            choice.Choice == "edge" ? FindInstalled("edge") : FindInstalled("chrome");
        if (executable == "" && choice.Choice == "auto") executable = FindInstalled("edge");
        var saved = selected != null ? IndependentProfile(selected) : null;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(environment))))[..16];
        string id = job.StartsWith(@"Local\RegionLauncher.") ? job[@"Local\RegionLauncher.".Length..] : "";
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("无效的运行环境编号。");
        string temporary = Path.GetFullPath(Path.Combine(data, "link-browser-temp", id, Kind(executable) + "-" + hash));
        Directory.CreateDirectory(temporary);
        if (saved != null) Directory.CreateDirectory(saved.Value.Profile);
        return new(executable, saved?.Profile ?? "", saved?.Name ?? "", temporary);
    }
}

sealed class LinkBrowserDialog : Form
{
    public LinkBrowserDialog(string data, ApplicationList? library) {
        HomeTheme.Dialog(this, "授权链接浏览器", new(650, 330), new(520, 300));
        MinimizeBox = MaximizeBox = false;
        var list = HomeTheme.Select(); list.Dock = DockStyle.Top; list.PrefixSvg = "GlobalOutlined"; list.AccessibleName = "授权链接浏览器";
        list.Items.AddRange(LinkBrowser.Choices(library).Cast<object>().ToArray());
        string path = Path.Combine(data, "link-browser.json");
        var selected = File.Exists(path) ? JsonSerializer.Deserialize<LinkBrowserSelection>(File.ReadAllText(path)) : null;
        list.SelectedValue = list.Items.Cast<object>().OfType<LinkBrowserChoice>().FirstOrDefault(c => c.Id == selected?.Choice) ?? list.Items[0];
        var hint = new Label { Dock = DockStyle.Fill, Padding = new(2, 14, 0, 0), ForeColor = HomeTheme.Muted, Text = "· 优先使用软件列表中带独立资料目录的 Chrome / Edge。\n· 同一运行环境内已打开的实例可以直接复用。\n· 没有独立配置，或它被外部实例占用时，使用临时资料和无痕窗口。\n· 临时资料在环境结束后清理；已保存的资料不会删除。\n· 选择后从下次启动程序生效，不修改系统默认浏览器。" };
        var save = HomeTheme.Action("保存", AntdUI.TTypeMini.Primary, "CheckOutlined"); var cancel = HomeTheme.Action("取消"); cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) => { try { Directory.CreateDirectory(data); File.WriteAllText(path, JsonSerializer.Serialize(new LinkBrowserSelection { Choice = ((LinkBrowserChoice)list.SelectedValue!).Id }, new JsonSerializerOptions { WriteIndented = true })); DialogResult = DialogResult.OK; } catch (Exception ex) { MessageDialog.Show(this, ex.Message, "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        var buttons = HomeTheme.Footer(cancel, save); buttons.Dock = DockStyle.Bottom;
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new(24, 20, 24, 8) }; panel.Controls.Add(hint); panel.Controls.Add(list);
        Controls.Add(panel); Controls.Add(buttons);
        AcceptButton = save; CancelButton = cancel;
    }
}
