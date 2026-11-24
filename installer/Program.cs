static class Program
{
    /// <summary>
    /// RegionLauncherSetup.exe                         interactive installer
    /// RegionLauncherSetup.exe /update ROOT /pid PID   update started by the launcher: waits for it to exit, overwrites, relaunches
    /// RegionLauncherSetup.exe /silent /dir ROOT [/nodesktop]   unattended install (exit code 0 on success)
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        HomeTheme.Initialize();
        string? Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Flag("/silent")) {
            string root = Arg("/dir") ?? Installation.DefaultDirectory;
            try {
                var warning = SetupEngine.Install(new SetupEngine.Options(root, !Flag("/nodesktop"), Installation.IsOwnedDirectory(root)),
                    new Progress<(double, string)>(p => Console.WriteLine(p.Item2)), CancellationToken.None);
                if (warning != null) Console.Error.WriteLine(warning);
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (Arg("/update") is { } updateRoot) {
            if (!Installation.IsOwnedDirectory(updateRoot)) {
                MessageDialog.Show(null, $"未在 {updateRoot} 找到已安装的 Region Launcher，更新已取消。", "Region Launcher 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
            Application.Run(new SetupForm(Path.GetFullPath(updateRoot), int.TryParse(Arg("/pid"), out int pid) ? pid : 0));
            return 0;
        }
        Application.Run(new SetupForm());
        return 0;
    }
}
