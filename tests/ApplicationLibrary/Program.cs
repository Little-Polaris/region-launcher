using System.Text.Json;

string folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
string path = Path.Combine(folder, "applications.json");
if (File.Exists(path)) File.Delete(path);
foreach (var (target, command, interactive) in new[] {
    ("pwsh.exe", "", true), ("PowerShell.EXE", "-NoProfile -NoExit", true),
    ("pwsh.exe", "-Command echo hi", false), ("powershell.exe", "-File task.ps1", false),
    ("cmd.exe", "/d /k echo ready", true), ("cmd.exe", "/d /s /c echo done", false),
    ("task.ps1", "", false), ("task.bat", "", false), ("chrome.exe", "", false) })
    if (ApplicationLibrary.UsesInteractiveConsole(target, command) != interactive)
        throw new Exception($"Incorrect console mode for {target} {command}");
var store = new ApplicationLibrary(path);
const string legacy = "C:\\中文 软件🚀\\browser.exe";
const string arguments = "--user-data-dir=\"D:\\浏览器资料\\专用 副本\" --profile-directory=Default";
var japan = new LaunchEnvironment { GeoId = 122, Locale = "ja-JP", TimeZone = "Tokyo Standard Time", AutoDst = false };
var initial = store.Load(legacy, arguments, japan);
if (initial.Applications[0].Environment != japan) throw new Exception("Legacy region settings lost");
if (initial.Applications.Single().Application != legacy || initial.Applications[0].Arguments != arguments || initial.SelectedId != initial.Applications[0].Id)
    throw new Exception("Legacy configuration lost during migration");
var second = new LaunchApplication { Name = "另一套参数", Application = legacy, Arguments = "--new-window", Environment = new() };
var two = initial with { Applications = [.. initial.Applications, second], SelectedId = second.Id };
store.Save(two);
var reloaded = store.Load("C:\\different.exe", "different arguments");
if (reloaded.Applications[0].Environment != japan || reloaded.Applications[1].Environment != new LaunchEnvironment()) throw new Exception("Per-app environments mixed");
if (reloaded.Applications.Count != 2 || reloaded.SelectedId != second.Id || reloaded.Applications[0].Arguments != arguments)
    throw new Exception("Saved list/selection was not retained, or legacy profile imported twice");
var edited = second with { Name = "修改后的中文名称", Arguments = "--flag=\"保留空格 🚀\"" };
store.Save(two with { Applications = [two.Applications[0], edited] });
if (store.Load("", "").Applications[1] != edited) throw new Exception("Edit not persisted");
store.Save(new ApplicationList());
if (store.Load(legacy, arguments).Applications.Count != 0) throw new Exception("Deleted list resurrected from legacy profile");
byte[] before = File.ReadAllBytes(path);
foreach (var invalid in new[] {
    new ApplicationList { Version = 2 },
    new ApplicationList { Applications = [second, second] },
    new ApplicationList { Applications = [second with { Arguments = "\r\nApplication=bad.exe" }] },
    new ApplicationList { SelectedId = "missing", Applications = [second] },
    new ApplicationList { Applications = [second with { Application = "bad.txt" }] }
}) {
    try { store.Save(invalid); throw new Exception("Invalid configuration accepted"); }
    catch (InvalidDataException) { }
    if (!File.ReadAllBytes(path).SequenceEqual(before)) throw new Exception("Rejected save changed file");
}
File.WriteAllText(path, "{broken original config");
try { store.Load(legacy, arguments); throw new Exception("Malformed file accepted"); } catch (JsonException) { }
if (File.ReadAllText(path) != "{broken original config") throw new Exception("Malformed source overwritten");
if (ApplicationLibrary.ResolvePath("%SystemRoot%\\System32\\cmd.exe") != Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"))
    throw new Exception("Environment path expansion failed");
const string oldExe = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
const string newPackage = "OpenAI.Codex_26.1117.3838.0_x64__2p2nqsd0c76g0";
const string newRoot = @"D:\WindowsApps\OpenAI.Codex_26.1117.3838.0_x64__2p2nqsd0c76g0";
var resolved = PackageExecutable.Resolve(oldExe,
    family => family == "OpenAI.Codex_2p2nqsd0c76g0"
        ? ["OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0", newPackage,
           "OpenAI.Codex_99.0.0.0_x64__badpublisher1", "OpenAI.Codex_99.0.0.0_x64_en-us_2p2nqsd0c76g0",
           "OpenAI.Codex_99.0.0.0_arm64__2p2nqsd0c76g0"]
        : throw new Exception("Incorrect package family"),
    full => full == newPackage ? newRoot : Path.GetDirectoryName(Path.GetDirectoryName(oldExe)),
    _ => true);
if (resolved != Path.Combine(newRoot, @"app\ChatGPT.exe")) throw new Exception("Store upgrade did not resolve same-family executable on its registered volume");
if (PackageExecutable.Resolve(oldExe, _ => [], _ => null, _ => false) != oldExe)
    throw new Exception("Absent Store package should preserve the saved path");
if (args.Length == 3) {
    string actual = ApplicationLibrary.ResolvePath(args[1]);
    if (!actual.Equals(args[2], StringComparison.OrdinalIgnoreCase) || !File.Exists(actual))
        throw new Exception($"Did not resolve the actual registered package: {actual}");
    Console.WriteLine("PASS: Windows package API resolved the stale path to the registered ChatGPT executable.");
}
Console.WriteLine("PASS: Store app updates resolve current version without changing saved path.");
if (ApplicationLibrary.ResolvePath("RegionProbe.exe") != Path.Combine(AppContext.BaseDirectory, "RegionProbe.exe")) throw new Exception("Relative path base incorrect");
File.Delete(path);
File.WriteAllText(path, """{"version":1,"selectedId":"old","applications":[{"id":"old","name":"old","application":"old.exe","arguments":""}]}""");
if (store.Load("", "", japan).Applications[0].Environment != japan) throw new Exception("Existing old list did not inherit saved region");
if (store.Load("", "", new()).Applications[0].Environment != japan) throw new Exception("Migration overwrote explicit region on reopen");
before = File.ReadAllBytes(path);
foreach (var bad in new[] { japan with { GeoId = -1 }, japan with { Locale = "bad\r\nRegion=US" }, japan with { TimeZone = "Missing Standard Time" } }) {
    try { store.Save(new ApplicationList { Applications = [second with { Environment = bad }] }); throw new Exception("Invalid environment accepted"); }
    catch (InvalidDataException) { }
    if (!File.ReadAllBytes(path).SequenceEqual(before)) throw new Exception("Invalid environment replaced saved configuration");
}
File.Delete(path);
Console.WriteLine("PASS: independent per-app environments, old-list migration, one-time defaults and invalid region preservation");
if (args.Length == 2) {
    string fixtures = Path.Combine(folder, "脚本 & 测试");
    Directory.CreateDirectory(fixtures);
    foreach (string ext in new[] { ".cmd", ".bat", ".ps1", ".vbs", ".sh" }) {
        string script = Path.Combine(fixtures, "sample script" + ext);
        string body = ext switch {
            ".cmd" or ".bat" => "@echo off\r\n> result.txt echo %~1\r\n",
            ".ps1" => "Set-Content -LiteralPath result.txt -Value $args[0]\r\n",
            ".vbs" => "Set f = CreateObject(\"Scripting.FileSystemObject\").CreateTextFile(\"result.txt\", True)\r\nf.WriteLine WScript.Arguments(0)\r\nf.Close\r\n",
            _ => "printf '%s\\n' \"$1\" > result.txt\n"
        };
        File.WriteAllText(script, body);
        var entry = second with { Application = script, Arguments = "\"two words\"" };
        store.Save(new ApplicationList { Applications = [entry] });
        if (store.Load("", "").Applications.Single() != entry) throw new Exception("Script entry was changed");
        (string host, string command, string cwd) target;
        try { target = ApplicationLibrary.PrepareTarget(script, entry.Arguments); }
        catch (FileNotFoundException) when (ext == ".sh") {
            Console.WriteLine("SKIP: Git Bash unavailable.");
            continue;
        }
        if (target.cwd != fixtures) throw new Exception("Script working directory was not set");
        string result = Path.Combine(fixtures, "result.txt");
        File.Delete(result);
        string config = Path.Combine(fixtures, "run.ini");
        File.WriteAllText(config, $"[session]\r\nJob=Local\\RegionLauncher.{Guid.NewGuid():N}\r\nApplication={target.host}\r\nArguments={target.command}\r\nWorkingDirectory={target.cwd}\r\nLocale=en-US\r\nRegion=US\r\nGeo=244\r\nTimeZone=Pacific Standard Time\r\n", System.Text.Encoding.Unicode);
        var start = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(args[1])) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--locale-test"); start.ArgumentList.Add(config);
        using var session = System.Diagnostics.Process.Start(start)!;
        var error = session.StandardError.ReadToEndAsync();
        var output = session.StandardOutput.ReadToEndAsync();
        if (!session.WaitForExit(15000)) { session.Kill(true); throw new Exception("Script launch timed out"); }
        if (!output.Result.Contains("STARTED PID=")) throw new Exception($"Cannot start {ext} interpreter: {error.Result}");
        if (ext == ".ps1" && !File.Exists(result) && error.Result.Contains("PSSecurityException", StringComparison.Ordinal)) {
            Console.WriteLine("PASS: PowerShell policy can still block script execution."); continue;
        }
        if (session.ExitCode != 0 || !File.Exists(result) || File.ReadAllText(result).Trim() != "two words")
            throw new Exception($"{ext} did not run with expected directory and arguments: {error.Result}");
        Console.WriteLine($"PASS: {ext} runs through the session helper with a Unicode path, quoted argument and script working directory.");
    }
}
LinkBrowserTests.Run();
string shortcut = Path.Combine(folder, "中文 快捷方式.LNK");
string targetFile = Path.Combine(folder, "示例 程序.exe");
string workingFolder = Path.Combine(folder, "专用 工作目录");
File.WriteAllBytes(targetFile, []);
Directory.CreateDirectory(workingFolder);
dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
try {
    dynamic link = shell.CreateShortcut(shortcut);
    try {
        link.TargetPath = targetFile;
        link.Arguments = arguments;
        link.WorkingDirectory = workingFolder;
        link.Save();
    } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
} finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
var imported = ApplicationLibrary.ImportTarget(shortcut, japan);
store.Save(new ApplicationList { Applications = [imported] });
if (imported.Name != "中文 快捷方式" || imported.Application != targetFile || imported.Arguments != arguments ||
    imported.WorkingDirectory != workingFolder || imported.Environment != japan || store.Load("", "").Applications.Single() != imported ||
    ApplicationLibrary.PrepareTarget(imported.Application, imported.Arguments, imported.WorkingDirectory) != (targetFile, arguments, workingFolder) ||
    ApplicationLibrary.PrepareTarget(targetFile, arguments).WorkingDirectory != folder)
    throw new Exception("Shortcut metadata or working directory was lost");
File.Delete(targetFile);
try { ApplicationLibrary.ImportTarget(shortcut, japan); throw new Exception("Broken shortcut was accepted"); }
catch (FileNotFoundException) { }
Console.WriteLine("PASS: Windows shortcut imports and preserves Unicode paths, quoted arguments and a custom working directory; missing targets are rejected");
Console.WriteLine("PASS: legacy migration, Unicode/quoted arguments, two entries for one executable, edit/delete/reopen/selection, invalid save preservation, malformed file preservation, relative/environment paths");
