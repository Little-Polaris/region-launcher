using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

sealed class RunningApplications : Panel
{
    sealed record Entry(string Name, Process Process, Bitmap? Icon, DateTime Started, string Job)
    {
        public override string ToString() => Name;
    }

    readonly ModernList list = new() { Dock = DockStyle.Fill, AccessibleName = "正在运行的程序",
        EmptyText = "暂无运行中的程序", EmptyHint = "点击左侧卡片启动" };
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 800 };
    readonly Label title = new() { Text = "正在运行", AutoSize = true, Dock = DockStyle.Top, Padding = new(2, 0, 0, 8),
        ForeColor = HomeTheme.Text, UseMnemonic = false };
    readonly AntdUI.Tag count = new() { Text = "0", AutoSizeMode = AntdUI.TAutoSize.Auto, Radius = 10, Anchor = AnchorStyles.Top | AnchorStyles.Right };

    public RunningApplications()
    {
        Dock = DockStyle.Right; Width = 220; Margin = Padding.Empty;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = HomeTheme.Background;
        title.Font = new(HomeTheme.FontName, 10.5f, FontStyle.Bold);
        title.BackColor = HomeTheme.Surface; count.BackColor = HomeTheme.Surface;
        list.Detail = item => item is Entry entry ? Describe(entry) : null;
        list.ItemIcon = item => (item as Entry)?.Icon;
        list.Dot = item => item is Entry { Process.HasExited: false } ? HomeTheme.Success : HomeTheme.Subtle;
        list.Refit();
        Controls.Add(list); Controls.Add(title); Controls.Add(count); count.BringToFront();
        list.ContextMenuStrip = menu;
        HomeTheme.Apply(menu);
        Image? MenuIcon(string name, Color? color = null) => HomeTheme.Icon(name, LogicalToDeviceUnits(16), color ?? HomeTheme.Muted);
        var foreground = menu.Items.Add("切到前台", MenuIcon("ExportOutlined", HomeTheme.Accent), (_, _) => ActivateSelected());
        var copy = menu.Items.Add("复制进程 ID", MenuIcon("CopyOutlined"), (_, _) => {
            if (list.SelectedItem is Entry entry && !entry.Process.HasExited)
                try { Clipboard.SetText(entry.Process.Id.ToString()); } catch (ExternalException) { Feedback("剪贴板暂时不可用。"); }
        });
        menu.Items.Add(new ToolStripSeparator());
        var close = menu.Items.Add("退出程序", MenuIcon("PoweroffOutlined", HomeTheme.Danger), (_, _) => {
            if (Window() is var window && window != IntPtr.Zero && !PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
                Feedback("无法向程序发送关闭请求。");
        });
        menu.Opening += (_, e) => {
            e.Cancel = list.SelectedItem is not Entry;
            foreground.Enabled = close.Enabled = Window() != IntPtr.Zero;
            copy.Enabled = !e.Cancel;
        };
        list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) list.SelectedIndex = list.IndexFromPoint(e.Location); };
        list.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && list.IndexFromPoint(e.Location) >= 0) ActivateSelected(); };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ActivateSelected(); } };
        timer.Tick += (_, _) => { RemoveExited(); list.Invalidate(); };
    }

    static string Describe(Entry entry)
    {
        var elapsed = DateTime.Now - entry.Started;
        string age = elapsed.TotalMinutes < 1 ? "刚刚启动" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分钟"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分" : $"{(int)elapsed.TotalDays} 天";
        return $"PID {entry.Process.Id} · {age}";
    }

    static Bitmap? ProcessIcon(Process process, int size)
    {
        try {
            string? path = process.MainModule?.FileName;
            if (path == null) return null;
            using var icon = Icon.ExtractIcon(path, 0, size);
            return icon?.ToBitmap();
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public void FitWidth()
    {
        ScaleSpacing();
        Width = Math.Max(LogicalToDeviceUnits(220), title.PreferredWidth + count.Width + Padding.Horizontal + LogicalToDeviceUnits(24));
    }

    // Padding is the card margin plus the card's inner spacing, in device pixels for the current DPI
    // (the form does not rescale it), so the list never covers the card border.
    void ScaleSpacing()
    {
        int S(int n) => LogicalToDeviceUnits(n);
        var padding = new Padding(S(CardMargin.Left + 14), S(CardMargin.Top + 14), S(CardMargin.Right + 8), S(CardMargin.Bottom + 8));
        if (Padding != padding) Padding = padding;
        var titlePadding = new Padding(S(2), 0, 0, S(8));
        if (title.Padding != titlePadding) title.Padding = titlePadding;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        ScaleSpacing();
        base.OnLayout(levent);
        count.Location = new(ClientSize.Width - Padding.Right - count.Width - LogicalToDeviceUnits(6), title.Top + Math.Max(0, (title.Height - title.Padding.Bottom - count.Height) / 2));
    }

    // Card margin inside the panel; Padding adds the card's inner spacing on top of it.
    static readonly Padding CardMargin = new(4, 8, 20, 16);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int S(int n) => LogicalToDeviceUnits(n);
        var card = new RectangleF(S(CardMargin.Left) + 0.5f, S(CardMargin.Top) + 0.5f,
            ClientSize.Width - S(CardMargin.Horizontal) - 1f, ClientSize.Height - S(CardMargin.Vertical) - 1f);
        HomeTheme.Card(e.Graphics, card, S(12), HomeTheme.Surface, HomeTheme.Border, 3);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        FitWidth();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        FitWidth();
    }

    public void Track(string name, int pid, string jobName)
    {
        // Some programs (for example Claude) start through a stub that launches the real app and exits at once;
        // follow it to the app it started inside the same environment.
        var since = DateTime.Now.AddMinutes(-2);
        var process = Member(pid, jobName, since) ?? Successor(pid, jobName, since);
        if (process == null) return;
        if (Tracked(process.Id)) { process.Dispose(); return; }
        list.Items.Add(new Entry(name, process, ProcessIcon(process, LogicalToDeviceUnits(32)), StartTime(process), jobName));
        UpdateCount();
    }

    bool Tracked(int pid) => list.Items.Cast<Entry>().Any(entry => entry.Process.Id == pid && !entry.Process.HasExited);

    static DateTime StartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return DateTime.Now; }
    }

    // A live process with this PID inside the environment's job that started no earlier than `since`;
    // never manage an unrelated or recycled PID.
    static Process? Member(int pid, string jobName, DateTime since)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            using var job = OpenJobObjectW(4, false, jobName);
            if (job.IsInvalid || !IsProcessInJob(process.SafeHandle, job, out bool member) || !member || process.HasExited ||
                StartTime(process) < since.AddSeconds(-2)) return null;
            var result = process; process = null; return result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally { process?.Dispose(); }
    }

    // The nearest live, untracked descendant of an exited process in the same job: the app a launcher stub handed over to.
    Process? Successor(int pid, string jobName, DateTime since)
    {
        var parents = ParentMap();
        var level = new List<int> { pid };
        var seen = new HashSet<int> { pid };
        for (int depth = 0; depth < 4 && level.Count != 0; depth++)
        {
            var children = parents.Where(p => level.Contains(p.Value) && seen.Add(p.Key)).Select(p => p.Key).ToList();
            var found = children.Select(child => Member(child, jobName, since)).OfType<Process>().OrderBy(StartTime).ToList();
            var pick = found.FirstOrDefault(p => !Tracked(p.Id));
            foreach (var other in found) if (other != pick) other.Dispose();
            if (pick != null) return pick;
            level = children;
        }
        return null;
    }

    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return map;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        return map;
    }

    void UpdateCount()
    {
        count.Text = list.Items.Count.ToString();
        count.Type = list.Items.Count == 0 ? AntdUI.TTypeMini.Default : AntdUI.TTypeMini.Success;
        FitWidth(); PerformLayout();
        timer.Enabled = list.Items.Count != 0;
    }

    void RemoveExited()
    {
        for (int i = list.Items.Count - 1; i >= 0; i--)
        {
            var entry = (Entry)list.Items[i];
            if (!entry.Process.HasExited) continue;
            if (Successor(entry.Process.Id, entry.Job, entry.Started) is { } next)
            {
                var icon = ProcessIcon(next, LogicalToDeviceUnits(32));
                list.Items[i] = entry with { Process = next, Icon = icon ?? entry.Icon };
                entry.Process.Dispose(); if (icon != null) entry.Icon?.Dispose();
                continue;
            }
            list.Items.RemoveAt(i); entry.Process.Dispose(); entry.Icon?.Dispose();
        }
        UpdateCount();
    }

    IntPtr Window()
    {
        if (list.SelectedItem is not Entry entry || entry.Process.HasExited) return IntPtr.Zero;
        entry.Process.Refresh();
        var window = entry.Process.MainWindowHandle;
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint owner) != 0 &&
            owner == (uint)entry.Process.Id && !entry.Process.HasExited ? window : IntPtr.Zero;
    }

    void ActivateSelected()
    {
        var window = Window();
        if (window == IntPtr.Zero) { Feedback("这个实例暂时没有可切换的窗口。"); return; }
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        if (!SetForegroundWindow(window)) Feedback("Windows 未允许将此窗口切到前台。");
    }

    void Feedback(string text) => MessageDialog.Show(FindForm(), text, "正在运行的程序", MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); menu.Dispose(); foreach (Entry item in list.Items) { item.Process.Dispose(); item.Icon?.Dispose(); } }
        base.Dispose(disposing);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern SafeProcessHandle OpenJobObjectW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern bool IsProcessInJob(SafeProcessHandle process, SafeProcessHandle job, out bool member);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int PriorityClassBase; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)] static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
