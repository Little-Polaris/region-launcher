using System.Runtime.InteropServices;

sealed class SearchComboBox : ComboBox
{
    object[] options = [];
    object? value;
    bool updating, composing;
    int wheelRemainder, targetTop;
    readonly System.Windows.Forms.Timer scroll = new() { Interval = 16 };
    readonly ChildWindow list;
    readonly ChildWindow edit;
    public object? Value => value;

    public SearchComboBox() {
        DropDownStyle = ComboBoxStyle.DropDown; AutoCompleteMode = AutoCompleteMode.None;
        MaxDropDownItems = 10; IntegralHeight = false;
        list = new(this, false); edit = new(this, true);
        scroll.Tick += (_, _) => ScrollStep();
    }
    public void SetOptions(IEnumerable<object> items) {
        options = items.ToArray(); ResetFilter();
    }
    public void ResetFilter() {
        scroll.Stop(); wheelRemainder = 0;
        updating = true;
        try {
            BeginUpdate(); Items.Clear(); Items.AddRange(options); SelectedItem = value;
            Text = value?.ToString() ?? "";
        } finally { EndUpdate(); updating = false; ClearAutomaticSelection(); }
    }
    protected override void OnSelectedIndexChanged(EventArgs e) {
        if (updating || SelectedItem == null) return;
        value = SelectedItem; base.OnSelectedIndexChanged(e);
        ClearAutomaticSelection();
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed && !Focused) ClearAutomaticSelection(); });
    }
    void ClearAutomaticSelection() { SelectionStart = Text.Length; SelectionLength = 0; }
    protected override void OnEnter(EventArgs e) {
        base.OnEnter(e);
        BeginInvoke(() => { if (!IsDisposed && SelectedItem != null && !composing) ClearAutomaticSelection(); });
    }
    protected override void OnTextUpdate(EventArgs e) {
        base.OnTextUpdate(e);
        if (!updating && !composing) Filter();
    }
    static string SearchText(object item) => item is TimeZoneInfo zone ? zone.DisplayName + " " + zone.Id : item.ToString() ?? "";
    void Filter() {
        if (IsDisposed) return;
        string query = Text; int caret = SelectionStart, length = SelectionLength;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = options.Where(o => terms.All(t => SearchText(o).Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        scroll.Stop(); wheelRemainder = 0; updating = true;
        try {
            BeginUpdate(); Items.Clear(); Items.AddRange(matches); SelectedIndex = -1;
            Text = query; EndUpdate();
            if (Focused && matches.Length > 0) DroppedDown = true;
            SelectionStart = Math.Min(caret, Text.Length); SelectionLength = Math.Min(length, Text.Length - SelectionStart);
        } finally { updating = false; }
    }
    protected override void OnDropDown(EventArgs e) {
        DropDownHeight = ItemHeight * MaxDropDownItems + 2;
        base.OnDropDown(e); AttachChildren();
    }
    protected override void OnDropDownClosed(EventArgs e) {
        scroll.Stop(); base.OnDropDownClosed(e);
        if (!updating && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed && !DroppedDown) ResetFilter(); });
    }
    protected override void OnLeave(EventArgs e) { ResetFilter(); base.OnLeave(e); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (keyData == Keys.Escape) { DroppedDown = false; ResetFilter(); return true; }
        if (keyData == Keys.Enter && DroppedDown) {
            if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;
            DroppedDown = false; ResetFilter(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AttachChildren(); }
    void AttachChildren() {
        var info = new ComboInfo { size = Marshal.SizeOf<ComboInfo>() };
        if (GetComboBoxInfo(Handle, ref info)) { list.Attach(info.list); edit.Attach(info.edit); }
    }
    // Wheel browsing never selects a value or writes the application's settings.
    void Wheel(int delta) {
        if (!DroppedDown || list.Handle == 0) return;
        wheelRemainder += delta;
        int rows = wheelRemainder / 120 * 3; wheelRemainder %= 120;
        if (rows == 0) return;
        if (!scroll.Enabled) targetTop = (int)SendMessage(list.Handle, 0x018e, 0, 0); // LB_GETTOPINDEX
        targetTop = Math.Clamp(targetTop - rows, 0, Math.Max(0, Items.Count - MaxDropDownItems));
        scroll.Start();
    }
    void ScrollStep() {
        if (!DroppedDown || list.Handle == 0) { scroll.Stop(); return; }
        int top = (int)SendMessage(list.Handle, 0x018e, 0, 0);
        if (top == targetTop) { scroll.Stop(); return; }
        SendMessage(list.Handle, 0x0197, top + Math.Sign(targetTop - top), 0); // LB_SETTOPINDEX
        if ((int)SendMessage(list.Handle, 0x018e, 0, 0) == top) scroll.Stop();
    }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x020a) { Wheel((short)((long)m.WParam >> 16)); m.Result = 0; return; }
        base.WndProc(ref m);
        // Native CB_SETCURSEL selects the edit text after the managed selection event.
        if (m.Msg == 0x014e) ClearAutomaticSelection();
    }
    protected override void Dispose(bool disposing) {
        if (disposing) { scroll.Dispose(); list.ReleaseHandle(); edit.ReleaseHandle(); }
        base.Dispose(disposing);
    }
    sealed class ChildWindow(SearchComboBox owner, bool input) : NativeWindow {
        public void Attach(nint handle) { if (Handle == handle) return; ReleaseHandle(); if (handle != 0) AssignHandle(handle); }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x020a) { owner.Wheel((short)((long)m.WParam >> 16)); m.Result = 0; return; }
            if (m.Msg == 0x0020 && m.WParam == Handle && ((long)m.LParam & 0xffff) == 1) {
                Cursor.Current = input ? Cursors.IBeam : Cursors.Arrow;
                m.Result = 1; return;
            }
            if (input && m.Msg == 0x010d) owner.composing = true;
            base.WndProc(ref m);
            // Captured mouse moves may arrive without WM_SETCURSOR after typing hides the pointer.
            if (m.Msg == 0x0200 && GetClientRect(Handle, out var bounds)) {
                int x = (short)(long)m.LParam, y = (short)((long)m.LParam >> 16);
                if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
                    Cursor.Current = input ? Cursors.IBeam : Cursors.Arrow;
            }
            if (input && m.Msg == 0x010e) { owner.composing = false; if (owner.IsHandleCreated) owner.BeginInvoke(() => { if (owner.Focused) owner.Filter(); }); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint handle, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] struct ComboInfo { public int size; public Rectangle item, button; public int state; public nint combo, edit, list; }
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(nint handle, ref ComboInfo info);
    [DllImport("user32.dll")] static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
}
