using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class SearchComboTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
    static object Invoke(ComboBox box, string method, params object[] args) => box.GetType().GetMethod(method, Hidden | BindingFlags.Public)!.Invoke(box, args)!;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void CheckPointer(ComboBox box) {
        var previous = Cursor.Current;
        try {
            foreach (string part in new[] { "edit", "list" }) {
                var window = (NativeWindow)box.GetType().GetField(part, Hidden)!.GetValue(box)!;
                Cursor.Current = null;
                SendMessage(window.Handle, 0x0020, window.Handle, (nint)((0x0200 << 16) | 1));
                nint expected = (part == "edit" ? Cursors.IBeam : Cursors.Arrow).Handle;
                Check(Cursor.Current?.Handle == expected, $"{box.Name} {part}: pointer not restored after filtering");
                Cursor.Current = null;
                SendMessage(window.Handle, 0x0200, 0, (nint)((1 << 16) | 1));
                Check(Cursor.Current?.Handle == expected, $"{box.Name} {part}: pointer not restored by captured mouse move");
                Cursor.Current = Cursors.Cross;
                SendMessage(window.Handle, 0x0200, 0, (nint)(-1));
                Check(Cursor.Current?.Handle == Cursors.Cross.Handle, "Captured movement outside selector must not override another cursor");
            }
        } finally { Cursor.Current = previous; }
    }
    public static void Run(ComboBox country, ComboBox language, ComboBox zone, string settingsFile) {
        Application.DoEvents();
        var original = File.ReadAllBytes(settingsFile);
        foreach (var (box, query) in new[] { (country, "US"), (language, "en-US"), (zone, "Pacific Standard") }) {
            int count = box.Items.Count; var selected = box.SelectedItem;
            Check(box.SelectionLength == 0, "Saved values should not have automatic blue selection");
            Check(box.DropDownStyle == ComboBoxStyle.DropDown, "Search input must be editable");
            box.Focus();
            box.Text = query; box.SelectionStart = query.Length; Invoke(box, "OnTextUpdate", EventArgs.Empty);
            Check(box.Items.Count > 0 && box.Items.Count < count, "Live filter did not reduce results: " + query);
            Check(box.DroppedDown, "Typing should open matching suggestions");
            CheckPointer(box);
            Check(box.Text == query && box.SelectionStart == query.Length, "Filtering changed input or caret");
            Check(Equals(box.GetType().GetProperty("Value")!.GetValue(box), selected), "Search changed committed setting");
            Check(File.ReadAllBytes(settingsFile).SequenceEqual(original), "Typing saved an incomplete setting");
            box.Text = "no-match-123456"; Invoke(box, "OnTextUpdate", EventArgs.Empty);
            Check(box.Items.Count == 0, "Unmatched query must have no options");
            Invoke(box, "OnLeave", EventArgs.Empty);
            Check(box.Items.Count == count && Equals(box.SelectedItem, selected), "Leaving search lost saved selection");
            Check(box.SelectionLength == 0, "Leaving search kept a blue text selection");
            box.Text = ""; Invoke(box, "OnTextUpdate", EventArgs.Empty);
            Check(box.Items.Count == count, "Empty search must show all options");
            box.DroppedDown = false; Application.DoEvents();
            Invoke(box, "ResetFilter");
        }
        var priorCountry = country.SelectedItem;
        country.Focus(); country.Text = "JP"; Invoke(country, "OnTextUpdate", EventArgs.Empty);
        var message = Message.Create(country.Handle, 0x0100, (nint)Keys.Enter, 0);
        Check((bool)Invoke(country, "ProcessCmdKey", message, Keys.Enter), "Enter should select a suggestion");
        Check(country.SelectedItem!.ToString()!.Contains("(JP)"), "Enter selected wrong filtered result");
        country.SelectedItem = priorCountry;
        country.Focus(); Application.DoEvents();
        Check(country.SelectionLength == 0, "Focusing a saved value automatically selected all text");
        country.SelectAll(); Check(country.SelectionLength == country.Text.Length, "Manual text selection must still work");
        Invoke(country, "ResetFilter");
        Check(File.ReadAllBytes(settingsFile).SequenceEqual(original), "Search selection did not restore correctly");
        // Exercise the actual native dropdown wheel path, including high-resolution deltas.
        var value = language.SelectedItem;
        language.DroppedDown = true; Application.DoEvents();
        var window = (NativeWindow)language.GetType().GetField("list", Hidden)!.GetValue(language)!;
        SendMessage(window.Handle, 0x0197, 0, 0);
        SendMessage(window.Handle, 0x020a, (nint)(-60 << 16), 0);
        Invoke(language, "ScrollStep");
        Check((int)SendMessage(window.Handle, 0x018e, 0, 0) == 0, "Half notch must accumulate");
        SendMessage(window.Handle, 0x020a, (nint)(-60 << 16), 0);
        Invoke(language, "ScrollStep");
        Check((int)SendMessage(window.Handle, 0x018e, 0, 0) == 1, "First wheel frame must advance one row");
        Invoke(language, "ScrollStep"); Invoke(language, "ScrollStep");
        Check((int)SendMessage(window.Handle, 0x018e, 0, 0) == 3, "One notch must advance three rows");
        Check(Equals(language.SelectedItem, value) && File.ReadAllBytes(settingsFile).SequenceEqual(original), "Wheel changed saved selection");
        language.DroppedDown = false; Application.DoEvents(); Invoke(language, "ResetFilter");
        Console.WriteLine("PASS: inline search, caret, empty/no-match recovery, settings preservation, native wheel steps; edit/list pointer restoration on cursor and captured-move messages, outside cursor preserved");
    }
}
