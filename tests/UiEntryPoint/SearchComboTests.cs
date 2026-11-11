using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class SearchComboTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
    static object Invoke(ComboBox box, string method, params object[] args) {
        var result = box.GetType().GetMethod(method, Hidden | BindingFlags.Public)!.Invoke(box, args)!;
        if (method == "OnTextUpdate") Application.DoEvents();
        return result;
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void CheckNativeEditing(ComboBox box, string settingsFile, string first, string second, string final) {
        Invoke(box, "ResetFilter");
        var all = box.Items.Cast<object>().ToArray(); var saved = box.SelectedItem;
        var original = File.ReadAllBytes(settingsFile);
        box.Focus(); Application.DoEvents();
        var edit = (NativeWindow)box.GetType().GetField("edit", Hidden)!.GetValue(box)!;
        void Key(char ch) { SendMessage(edit.Handle, 0x0102, ch, 1); Application.DoEvents(); }
        void Query(string text) {
            var expected = all.Where(item => (item is TimeZoneInfo zone ? zone.DisplayName + " " + zone.Id : item.ToString()!).Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            Check(box.Text == text && box.Items.Cast<object>().SequenceEqual(expected), $"Native editing mismatch: wanted '{text}', got '{box.Text}', matches {box.Items.Count}/{expected.Length}, selection {box.SelectionStart}+{box.SelectionLength}");
            Check(File.ReadAllBytes(settingsFile).SequenceEqual(original), "Editing changed saved environment");
        }
        string initial = box.Text;
        box.Select(initial.Length, 0);
        for (int remaining = initial.Length - 1; remaining >= 0; --remaining) { Key('\b'); Query(initial[..remaining]); }
        Query("");
        foreach (string search in new[] { first, second }) {
            for (int n = 0; n < search.Length; ++n) { Key(search[n]); Query(search[..(n + 1)]); }
            for (int n = search.Length - 1; n >= 0; --n) { Key('\b'); Query(search[..n]); }
        }
        foreach (char ch in final) Key(ch);
        Query(final);
        box.Select(0, 0);
        for (int n = 1; n <= final.Length; ++n) {
            SendMessage(edit.Handle, 0x0100, 0x2e, 1); SendMessage(edit.Handle, 0x0101, 0x2e, 1); Application.DoEvents(); Query(final[n..]);
        }
        foreach (char ch in final) Key(ch);
        Query(final);
        var expectedChoice = box.Items[0];
        var list = (NativeWindow)box.GetType().GetField("list", Hidden)!.GetValue(box)!;
        SendMessage(list.Handle, 0x0197, 0, 0);
        nint point = (nint)(((box.ItemHeight / 2) << 16) | 4);
        SendMessage(list.Handle, 0x0201, 1, point); SendMessage(list.Handle, 0x0202, 0, point); Application.DoEvents();
        Check(Equals(box.SelectedItem, expectedChoice) && !box.DroppedDown, "Mouse selection did not commit search result");
        Check(box.SelectionLength == 0, "Mouse selection restored blue full-text highlight");
        box.SelectedItem = saved; Invoke(box, "ResetFilter"); Application.DoEvents();
        Console.WriteLine("PASS: native character/backspace editing, repeated delete-to-empty and new query, mouse selection without blue highlight");
    }
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
        CheckNativeEditing(country, settingsFile, "US", "CN", "US");
        CheckNativeEditing(language, settingsFile, "en", "zh", "en-US");
        CheckNativeEditing(zone, settingsFile, "UTC", "Tokyo", "Tokyo");
        var original = File.ReadAllBytes(settingsFile);
        foreach (var (box, query) in new[] { (country, "US"), (language, "en-US"), (zone, "Pacific Standard") }) {
            int count = box.Items.Count; var selected = box.SelectedItem;
            Check(box.SelectionLength == 0, $"{box.Name}: saved value has automatic selection {box.SelectionStart}+{box.SelectionLength}, focus={box.Focused}");
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
