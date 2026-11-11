using System.Text.Json;

string folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
string path = Path.Combine(folder, "applications.json");
if (File.Exists(path)) File.Delete(path);
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
    new ApplicationList { Applications = [second with { Application = "bad.cmd" }] }
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
Console.WriteLine("PASS: legacy migration, Unicode/quoted arguments, two entries for one executable, edit/delete/reopen/selection, invalid save preservation, malformed file preservation, relative/environment paths");
