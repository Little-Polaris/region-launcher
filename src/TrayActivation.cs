using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

static class TrayActivation
{
    [StructLayout(LayoutKind.Sequential)]
    struct IconId { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId icon, out Rect bounds);

    public enum Result { Invoked, Missing }

    internal static nint Overflow()
    {
        foreach (string cls in new[] { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" })
        {
            nint handle = FindWindow(cls, null);
            if (handle != 0 && IsWindowVisible(handle)) return handle;
        }
        return 0;
    }

    // Explorer owns the accessibility buttons. Bind their coordinates to a registered
    // notification icon of this process before invoking any of them.
    static bool TryInvoke(Process process, nint area, IReadOnlyCollection<uint> ids)
    {
        if (area == 0 || process.HasExited) return false;
        var buttons = AutomationElement.FromHandle(area).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon"));
        if (buttons.Count == 0) return false;
        var candidates = new List<(uint Id, nint Window, AutomationElement Button)>();
        var windows = new List<nint>();
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == process.Id) windows.Add(window);
            return true;
        }, 0);
        foreach (var window in windows)
        {
            foreach (uint id in ids)
            {
                var identity = new IconId { Size = (uint)Marshal.SizeOf<IconId>(), Window = window, Id = id };
                if (Shell_NotifyIconGetRect(ref identity, out var r) != 0 || r.Right <= r.Left || r.Bottom <= r.Top) continue;
                foreach (AutomationElement item in buttons)
                    if (!item.Current.IsOffscreen && item.Current.BoundingRectangle.Contains(
                        (r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0)) candidates.Add((id, window, item));
            }
        }
        if (candidates.Count != 1 || process.HasExited) return false;
        var candidate = candidates[0];
        GetWindowThreadProcessId(candidate.Window, out uint owner);
        var icon = new IconId { Size = (uint)Marshal.SizeOf<IconId>(), Window = candidate.Window, Id = candidate.Id };
        if (owner != process.Id || Shell_NotifyIconGetRect(ref icon, out var rect) != 0 ||
            !candidate.Button.Current.BoundingRectangle.Contains((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0) ||
            !candidate.Button.TryGetCurrentPattern(InvokePattern.Pattern, out var action)) return false;
        ((InvokePattern)action).Invoke();
        return true;
    }

    static HashSet<uint> RegisteredIds(Process process)
    {
        var ids = Enumerable.Range(0, 9).Select(i => (uint)i).ToHashSet();
        using var settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (settings == null) return ids;
        string executable = process.ProcessName + ".exe";
        foreach (var name in settings.GetSubKeyNames())
        {
            using var item = settings.OpenSubKey(name);
            if (item?.GetValue("UID") is int id && item.GetValue("ExecutablePath") is string path &&
                string.Equals(Path.GetFileName(path), executable, StringComparison.OrdinalIgnoreCase))
                ids.Add(unchecked((uint)id));
        }
        return ids;
    }

    public static Result Open(Process process)
    {
        try
        {
            nint taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == 0 || process.HasExited) return Result.Missing;
            var ids = RegisteredIds(process);
            if (TryInvoke(process, taskbar, ids)) return Result.Invoked;
            return TryInvoke(process, Overflow(), ids) ? Result.Invoked : Result.Missing;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            return Result.Missing;
        }
    }
}
