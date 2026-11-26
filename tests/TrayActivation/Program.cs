using System.Diagnostics;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 1)
        {
            using var window = new Form { Text = "Region Launcher tray fixture" };
            using var icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Identical tray caption", Visible = true };
            icon.Click += (_, _) => { window.Show(); File.WriteAllText(args[0] + ".shown", ""); };
            File.WriteAllText(args[0], "ready");
            Application.Run();
            return 0;
        }

        string directory = Path.Combine(Path.GetTempPath(), "RegionLauncher-Tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixtures = new List<Process>();
        try
        {
            string FileFor(int index) => Path.Combine(directory, index.ToString());
            for (int i = 0; i < 2; i++)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(FileFor(i));
                fixtures.Add(Process.Start(start)!);
                if (!SpinWait.SpinUntil(() => File.Exists(FileFor(i)), 5000)) throw new Exception("Tray fixture did not start");
            }
            Thread.Sleep(500);
            var initialOverflow = TrayActivation.Overflow();
            using var current = Process.GetCurrentProcess();
            if (TrayActivation.Open(current) != TrayActivation.Result.Missing || TrayActivation.Overflow() != initialOverflow)
                throw new Exception("Missing icon changed the tray state");
            Console.WriteLine("PASS: missing icon reported without changing tray visibility");
            var result = Task.Run(() => TrayActivation.Open(fixtures[1])).GetAwaiter().GetResult();
            if (result == TrayActivation.Result.Missing && initialOverflow == 0)
            {
                if (TrayActivation.Overflow() != 0 || fixtures.Select((_, i) => FileFor(i) + ".shown").Any(File.Exists))
                    throw new Exception("An inaccessible icon opened the tray or a window");
                Console.WriteLine("PASS: hidden same-caption icons left alone");
                return 0;
            }
            if (!SpinWait.SpinUntil(() => File.Exists(FileFor(1) + ".shown"), 4000) ||
                File.Exists(FileFor(0) + ".shown"))
                throw new Exception($"Expected only selected instance to open: {result}");
            Console.WriteLine("PASS: invoked the selected tray-only process, same-caption sibling untouched");
            return 0;
        }
        finally
        {
            foreach (var p in fixtures) { if (!p.HasExited) p.Kill(); p.WaitForExit(); p.Dispose(); }
            Directory.Delete(directory, true);
        }
    }
}
