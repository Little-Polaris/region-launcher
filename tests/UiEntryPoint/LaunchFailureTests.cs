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
    public static void Run(Form form) {
        var type = form.GetType();
        var tray = (NotifyIcon)type.GetField("tray", Hidden)!.GetValue(form)!;
        int dialogs = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) => {
            var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "启动失败") { dialogs++; PostMessage(window, 0x0010, 0, 0); }
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
            var task = (Task)type.GetMethod("MonitorLaunch", Hidden)!.Invoke(form, new object[] { process })!;
            Pump(task);
            Check(started ? task.IsCompletedSuccessfully : task.Exception?.InnerException is IOException, "Wrong launch result classification");
        }
        form.Hide();
        var failure = (Task)type.GetMethod("StartSession", Hidden)!.Invoke(form, null)!;
        Pump(failure); failure.GetAwaiter().GetResult();
        Check(dialogs == 0, "Hidden failure opened a modal dialog");
        Check(!form.Visible && tray.BalloonTipTitle.Contains("启动失败") && tray.BalloonTipText.Contains("所选程序") && tray.BalloonTipIcon == ToolTipIcon.Error,
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
        Console.WriteLine("PASS: helper failure classification, real visible error dialog, hidden/minimized notification request, click restore, intentional-exit suppression");
    }
    delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
