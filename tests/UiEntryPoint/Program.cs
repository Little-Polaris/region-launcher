using System.Windows.Forms;
using System.Reflection;
using System.Text.Json;

// A fixture failure must reach the test runner, never leave a desktop error dialog.
System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException, false);

if (args.Length == 2 && args[0] == "--launch-result") {
    if (args[1].StartsWith("started")) { if (args[1] == "started-delay") Thread.Sleep(150); Console.WriteLine("STARTED PID=" + Environment.ProcessId); if (args[1] == "started-delay") Thread.Sleep(350); }
    else Console.Error.WriteLine("Application was not started: fixture rejection");
    Environment.ExitCode = 17; return;
}
if (args.Length == 4 && args[0] == "--single-fixture") { SingleInstanceTests.Fixture(args); return; }
if (args.Length == 3 && args[0] == "--single-test") { SingleInstanceTests.Run(args[1], args[2]); return; }
if (args.Length > 0 && args[0] == "--tray-leaf") { Thread.Sleep(Timeout.Infinite); return; }
if (args.Length == 2 && args[0] == "--tray-proxy") {
    var loopback = System.Net.IPAddress.Loopback;
    using var tcp = new System.Net.Sockets.TcpListener(loopback,0); tcp.Start();
    int port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
    using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(loopback,port));
    var dns = System.Net.IPAddress.Parse("127.0.0.154");
    using var dnsTcp = new System.Net.Sockets.TcpListener(dns,0); dnsTcp.Start();
    int dnsPort = ((System.Net.IPEndPoint)dnsTcp.LocalEndpoint).Port;
    using var dnsUdp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(dns,dnsPort));
    File.WriteAllText(args[1],$"{port},{dnsPort}");
    Thread.Sleep(Timeout.Infinite); return;
}
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
File.WriteAllText(Path.Combine(fixture, "profile.json"), JsonSerializer.Serialize(new { Application = Path.ChangeExtension(Path.GetFullPath(args[0]), ".exe"), Arguments = "--legacy=\"中文 参数\"" }));
object OpenLauncher() => Activator.CreateInstance(assembly.GetType("Launcher")!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { fixture }, null)!;
var ui = new Thread(() =>
{
    try
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = (System.Windows.Forms.Form)OpenLauncher();
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false;
        form.Show(); System.Windows.Forms.Application.DoEvents();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var links = (System.Windows.Forms.Form)Activator.CreateInstance(assembly.GetType("LinkBrowserDialog")!, new object[] { fixture, null })!) {
            links.StartPosition = System.Windows.Forms.FormStartPosition.Manual; links.Location = new(-32000, -32000); links.ShowInTaskbar = false;
            links.Show(); System.Windows.Forms.Application.DoEvents(); Snapshot(links, "link-browser.png"); links.Close();
        }
        FlowLayoutPanel Tiles(Form owner) => (FlowLayoutPanel)owner.GetType().GetField("tiles", flags)!.GetValue(owner)!;
        var tiles = Tiles(form);
        Button Tile(int index) => (Button)tiles.Controls[index];
        object Entry(int index) => Tile(index).Tag!;
        T Property<T>(object item, string name) => (T)item.GetType().GetProperty(name)!.GetValue(item)!;
        T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, flags)!.GetValue(instance)!;
        if (tiles.Controls.Count != 2 || Property<string>(Entry(0), "Arguments") != "--legacy=\"中文 参数\"" ||
            Tile(1).Text != "添加程序" || Tile(0).Image is not { Width: >= 48 } || Tile(0).ContextMenuStrip?.Items.Count != 5)
            throw new Exception($"Legacy entry or tile actions missing: tiles={tiles.Controls.Count}, icon={Tile(0).Image?.Size}, actions={Tile(0).ContextMenuStrip?.Items.Count}");
        if (form.Controls.OfType<TextBox>().Any() ||
            !Field<ContextMenuStrip>(form, "actions").Items.Cast<ToolStripItem>().Any(item => item.Text == "打开日志"))
            throw new Exception("Logs must remain available from the menu");
        var status = Field<Label>(form, "homeStatus");
        if (!status.Text.Contains("1 个程序") || !Tile(0).AccessibleDescription!.Contains("US · en-US") ||
            Field<Button>(form, "footer").Text != "查看运行日志") throw new Exception("Home card and status unavailable");
        var drag = form.GetType().GetMethod("DroppedExecutable", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Drop(params string[] paths) => drag.Invoke(null, new object[] {
            new DragEventArgs(new DataObject(DataFormats.FileDrop, paths), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None) });
        if (!Equals(Drop(Environment.ProcessPath!), Environment.ProcessPath) || Drop("relative.exe") != null ||
            Drop(Path.ChangeExtension(Environment.ProcessPath!, ".txt")) != null || Drop(Environment.ProcessPath!, Environment.ProcessPath!) != null)
            throw new Exception("Dropped file validation failed");
        var diagnostics = Field<object>(form, "diagnostics");
        var tail = (string)diagnostics.GetType().GetMethod("ReadTail", flags)!.Invoke(diagnostics, null)!;
        if (!tail.Contains("启动器版本 ") || !tail.Contains("请在右上角菜单选择")) throw new Exception("Run log does not expose the current diagnostic file");
        using (var log = (Form)form.GetType().GetMethod("CreateRunLog", flags)!.Invoke(form, null)!) {
            if (!log.Controls.OfType<TextBox>().Single().Text.Contains("启动器版本 ")) throw new Exception("Log dialog is not wired to diagnostics");
        }
        var editorType = assembly.GetType("ApplicationEditor")!;
        foreach (float fontSize in new[] { 10f, 15f, 20f }) {
            using var layoutEditor = (System.Windows.Forms.Form)Activator.CreateInstance(editorType, new object[] { null, null })!;
            layoutEditor.Font = new System.Drawing.Font("Microsoft YaHei UI", fontSize);
            layoutEditor.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            layoutEditor.Location = new(-32000, -32000); layoutEditor.ShowInTaskbar = false;
            layoutEditor.Show();
            foreach (var size in new[] { new System.Drawing.Size(740, 390), new System.Drawing.Size(680, 360) }) {
                layoutEditor.Size = size; System.Windows.Forms.Application.DoEvents();
                foreach (var button in new[] { (System.Windows.Forms.Control)layoutEditor.AcceptButton!, (System.Windows.Forms.Control)layoutEditor.CancelButton! }) {
                    var bounds = button.RectangleToScreen(button.ClientRectangle);
                    for (var parent = button.Parent; parent != null; parent = parent.Parent)
                        if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                            throw new Exception($"Editor button clipped at font {fontSize}, size {size}");
                }
            }
            Snapshot(layoutEditor, $"editor-layout-{fontSize}.png");
        }
        Console.WriteLine("PASS: editor save/cancel remain fully visible at compact sizes and 10/15/20-point fonts");
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
        if (tiles.Controls.Count != 3 || Property<string>(Entry(1), "Arguments") != "--second=\"两个 参数\"")
            throw new Exception("Added tile lost arguments");
        using (var editor = (Form)Activator.CreateInstance(editorType, new object[] { Entry(1), null })!) {
            editor.StartPosition = FormStartPosition.Manual; editor.Location = new(-32000, -32000); editor.ShowInTaskbar = false;
            editor.Show(); Application.DoEvents();
            var editCountry = Field<ComboBox>(editor, "country");
            var editLanguage = Field<ComboBox>(editor, "language");
            var editZone = Field<ComboBox>(editor, "timeZone");
            var editDst = Field<CheckBox>(editor, "dst");
            if (((TimeZoneInfo)editZone.SelectedItem!).Id != "Tokyo Standard Time" || editDst.Checked ||
                Property<int>(editCountry.SelectedItem!, "GeoId") != 122 || Property<string>(editLanguage.SelectedItem!, "Name") != "ja-JP")
                throw new Exception("Per-app settings not loaded in editor");
            Field<TextBox>(editor, "name").Text = "编辑后的软件";
            editCountry.SelectedItem = editCountry.Items.Cast<object>().Single(r => Property<int>(r, "GeoId") == 94);
            editLanguage.SelectedItem = editLanguage.Items.Cast<object>().Single(l => Property<string>(l, "Name") == "de-DE");
            editZone.SelectedItem = editZone.Items.Cast<TimeZoneInfo>().Single(z => z.Id == "W. Europe Standard Time");
            editDst.Checked = true;
            Field<Button>(editor, "save").PerformClick();
            form.GetType().GetMethod("SaveSoftware", flags)!.Invoke(form, new[] { Property<object>(editor, "Entry") });
            if (tiles.Controls.Count != 3 || Tile(1).Text != "编辑后的软件" || Property<string>(Entry(1), "Arguments") != "--second=\"两个 参数\"")
                throw new Exception("Edit duplicated tile or lost arguments");
        }
        using (var editor = (Form)Activator.CreateInstance(editorType, new object[] { Entry(0), null })!) {
            editor.StartPosition = FormStartPosition.Manual; editor.Location = new(-32000, -32000); editor.ShowInTaskbar = false;
            editor.Show(); Application.DoEvents();
            SearchComboTests.Run(Field<ComboBox>(editor, "country"), Field<ComboBox>(editor, "language"), Field<ComboBox>(editor, "timeZone"),
                Path.Combine(fixture, "applications.json"));
        }
        var contentBounds = Tile(0).GetType().GetMethod("ContentBounds", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (int dpi in new[] { 96, 120, 144, 192, 240 }) {
            foreach (bool constrained in new[] { false, true }) {
                int S(int n) => n * dpi / 96;
                var size = new System.Drawing.Size(S(142), constrained ? 142 : S(142));
                if (size.Height < S(73)) continue;
                var (icon, nameBox, subtitle) = ((System.Drawing.Rectangle, System.Drawing.Rectangle, System.Drawing.Rectangle))
                    contentBounds.Invoke(null, new object[] { size, dpi, S(20), S(17) })!;
                var card = new System.Drawing.Rectangle(System.Drawing.Point.Empty, size);
                if (!card.Contains(nameBox) || !card.Contains(subtitle) || icon.Bottom > nameBox.Top ||
                    nameBox.Bottom > subtitle.Top || subtitle.Bottom >= size.Height || icon.Width > S(48))
                    throw new Exception($"Card text is clipped or obscured at {dpi} DPI (size {size})");
            }
        }
        Snapshot(form, "software-launcher.png");
        var originalSize = form.Size;
        form.Size = form.MinimumSize; Application.DoEvents();
        if (Tile(2).Top <= Tile(0).Top || !tiles.AutoScroll || !tiles.AllowDrop)
            throw new Exception("Cards cannot wrap or scroll in a narrow window");
        var header = form.Controls.OfType<TableLayoutPanel>().Single();
        var quick = header.Controls.OfType<FlowLayoutPanel>().Single();
        var title = header.Controls.OfType<Label>().Single(label => label.Text == "Region Launcher");
        using (var largerFont = new System.Drawing.Font(form.Font.FontFamily, 16f)) {
            form.Font = largerFont;
            Application.DoEvents();
            if (header.GetRow(quick) != 2 || !header.ClientRectangle.Contains(quick.Bounds) ||
                title.Bounds.IntersectsWith(quick.Bounds) || !tiles.ClientRectangle.Contains(Tile(0).Bounds) ||
                Tile(0).Size != Tile(0).GetPreferredSize(System.Drawing.Size.Empty))
                throw new Exception("Compact window did not adapt to larger fonts");
            Snapshot(form, "software-launcher-large-font.png");
            form.Font = new System.Drawing.Font(largerFont.FontFamily, 10f);
        }
        form.Size = originalSize; Application.DoEvents();
        Console.WriteLine($"PASS: card layout at 100%-250%, including constrained cards and larger fonts on a {form.DeviceDpi} DPI desktop");
        using (var reopened = (Form)OpenLauncher()) {
            var restored = Tiles(reopened);
            var settings = Property<object>(restored.Controls[1].Tag!, "Environment");
            var original = Property<object>(restored.Controls[0].Tag!, "Environment");
            if (restored.Controls.Count != 3 || Property<string>(settings, "TimeZone") != "W. Europe Standard Time" ||
                Property<int>(settings, "GeoId") != 94 || Property<string>(settings, "Locale") != "de-DE" || !Property<bool>(settings, "AutoDst") ||
                Property<string>(original, "TimeZone") != "Pacific Standard Time" || Property<int>(original, "GeoId") != 244 ||
                Property<string>(original, "Locale") != "en-US" || !Property<bool>(original, "AutoDst"))
                throw new Exception("Per-app settings did not persist independently");
        }
        var update = form.GetType().GetMethod("UpdateActions", flags)!;
        var session = form.GetType().GetField("session", flags)!;
        var preparing = form.GetType().GetField("preparing", flags)!;
        update.Invoke(form, null);
        if (!Tile(0).Enabled) throw new Exception("Idle launch state incorrect");
        using var dummySession = new System.Diagnostics.Process();
        session.SetValue(form, dummySession); update.Invoke(form, null);
        if (!Tile(0).Enabled || !status.Text.Contains("环境运行中")) throw new Exception("Existing environment must accept another program");
        preparing.SetValue(form, true); update.Invoke(form, null);
        if (!status.Text.Contains("正在准备") || tiles.Controls.Cast<Control>().Any(control => control.Enabled)) throw new Exception("Preparing must disable app and add tiles");
        session.SetValue(form, null); preparing.SetValue(form, false); update.Invoke(form, null);
        LaunchFailureTests.CheckTile(form, Tile(1), fixture);
        var empty = Activator.CreateInstance(assembly.GetType("ApplicationList")!)!;
        form.GetType().GetMethod("SaveSoftwareList", flags)!.Invoke(form, new[] { empty });
        if (tiles.Controls.Count != 1 || !Tile(0).Enabled || Tile(0).Text != "添加程序")
            throw new Exception("Empty library must display only the add tile");
        LaunchFailureTests.Run(form);
        TrayTests.Run(form, Path.GetDirectoryName(Path.GetFullPath(args[0]))!, fixture);
        File.WriteAllText(Path.Combine(fixture, "applications.json"), "{malformed");
        using var broken = (System.Windows.Forms.Form)OpenLauncher();
        if (Tiles(broken).Enabled ||
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
Console.WriteLine("PASS: app tiles, migration, per-app settings, add/edit persistence, empty and malformed lists.");
Console.WriteLine("PASS: tiles available idle and running, disabled while preparing; background logs are available through the menu.");
