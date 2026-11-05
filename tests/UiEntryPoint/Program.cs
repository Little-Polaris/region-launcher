using System.Reflection;

var entry = Assembly.LoadFrom(Path.GetFullPath(args[0])).EntryPoint
    ?? throw new Exception("Missing application entry point");
if (entry.GetCustomAttribute<STAThreadAttribute>() == null)
    throw new Exception("The UI entry point must use STA for Windows file dialogs.");
Console.WriteLine("PASS: published UI entry point uses STA.");

var assembly = entry.DeclaringType!.Assembly;
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
var ui = new Thread(() =>
{
    try
    {
        using var form = (System.Windows.Forms.Form)Activator.CreateInstance(assembly.GetType("Launcher")!, true)!;
        form.GetType().GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new[] { Activator.CreateInstance(assembly.GetType("Profile")!, true) });
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000); form.ShowInTaskbar = false;
        form.Show(); System.Windows.Forms.Application.DoEvents();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var country = (System.Windows.Forms.ComboBox)form.GetType().GetField("country", flags)!.GetValue(form)!;
        var language = (System.Windows.Forms.ComboBox)form.GetType().GetField("language", flags)!.GetValue(form)!;
        var zone = (System.Windows.Forms.ComboBox)form.GetType().GetField("timeZone", flags)!.GetValue(form)!;
        var dst = (System.Windows.Forms.CheckBox)form.GetType().GetField("dst", flags)!.GetValue(form)!;
        // Showing the form must not reset explicitly selected defaults to the
        // first alphabetic item, regardless of a saved user profile.
        if ((int)country.SelectedItem!.GetType().GetProperty("GeoId")!.GetValue(country.SelectedItem)! != 244 ||
            (string)language.SelectedItem!.GetType().GetProperty("Name")!.GetValue(language.SelectedItem)! != "en-US" ||
            ((TimeZoneInfo)zone.SelectedItem!).Id != "Pacific Standard Time" || !dst.Checked)
            throw new Exception("Shown form lost the US/en-US/Pacific automatic-DST selections");
        form.Close();
    }
    catch (Exception ex) { uiFailure = ex; }
});
ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
if (uiFailure != null) throw uiFailure;
Console.WriteLine("PASS: displayed form retains US/en-US/Pacific and automatic DST.");
