using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Program
{
    /// <summary>
    /// RegionLauncherSetup.exe                         interactive installer
    /// RegionLauncherSetup.exe /update ROOT /pid PID   update started by the launcher: waits for it to exit, overwrites, relaunches
    /// RegionLauncherSetup.exe /silent /dir ROOT [/nodesktop] [/force]   unattended install; asks a running copy to exit, /force ends it
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        HomeTheme.Initialize();
        string? Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (InsideLauncherJob()) {
            const string message = "当前安装程序运行在 Region Launcher 环境内。退出启动器会同时关闭安装程序。请在 Windows 文件资源管理器中找到下载的安装文件，并从那里重新打开。";
            if (Flag("/silent")) Console.Error.WriteLine(message);
            else MessageDialog.Show(null, message, "Region Launcher 安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 4;
        }

        if (Flag("/silent")) {
            string root = Arg("/dir") ?? Installation.DefaultDirectory;
            try {
                if (!Installation.CloseRunningSilently([root], Flag("/force"))) { Console.Error.WriteLine("Region Launcher 仍在运行，请先退出，或使用 /force。"); return 3; }
                bool upgrade = Installation.IsOwnedDirectory(root) || Installation.IsPortableDirectory(root);
                var warning = SetupEngine.Install(new SetupEngine.Options(root, !Flag("/nodesktop"), upgrade),
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

    static bool InsideLauncherJob()
    {
        const uint JobObjectQuery = 4;
        const string prefix = @"Local\RegionLauncher.";
        string? jobName = Environment.GetEnvironmentVariable("REGION_LAUNCHER_JOB");
        if (jobName == null || !jobName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(jobName.AsSpan(prefix.Length), "N", out _)) return false;
        nint current = GetCurrentProcess();
        if (!IsProcessInJob(current, 0, out bool inAnyJob) || !inAnyJob) return false;
        using var job = OpenJobObjectW(JobObjectQuery, false, jobName);
        // A restricted child can inherit the launcher job without permission to open it by name.
        if (job.IsInvalid) return Marshal.GetLastPInvokeError() == 5; // ERROR_ACCESS_DENIED
        return IsProcessInJob(current, job.DangerousGetHandle(), out bool member) && member;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle OpenJobObjectW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll")]
    static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool IsProcessInJob(nint process, nint job, out bool member);
}
