#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

static class SingleInstanceTests
{
    public static void Fixture(string[] args) {
        Exception? failure = null;
        var ui = new Thread(() => {
            try {
                Application.EnableVisualStyles();
                var assembly = Assembly.LoadFrom(args[1]);
                var launcher = assembly.GetType("Launcher")!;
                var state = Path.Combine(args[3], "state");
                using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                Func<Form> create = () => {
                    Thread.Sleep(700); // A second launch can arrive before the window exists.
                    File.AppendAllText(Path.Combine(args[3], "created"), Environment.ProcessId + "\n");
                    var form = (Form)Activator.CreateInstance(launcher, BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new object[] { Path.Combine(args[3], "data") }, null)!;
                    form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                    form.Shown += (_, _) => { form.Hide(); File.WriteAllText(state, "hidden"); };
                    timer.Tick += (_, _) => {
                        string command = Path.Combine(args[3], "command");
                        if (!File.Exists(command)) return;
                        string action = File.ReadAllText(command); File.Delete(command);
                        if (action == "hide") form.Hide();
                        if (action == "minimize") form.WindowState = FormWindowState.Minimized;
                        if (action == "exit") {
                            launcher.GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                            form.Close();
                        }
                        File.WriteAllText(state, action);
                    };
                    timer.Start(); return form;
                };
                Action<Form> restore = form => {
                    launcher.GetMethod("RestoreWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                    if (!form.Visible || form.WindowState == FormWindowState.Minimized) throw new Exception("Window was not restored");
                    File.AppendAllText(Path.Combine(args[3], "activated"), Environment.ProcessId + "\n");
                };
                assembly.GetType("SingleInstance")!.GetMethod("Run")!.Invoke(null, new object[] { create, restore, args[2] });
            } catch (Exception ex) { failure = ex; }
        });
        ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
        if (failure != null) throw failure;
    }

    public static void Run(string assemblyPath, string directory) {
        Directory.CreateDirectory(directory);
        string name = "RegionLauncher.Test." + Guid.NewGuid().ToString("N");
        var processes = new List<Process>();
        Process Start() {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--single-fixture", Path.GetFullPath(assemblyPath), name, Path.GetFullPath(directory) }) info.ArgumentList.Add(arg);
            var process = Process.Start(info)!; processes.Add(process); return process;
        }
        void Wait(Func<bool> condition, string message) {
            var deadline = Stopwatch.StartNew();
            while (!condition()) { if (deadline.ElapsedMilliseconds > 15000) throw new Exception(message); Thread.Sleep(30); }
        }
        int Lines(string file) => File.Exists(Path.Combine(directory, file)) ? File.ReadAllLines(Path.Combine(directory, file)).Length : 0;
        void Duplicate(int count) {
            using var duplicate = Start();
            if (!duplicate.WaitForExit(15000) || duplicate.ExitCode != 0) throw new Exception("Duplicate did not exit successfully");
            Wait(() => Lines("activated") >= count, "Existing window was not activated");
            if (Lines("created") != 1) throw new Exception("Duplicate created another launcher");
        }
        void Command(string action) {
            File.WriteAllText(Path.Combine(directory, "command"), action);
            Wait(() => File.ReadAllText(Path.Combine(directory, "state")) == action, "Window command failed: " + action);
        }
        try {
            var first = Start(); Duplicate(1);
            Command("hide"); Duplicate(2);
            Command("minimize"); Duplicate(3);
            Command("exit"); Wait(() => first.HasExited, "Primary did not exit");
            var reopened = Start(); Wait(() => Lines("created") == 2, "Cannot reopen after normal exit");
            reopened.Kill(); reopened.WaitForExit();
            var recovered = Start(); Wait(() => Lines("created") == 3, "Cannot reopen after a crash");
            Wait(() => File.ReadAllText(Path.Combine(directory, "state")) == "hidden", "Recovered window not ready");
            Command("exit"); Wait(() => recovered.HasExited, "Recovered primary did not exit");
            Console.WriteLine("PASS: single launcher during startup, hidden/minimized activation, duplicate exits, clean restart and crash recovery");
        } finally {
            foreach (var process in processes) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } } catch (InvalidOperationException) { } process.Dispose(); }
        }
    }
}
