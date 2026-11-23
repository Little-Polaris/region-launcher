using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

sealed class RunningApplications : Panel
{
    sealed record Entry(string Name, Process Process)
    {
        public override string ToString() => Name;
    }

    readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "正在运行的程序" };
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 800 };
    readonly Label title = new() { Text = "正在运行 (0)", AutoSize = true, Dock = DockStyle.Top, Padding = new(0, 0, 0, 6) };

    public RunningApplications()
    {
        Dock = DockStyle.Right; Width = 150; Padding = new(0, 8, 16, 16);
        Controls.Add(list); Controls.Add(title);
        list.ContextMenuStrip = menu;
        var foreground = menu.Items.Add("切到前台", null, (_, _) => ActivateSelected());
        var copy = menu.Items.Add("复制进程 ID", null, (_, _) => {
            if (list.SelectedItem is Entry entry && !entry.Process.HasExited)
                try { Clipboard.SetText(entry.Process.Id.ToString()); } catch (ExternalException) { Feedback("剪贴板暂时不可用。"); }
        });
        menu.Items.Add(new ToolStripSeparator());
        var close = menu.Items.Add("退出程序", null, (_, _) => {
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
        timer.Tick += (_, _) => RemoveExited();
    }

    public void Track(string name, int pid, string jobName)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            using var job = OpenJobObjectW(4, false, jobName);
            // Keep the process handle, and never manage an unrelated or recycled PID.
            if (job.IsInvalid || !IsProcessInJob(process.SafeHandle, job, out bool member) || !member || process.HasExited) return;
            if (list.Items.Cast<Entry>().Any(entry => entry.Process.Id == pid && !entry.Process.HasExited)) return;
            list.Items.Add(new Entry(name, process));
            process = null;
            UpdateCount();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process?.Dispose(); }
    }

    void UpdateCount()
    {
        title.Text = $"正在运行 ({list.Items.Count})";
        timer.Enabled = list.Items.Count != 0;
    }

    void RemoveExited()
    {
        for (int i = list.Items.Count - 1; i >= 0; i--)
        {
            var entry = (Entry)list.Items[i];
            if (!entry.Process.HasExited) continue;
            list.Items.RemoveAt(i); entry.Process.Dispose();
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

    void Feedback(string text) => MessageBox.Show(FindForm(), text, "正在运行的程序", MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); menu.Dispose(); foreach (Entry item in list.Items) item.Process.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern SafeProcessHandle OpenJobObjectW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern bool IsProcessInJob(SafeProcessHandle process, SafeProcessHandle job, out bool member);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)] static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
