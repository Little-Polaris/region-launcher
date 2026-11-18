using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class LaunchFailureTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Pump(Task task) {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompleted, "Launch failure test timed out");
    }
    public static void CheckTile(Form form, Button tile, string data) {
        using var timer = new System.Windows.Forms.Timer { Interval = 40 };
        bool notified = false;
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "启动失败") { notified = true; PostMessage(window, 0x0010, 0, 0); }
            return true;
        }, 0);
        timer.Start();
        tile.PerformClick();
        timer.Stop();
        Check(notified, "Clicking an app tile did not attempt to launch it");
        string logs = string.Concat(Directory.GetFiles(Path.Combine(data, "logs"), "*.log").Select(path => {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }));
        Check(logs.Contains("请求启动软件：" + tile.Text + "；"), "Clicked tile did not launch the matching entry or write its background log");
        Console.WriteLine("PASS: clicking an app tile routes the matching entry to the launch path and writes a background log.");
    }
    public static void Run(Form form) {
        var type = form.GetType();
        var tray = (NotifyIcon)type.GetField("tray", Hidden)!.GetValue(form)!;
        int dialogs = 0;
        List<string> checkDialogs = [];
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "启动失败") { dialogs++; PostMessage(window, 0x0010, 0, 0); }
            if (title.ToString().StartsWith("检测并修复：")) { checkDialogs.Add(title.ToString()); PostMessage(window, 0x0010, 0, 0); }
            return true;
        }, 0);
        timer.Start();
        // Native helper rejection must fail, but a started app's nonzero exit is not a startup failure.
        foreach (bool started in new[] { false, true }) {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.ArgumentList.Add("--launch-result"); psi.ArgumentList.Add(started ? "started" : "failed");
            using var process = Process.Start(psi)!;
            var task = (Task)type.GetMethod("MonitorLaunch", Hidden)!.Invoke(form, new object[] { process, null })!;
            Pump(task);
            Check(started ? task.IsCompletedSuccessfully : task.Exception?.InnerException is IOException, "Wrong launch result classification");
        }
        form.Hide();
        // A retiring application must not release the next operation's preparation state.
        var begin = type.GetMethod("BeginPreparation", Hidden)!;
        var end = type.GetMethod("EndPreparation", Hidden)!;
        object[] first = { 0L }; Check((bool)begin.Invoke(form, first)!, "First preparation rejected");
        using (var delayed = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--launch-result started-delay") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!) {
            object[] next = { 0L }; bool acknowledged = false;
            Action ready = () => { acknowledged = true; end.Invoke(form, first); Check((bool)begin.Invoke(form, next)!, "Next preparation blocked after STARTED"); };
            var monitor = (Task)type.GetMethod("MonitorLaunch", Hidden)!.Invoke(form, new object[] { delayed, ready })!;
            Check((bool)type.GetField("preparing", Hidden)!.GetValue(form)!, "Released preparation before STARTED");
            Pump(monitor); monitor.GetAwaiter().GetResult(); end.Invoke(form, first);
            Check(acknowledged && (bool)type.GetField("preparing", Hidden)!.GetValue(form)!, "Old exit cleared new preparation");
            object[] overlap = { 0L }; Check(!(bool)begin.Invoke(form, overlap)!, "Overlapping check accepted");
            Check(!((ToolStripMenuItem)type.GetField("checkProxy", Hidden)!.GetValue(form)!).Enabled, "Manual check enabled during preparation");
            end.Invoke(form, next);
        }
        Console.WriteLine("PASS: startup acknowledgement gates preparation; old process exit preserves newer preparation and rejects overlapping checks");
        type.GetMethod("NotifyLaunchFailure", Hidden)!.Invoke(form, new object[] { "测试程序" });
        Check(dialogs == 0, "Hidden failure opened a modal dialog");
        Check(!form.Visible && tray.BalloonTipTitle.Contains("启动失败") && tray.BalloonTipText.Contains("测试程序") && tray.BalloonTipIcon == ToolTipIcon.Error,
            "Hidden launch failure did not request an error notification");
        tray.GetType().GetMethod("OnBalloonTipClicked", Hidden)!.Invoke(tray, null);
        Check(form.Visible && form.WindowState != FormWindowState.Minimized, "Notification click did not restore launcher");
        form.WindowState = FormWindowState.Minimized;
        type.GetMethod("NotifyLaunchFailure", Hidden)!.Invoke(form, new object[] { "最小化测试" });
        Check(tray.BalloonTipText.Contains("最小化测试"), "Minimized failure did not request a notification");
        Check(dialogs == 0, "Minimized failure opened a modal dialog");
        form.WindowState = FormWindowState.Normal;
        // This is a real owner-modal dialog; the fixture closes only its own test thread's window.
        type.GetMethod("NotifyLaunchFailure", Hidden)!.Invoke(form, new object[] { "前台测试" });
        timer.Stop();
        Check(dialogs > 0, "Visible failure did not show a dialog");
        type.GetField("exiting", Hidden)!.SetValue(form, true);
        form.Hide(); string previous = tray.BalloonTipText;
        type.GetMethod("NotifyLaunchFailure", Hidden)!.Invoke(form, new object[] { "退出不应提醒" });
        Check(tray.BalloonTipText == previous, "Intentional exit generated a failure notification");
        type.GetField("exiting", Hidden)!.SetValue(form, false);
        type.GetMethod("RestoreWindow", Hidden)!.Invoke(form, null);
        timer.Start();
        var notifyCheck = type.GetMethod("NotifyCheckResult", Hidden)!;
        foreach (int mode in new[] { 0, 1, 2 }) {
            if (mode == 1) form.Hide();
            if (mode == 2) form.WindowState = FormWindowState.Minimized;
            notifyCheck.Invoke(form, new object[] { mode == 1, "实际联网及环境出口校验通过。", mode == 2 ? "测试节点不可用" : null });
            Check(form.Visible && form.WindowState != FormWindowState.Minimized, "Check result did not restore launcher");
        }
        Check(checkDialogs.SequenceEqual(new[] { "检测并修复：连接正常", "检测并修复：连接已恢复", "检测并修复：未能恢复连接" }), "Missing or duplicate check result dialogs");
        type.GetField("exiting", Hidden)!.SetValue(form, true);
        notifyCheck.Invoke(form, new object[] { false, "", "退出期间" });
        Check(checkDialogs.Count == 3, "Exit generated a check result dialog");
        type.GetField("exiting", Hidden)!.SetValue(form, false);
        timer.Stop();
        Console.WriteLine("PASS: healthy/recovered/failed result dialogs, hidden/minimized restoration, one dialog per result and exit suppression");
        Console.WriteLine("PASS: helper failure classification, real visible error dialog, hidden/minimized notification request, click restore, intentional-exit suppression");
    }
    delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
