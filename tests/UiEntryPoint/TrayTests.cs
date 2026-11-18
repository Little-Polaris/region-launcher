using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

static class TrayTests {
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void PumpUntil(Func<bool> done) {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(20); }
        if (!done()) throw new Exception("Tray lifecycle timed out");
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(Form form, string bin, string data) {
        var type = form.GetType();
        var tray = (NotifyIcon)type.GetField("tray", Hidden)!.GetValue(form)!;
        Check(tray.Icon != null && tray.Icon.Handle != System.Drawing.SystemIcons.Application.Handle && form.Icon != null, "Custom tray/window icon missing");
        Check(tray.Visible && tray.ContextMenuStrip!.Items.Count == 3, "Tray menu missing");
        var job = @"Local\RegionLauncher." + Guid.NewGuid().ToString("N");
        var config = Path.Combine(data, "tray-session.ini"); var report = Path.Combine(data, "tray-processes.txt");
        File.WriteAllText(config, $"[session]\nJob={job}\nApplication={Environment.ProcessPath}\nArguments=--tray-fixture \"{report}\"\nLocale=en-US\nRegion=US\nGeo=244\nTimeZone=Pacific Standard Time\n", Encoding.Unicode);
        using var outside = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--tray-leaf") { UseShellExecute = false, CreateNoWindow = true })!;
        string proxyReport = Path.Combine(data,"tray-proxy.txt");
        using var proxyChild = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--tray-proxy \"{proxyReport}\"") { UseShellExecute=false,CreateNoWindow=true })!;
        using var session = Process.Start(new ProcessStartInfo(Path.Combine(bin, "RegionSession.exe"), $"--locale-test \"{config}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        try {
            PumpUntil(() => File.Exists(report) && new FileInfo(report).Length > 0);
            var ids = File.ReadAllText(report).Split(',').Select(int.Parse).ToArray();
            using var root = Process.GetProcessById(ids[0]); using var child = Process.GetProcessById(ids[1]);
            type.GetField("session", Hidden)!.SetValue(form, session);
            type.GetField("currentJob", Hidden)!.SetValue(form, job);
            var proxy = type.GetField("dedicatedProxy", Hidden)!.GetValue(form)!;
            PumpUntil(()=>File.Exists(proxyReport) && new FileInfo(proxyReport).Length>0);
            var portNumbers = File.ReadAllText(proxyReport).Split(',').Select(int.Parse).ToArray();
            var portsType = type.Assembly.GetType("ProxyPorts")!;
            var ports = Activator.CreateInstance(portsType, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic, null,
                new object[]{portNumbers[0],System.Net.IPAddress.Parse("127.0.0.154"),portNumbers[1]},null)!;
            proxy.GetType().GetField("process",Hidden)!.SetValue(proxy,Process.GetProcessById(proxyChild.Id));
            proxy.GetType().GetField("ports",Hidden)!.SetValue(proxy,ports);
            ((Action)proxy.GetType().GetField("UnexpectedExit", Hidden)!.GetValue(proxy)!)();
            Application.DoEvents(); Thread.Sleep(150);
            Check(!session.HasExited && !root.HasExited && !child.HasExited && !outside.HasExited,
                "Unexpected proxy exit terminated running applications");
            Check(((ToolStripMenuItem)type.GetField("checkProxy", Hidden)!.GetValue(form)!).Text == "检测并修复", "Recovery button missing");
            Console.WriteLine("PASS: unexpected proxy exit preserves existing root/child job and outside process");
            type.GetField("preparing", Hidden)!.SetValue(form, true);
            form.Close(); Application.DoEvents();
            Check(!form.Visible && !form.IsDisposed && tray.Visible && !session.HasExited && !root.HasExited && !child.HasExited, "Closing window stopped background work");
            tray.ContextMenuStrip!.Items[0].PerformClick(); Application.DoEvents();
            Check(form.Visible && form.ShowInTaskbar, "Tray restore failed");
            // Exit must wait for a pending preparation to observe cancellation.
            var shutdown = (CancellationTokenSource)type.GetField("shutdown", Hidden)!.GetValue(form)!;
            var pending = Task.Delay(Timeout.Infinite, shutdown.Token);
            ((HashSet<Task>)type.GetField("operations", Hidden)!.GetValue(form)!).Add(pending);
            tray.ContextMenuStrip.Items[2].PerformClick();
            PumpUntil(() => form.IsDisposed);
            Check(pending.IsCanceled && session.HasExited && root.HasExited && child.HasExited, "Tray exit left a process group alive");
            Check(!outside.HasExited && !tray.Visible, "Tray exit affected outside process or left tray icon");
            Check(proxyChild.HasExited,"Tray exit left its proxy alive");
            Check(portsType.GetMethod("Check",Hidden)!.Invoke(ports,null)==null,"Tray exit left TCP/UDP proxy or DNS ports occupied");
            string exitLogs=string.Join("",Directory.GetFiles(Path.Combine(data,"logs"),"*.log").Select(File.ReadAllText));
            Check(exitLogs.Contains("[代理退出]") && exitLogs.Contains("[端口释放] 已确认") && exitLogs.Contains("退出清理完成"),"Tray cleanup evidence missing");
            Console.WriteLine("PASS: close-to-tray while preparing, menu restore, tray exit cancels pending work and stops real root/child job; outside process survives");
        } finally {
            try { using var stop = EventWaitHandle.OpenExisting(job + ".stop"); stop.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            if (!session.HasExited) { if (!session.WaitForExit(5000)) session.Kill(true); }
            if (!outside.HasExited) { outside.Kill(); outside.WaitForExit(); }
            if (!proxyChild.HasExited) { proxyChild.Kill(); proxyChild.WaitForExit(); }
        }
    }
}
