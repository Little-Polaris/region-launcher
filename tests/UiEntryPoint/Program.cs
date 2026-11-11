using System.Reflection;
using System.Text.Json;

if (args.Length == 4 && args[0] == "--single-fixture") { SingleInstanceTests.Fixture(args); return; }
if (args.Length > 0 && args[0] == "--tray-leaf") { Thread.Sleep(Timeout.Infinite); return; }
if (args.Length == 2 && args[0] == "--tray-fixture") {
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--tray-leaf") { UseShellExecute = false, CreateNoWindow = true })!;
    File.WriteAllText(args[1], $"{Environment.ProcessId},{child.Id}");
    Thread.Sleep(Timeout.Infinite); return;
}

var entry = Assembly.LoadFrom(Path.GetFullPath(args[0])).EntryPoint
    ?? throw new Exception("Missing application entry point");
if (entry.GetCustomAttribute<STAThreadAttribute>() == null)
    throw new Exception("The UI entry point must use STA for Windows file dialogs.");
Console.WriteLine("PASS: published UI entry point uses STA.");

var assembly = entry.DeclaringType!.Assembly;
SingleInstanceTests.Run(args[0], Path.Combine(AppContext.BaseDirectory, "test-data", "single-" + Guid.NewGuid().ToString("N")));
var catalog = assembly.GetType("WindowsSettings") ?? throw new Exception("Missing Windows settings catalog");
var regions = ((System.Collections.IEnumerable)catalog.GetMethod("Regions")!.Invoke(null, null)!).Cast<object>().ToList();
var languages = ((System.Collections.IEnumerable)catalog.GetMethod("Languages")!.Invoke(null, null)!).Cast<object>().ToList();
var zones = (List<TimeZoneInfo>)catalog.GetMethod("TimeZones")!.Invoke(null, null)!;
if (regions.Count < 200 || !regions.Any(r => (int)r.GetType().GetProperty("GeoId")!.GetValue(r)! == 244)) throw new Exception("Windows US country option missing");
if (languages.Count < 100 || !languages.Any(l => (string)l.GetType().GetProperty("Name")!.GetValue(l)! == "en-US")) throw new Exception("Windows en-US language option missing");
var pacific = zones.Single(z => z.Id == "Pacific Standard Time");
if (pacific.GetUtcOffset(new DateTime(2026,1,15)) != TimeSpan.FromHours(-8) || pacific.GetUtcOffset(new DateTime(2026,7,15)) != TimeSpan.FromHours(-7)) throw new Exception("Pacific DST rules mismatch");
Console.WriteLine($"PASS: Windows catalog: {regions.Count} countries, {languages.Count} languages, {zones.Count} time zones; US/en-US/Pacific DST available.");
Exception uiFailure = null;
var fixture = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
void Snapshot(System.Windows.Forms.Form form, string name) {
    if (args.Length < 2) return;
    Directory.CreateDirectory(args[1]);
    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
    bitmap.Save(Path.Combine(args[1], name));
}
Directory.CreateDirectory(fixture);
File.WriteAllText(Path.Combine(fixture, "profile.json"), JsonSerializer.Serialize(new { Application = Environment.ProcessPath!, Arguments = "--legacy=\"中文 参数\"" }));
object OpenLauncher() => Activator.CreateInstance(assembly.GetType("Launcher")!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { fixture }, null)!;
var ui = new Thread(() =>
{
    try
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = (System.Windows.Forms.Form)OpenLauncher();
        form.GetType().GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new[] { Activator.CreateInstance(assembly.GetType("LaunchEnvironment")!, true) });
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false;
        form.Show(); System.Windows.Forms.Application.DoEvents();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var links = (System.Windows.Forms.Form)Activator.CreateInstance(assembly.GetType("LinkBrowserDialog")!, new object[] { fixture, null })!) {
            links.StartPosition = System.Windows.Forms.FormStartPosition.Manual; links.Location = new(-32000, -32000); links.ShowInTaskbar = false;
            links.Show(); System.Windows.Forms.Application.DoEvents(); Snapshot(links, "link-browser.png"); links.Close();
        }
        var software = (System.Windows.Forms.ComboBox)form.GetType().GetField("software", flags)!.GetValue(form)!;
        var executable = (System.Windows.Forms.TextBox)form.GetType().GetField("executable", flags)!.GetValue(form)!;
        var arguments = (System.Windows.Forms.TextBox)form.GetType().GetField("arguments", flags)!.GetValue(form)!;
        if (software.Items.Count != 1 || arguments.Text != "--legacy=\"中文 参数\"" || !executable.ReadOnly || !arguments.ReadOnly)
            throw new Exception("Legacy app did not populate selection and details");
        var editorType = assembly.GetType("ApplicationEditor")!;
        using (var editor = (System.Windows.Forms.Form)Activator.CreateInstance(editorType, new object[] { null, null })!) {
            editor.StartPosition = System.Windows.Forms.FormStartPosition.Manual; editor.Location = new(-32000, -32000); editor.ShowInTaskbar = false;
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            ((System.Windows.Forms.TextBox)editorType.GetField("name", flags)!.GetValue(editor)!).Text = "第二个软件 🚀";
            ((System.Windows.Forms.TextBox)editorType.GetField("executable", flags)!.GetValue(editor)!).Text = Environment.ProcessPath!;
            ((System.Windows.Forms.TextBox)editorType.GetField("arguments", flags)!.GetValue(editor)!).Text = "--second=\"两个 参数\"";
            var editCountry = (System.Windows.Forms.ComboBox)editorType.GetField("country", flags)!.GetValue(editor)!;
            var editLanguage = (System.Windows.Forms.ComboBox)editorType.GetField("language", flags)!.GetValue(editor)!;
            var editZone = (System.Windows.Forms.ComboBox)editorType.GetField("timeZone", flags)!.GetValue(editor)!;
            editCountry.SelectedItem = editCountry.Items.Cast<object>().Single(r => (int)r.GetType().GetProperty("GeoId")!.GetValue(r)! == 122);
            editLanguage.SelectedItem = editLanguage.Items.Cast<object>().Single(l => (string)l.GetType().GetProperty("Name")!.GetValue(l)! == "ja-JP");
            editZone.SelectedItem = editZone.Items.Cast<TimeZoneInfo>().Single(z => z.Id == "Tokyo Standard Time");
            ((System.Windows.Forms.CheckBox)editorType.GetField("dst", flags)!.GetValue(editor)!).Checked = false;
            Snapshot(editor, "software-editor.png");
            ((System.Windows.Forms.Button)editorType.GetField("save", flags)!.GetValue(editor)!).PerformClick();
            if (editor.DialogResult != System.Windows.Forms.DialogResult.OK) throw new Exception("Software editor did not save");
            var item = editorType.GetProperty("Entry")!.GetValue(editor)!;
            form.GetType().GetMethod("SaveSoftware", flags)!.Invoke(form, new[] { item });
        }
        if (software.Items.Count != 2 || arguments.Text != "--second=\"两个 参数\"") throw new Exception("Added software not selected");
        using (var editor = (System.Windows.Forms.Form)Activator.CreateInstance(editorType, new object[] { software.SelectedItem!, null })!) {
            editor.StartPosition = System.Windows.Forms.FormStartPosition.Manual; editor.Location = new(-32000, -32000); editor.ShowInTaskbar = false;
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            ((System.Windows.Forms.TextBox)editorType.GetField("name", flags)!.GetValue(editor)!).Text = "编辑后的软件";
            ((System.Windows.Forms.Button)editorType.GetField("save", flags)!.GetValue(editor)!).PerformClick();
            form.GetType().GetMethod("SaveSoftware", flags)!.Invoke(form, new[] { editorType.GetProperty("Entry")!.GetValue(editor)! });
            if (software.Items.Count != 2 || software.SelectedItem!.ToString() != "编辑后的软件" || arguments.Text != "--second=\"两个 参数\"")
                throw new Exception("Editor duplicated entry or lost arguments");
        }
        Snapshot(form, "software-launcher.png");
        software.SelectedIndex = 0;
        if (arguments.Text != "--legacy=\"中文 参数\"") throw new Exception("Selection used another software's arguments");
        using (var reopened = (System.Windows.Forms.Form)OpenLauncher()) {
            var selected = (System.Windows.Forms.ComboBox)reopened.GetType().GetField("software", flags)!.GetValue(reopened)!;
            if (selected.Items.Count != 2 || selected.SelectedIndex != 0) throw new Exception("Software selection not restored on reopen");
        }
        var country = (System.Windows.Forms.ComboBox)form.GetType().GetField("country", flags)!.GetValue(form)!;
        var language = (System.Windows.Forms.ComboBox)form.GetType().GetField("language", flags)!.GetValue(form)!;
        var zone = (System.Windows.Forms.ComboBox)form.GetType().GetField("timeZone", flags)!.GetValue(form)!;
        var dst = (System.Windows.Forms.CheckBox)form.GetType().GetField("dst", flags)!.GetValue(form)!;
        SearchComboTests.Run(country, language, zone, Path.Combine(fixture, "applications.json"));
        software.SelectedIndex = 1;
        if (((TimeZoneInfo)zone.SelectedItem!).Id != "Tokyo Standard Time" || dst.Checked ||
            (int)country.SelectedItem!.GetType().GetProperty("GeoId")!.GetValue(country.SelectedItem)! != 122 ||
            (string)language.SelectedItem!.GetType().GetProperty("Name")!.GetValue(language.SelectedItem)! != "ja-JP") throw new Exception("Editor region settings not loaded on selection");
        country.SelectedItem = country.Items.Cast<object>().Single(r => (int)r.GetType().GetProperty("GeoId")!.GetValue(r)! == 94);
        language.SelectedItem = language.Items.Cast<object>().Single(l => (string)l.GetType().GetProperty("Name")!.GetValue(l)! == "de-DE");
        zone.SelectedItem = zone.Items.Cast<TimeZoneInfo>().Single(z => z.Id == "W. Europe Standard Time");
        dst.Checked = true;
        software.SelectedIndex = 0;
        if (((TimeZoneInfo)zone.SelectedItem!).Id != "Pacific Standard Time" || !dst.Checked) throw new Exception("One app changed another app's region");
        software.SelectedIndex = 1;
        if (!dst.Checked) throw new Exception("Main form changes were not saved to the app");
        using (var reopened = (System.Windows.Forms.Form)OpenLauncher()) {
            var restoredZone = (System.Windows.Forms.ComboBox)reopened.GetType().GetField("timeZone", flags)!.GetValue(reopened)!;
            var restoredCountry = (System.Windows.Forms.ComboBox)reopened.GetType().GetField("country", flags)!.GetValue(reopened)!;
            var restoredLanguage = (System.Windows.Forms.ComboBox)reopened.GetType().GetField("language", flags)!.GetValue(reopened)!;
            if (((TimeZoneInfo)restoredZone.SelectedItem!).Id != "W. Europe Standard Time" ||
                (int)restoredCountry.SelectedItem!.GetType().GetProperty("GeoId")!.GetValue(restoredCountry.SelectedItem)! != 94 ||
                (string)restoredLanguage.SelectedItem!.GetType().GetProperty("Name")!.GetValue(restoredLanguage.SelectedItem)! != "de-DE") throw new Exception("App environment lost after reopening");
        }
        software.SelectedIndex = 0;
        var start = (System.Windows.Forms.Button)form.GetType().GetField("start", flags)!.GetValue(form)!;
        if (start.Parent!.Controls.Cast<System.Windows.Forms.Control>().Count(c => c.Text.Contains("所选程序")) != 1)
            throw new Exception("Expected one program launch button");
        var update = form.GetType().GetMethod("UpdateActions", flags)!;
        var session = form.GetType().GetField("session", flags)!;
        var preparing = form.GetType().GetField("preparing", flags)!;
        update.Invoke(form, null);
        if (!start.Enabled) throw new Exception("Idle launch state incorrect");
        using var dummySession = new System.Diagnostics.Process();
        session.SetValue(form, dummySession); update.Invoke(form, null);
        if (!start.Enabled || !country.Enabled || !language.Enabled || !zone.Enabled || !dst.Enabled) throw new Exception("Existing environment must accept another program and its region settings");
        preparing.SetValue(form, true); update.Invoke(form, null);
        if (start.Enabled || country.Enabled || zone.Enabled) throw new Exception("Preparing must prevent changed launch settings");
        session.SetValue(form, null); preparing.SetValue(form, false); update.Invoke(form, null);
        // Showing the form must not reset explicitly selected defaults to the
        // first alphabetic item, regardless of a saved user profile.
        if ((int)country.SelectedItem!.GetType().GetProperty("GeoId")!.GetValue(country.SelectedItem)! != 244 ||
            (string)language.SelectedItem!.GetType().GetProperty("Name")!.GetValue(language.SelectedItem)! != "en-US" ||
            ((TimeZoneInfo)zone.SelectedItem!).Id != "Pacific Standard Time" || !dst.Checked)
            throw new Exception("Shown form lost the US/en-US/Pacific automatic-DST selections");
        var empty = Activator.CreateInstance(assembly.GetType("ApplicationList")!)!;
        form.GetType().GetMethod("SaveSoftwareList", flags)!.Invoke(form, new[] { empty });
        if (start.Enabled || software.Items.Count != 0 || executable.Text != "" || arguments.Text != "")
            throw new Exception("Deleting all software left a stale launch target");
        TrayTests.Run(form, Path.GetDirectoryName(Path.GetFullPath(args[0]))!, fixture);
        File.WriteAllText(Path.Combine(fixture, "applications.json"), "{malformed");
        using var broken = (System.Windows.Forms.Form)OpenLauncher();
        if (((System.Windows.Forms.Button)broken.GetType().GetField("start", flags)!.GetValue(broken)!).Enabled ||
            ((System.Windows.Forms.Button)broken.GetType().GetField("addSoftware", flags)!.GetValue(broken)!).Enabled ||
            File.ReadAllText(Path.Combine(fixture, "applications.json")) != "{malformed")
            throw new Exception("Malformed list was enabled or overwritten");
    }
    catch (Exception ex) { uiFailure = ex; }
});
ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
if (uiFailure != null) throw uiFailure;
var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data")) + Path.DirectorySeparatorChar;
if (!Path.GetFullPath(fixture).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test directory");
Directory.Delete(fixture, true);
Console.WriteLine("PASS: displayed form retains US/en-US/Pacific and automatic DST.");
Console.WriteLine("PASS: single launch button is available both idle and running; disabled while preparing.");
Console.WriteLine("PASS: software migration, add/edit dialogs, dropdown parameter switching, remembered selection, empty and malformed lists.");
Console.WriteLine("PASS: per-app country/language/timezone/DST editing, automatic saving and reopening; existing session keeps controls available.");
