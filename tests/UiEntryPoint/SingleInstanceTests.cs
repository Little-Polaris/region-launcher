#nullable enable
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

static class SingleInstanceTests
{
    static string ReadMarker(string path) {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    static void WriteMarker(string path, string text) {
        try {
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, true);
        } catch (IOException ex) { throw new IOException("Writing test marker: " + path, ex); }
    }
    public static void Fixture(string[] args) {
        Exception? failure = null;
        var ui = new Thread(() => {
            try {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                var assembly = Assembly.LoadFrom(args[1]);
                var launcher = assembly.GetType("Launcher")!;
                var state = Path.Combine(args[3], "state");
                using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                Func<Form> create = () => {
                    WriteMarker(Path.Combine(args[3], "owner"), Environment.ProcessId.ToString());
                    Thread.Sleep(700); // A second launch can arrive before the window exists.
                    string created = Path.Combine(args[3], "created");
                    WriteMarker(created, ReadMarker(created) + Environment.ProcessId + "\n");
                    var form = (Form)Activator.CreateInstance(launcher, BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new object[] { Path.Combine(args[3], "data") }, null)!;
                    form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                    form.Shown += (_, _) => { form.Hide(); WriteMarker(state, "hidden"); };
                    timer.Tick += (_, _) => {
                        string command = Path.Combine(args[3], "command");
                        if (!File.Exists(command)) return;
                        string action = ReadMarker(command); File.Delete(command);
                        if (action == "hide") form.Hide();
                        if (action == "minimize") form.WindowState = FormWindowState.Minimized;
                        if (action == "exit") {
                            launcher.GetField("allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                            form.Close();
                        }
                        WriteMarker(state, action);
                    };
                    timer.Start(); return form;
                };
                Action<Form> restore = form => {
                    launcher.GetMethod("RestoreWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                    if (!form.Visible || form.WindowState == FormWindowState.Minimized) throw new Exception("Window was not restored");
                    string activated = Path.Combine(args[3], "activated");
                    WriteMarker(activated, ReadMarker(activated) + Environment.ProcessId + "\n");
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
        var outputs = new Dictionary<int, Task<string[]>>();
        Process Start() {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--single-fixture", Path.GetFullPath(assemblyPath), name, Path.GetFullPath(directory) }) info.ArgumentList.Add(arg);
            var process = Process.Start(info)!; processes.Add(process);
            outputs[process.Id] = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            return process;
        }
        void Wait(Func<bool> condition, string message) {
            var deadline = Stopwatch.StartNew();
            while (true) {
                try { if (condition()) return; }
                catch (IOException) { } // The fixture can be writing its state at this instant.
                if (deadline.ElapsedMilliseconds > 15000) throw new Exception(message);
                Thread.Sleep(30);
            }
        }
        int Lines(string file) => ReadMarker(Path.Combine(directory, file)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        void Duplicate(int count) {
            var duplicate = Start();
            if (!duplicate.WaitForExit(15000) || duplicate.ExitCode != 0) throw new Exception("Duplicate did not exit successfully");
            Wait(() => Lines("activated") >= count, "Existing window was not activated");
            if (Lines("created") != 1) throw new Exception("Duplicate created another launcher");
        }
        void Command(string action) {
            File.WriteAllText(Path.Combine(directory, "command.tmp"), action);
            File.Move(Path.Combine(directory, "command.tmp"), Path.Combine(directory, "command"));
            Wait(() => ReadMarker(Path.Combine(directory, "state")) == action, "Window command failed: " + action);
        }
        try {
            var first = Start();
            Wait(() => ReadMarker(Path.Combine(directory, "owner")) == first.Id.ToString(), "Primary did not acquire ownership");
            Duplicate(1);
            Command("hide"); Duplicate(2);
            Command("minimize"); Duplicate(3);
            Command("exit"); Wait(() => first.HasExited, "Primary did not exit");
            var reopened = Start(); Wait(() => Lines("created") == 2, "Cannot reopen after normal exit");
            reopened.Kill(); reopened.WaitForExit();
            var recovered = Start(); Wait(() => Lines("created") == 3, "Cannot reopen after a crash");
            Wait(() => ReadMarker(Path.Combine(directory, "state")) == "hidden", "Recovered window not ready");
            Command("exit"); Wait(() => recovered.HasExited, "Recovered primary did not exit");
            Console.WriteLine("PASS: single launcher during startup, hidden/minimized activation, duplicate exits, clean restart and crash recovery");
        } finally {
            foreach (var process in processes) {
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } } catch (InvalidOperationException) { }
                File.WriteAllText(Path.Combine(directory, $"process-{process.Id}.log"), string.Join("\n", outputs[process.Id].GetAwaiter().GetResult()));
                process.Dispose();
            }
        }
    }
}
