using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

sealed record LaunchApplication
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Application { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public LaunchEnvironment? Environment { get; init; }
    public override string ToString() => Name;
}

sealed record ApplicationList
{
    public int Version { get; init; } = 1;
    public string? SelectedId { get; init; }
    public List<LaunchApplication> Applications { get; init; } = [];
}

sealed class ApplicationLibrary(string path)
{
    static readonly JsonSerializerOptions json = new() {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public ApplicationList Load(string legacyApplication, string legacyArguments, LaunchEnvironment? legacyEnvironment = null)
    {
        legacyEnvironment ??= new();
        if (File.Exists(path)) {
            var value = JsonSerializer.Deserialize<ApplicationList>(File.ReadAllText(path), json)
                ?? throw new InvalidDataException("软件列表不能为空 JSON。");
            Validate(value);
            if (value.Applications.Any(a => a.Environment == null)) {
                value = value with { Applications = value.Applications.Select(a => a with { Environment = a.Environment ?? legacyEnvironment }).ToList() };
                Save(value);
            }
            return value;
        }
        var initial = new ApplicationList();
        if (!string.IsNullOrWhiteSpace(legacyApplication)) {
            var entry = new LaunchApplication { Name = System.IO.Path.GetFileNameWithoutExtension(legacyApplication), Application = legacyApplication, Arguments = legacyArguments, Environment = legacyEnvironment };
            initial = initial with { SelectedId = entry.Id, Applications = [entry] };
        }
        Save(initial); return initial;
    }
    public void Save(ApplicationList value)
    {
        Validate(value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, json));
            File.Move(temporary, path, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static string NormalizePath(string application) => System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(application), AppContext.BaseDirectory);
    public static string ResolvePath(string application) => PackageExecutable.Resolve(NormalizePath(application));
    internal const string TargetFilter = "程序与脚本 (*.exe;*.cmd;*.bat;*.ps1;*.vbs;*.sh)|*.exe;*.cmd;*.bat;*.ps1;*.vbs;*.sh";

    public static bool SupportsTarget(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".ps1" or ".vbs" or ".sh";

    internal static bool IsShortcut(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    internal static LaunchApplication ImportTarget(string path, LaunchEnvironment environment)
    {
        var entry = new LaunchApplication { Name = Path.GetFileNameWithoutExtension(path), Application = path, Environment = environment };
        if (!IsShortcut(path)) return entry;
        if (!File.Exists(path)) throw new FileNotFoundException("找不到此快捷方式。");

        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows 快捷方式组件不可用。");
        dynamic shell = Activator.CreateInstance(type)!;
        try {
            dynamic link = shell.CreateShortcut(path);
            try {
                string target = Environment.ExpandEnvironmentVariables((string)link.TargetPath);
                if (!Path.IsPathFullyQualified(target) || !SupportsTarget(target))
                    throw new InvalidDataException("此快捷方式没有指向可直接启动的程序或脚本。");
                if (!File.Exists(ResolvePath(target))) throw new FileNotFoundException("快捷方式指向的程序或脚本不存在。");
                string directory = (string)link.WorkingDirectory;
                entry = entry with { Application = target, Arguments = (string)link.Arguments,
                    WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? null : Environment.ExpandEnvironmentVariables(directory) };
                ValidateEntry(entry);
                return entry;
            } finally { Marshal.FinalReleaseComObject(link); }
        } finally { Marshal.FinalReleaseComObject(shell); }
    }

    public static bool UsesInteractiveConsole(string target, string arguments)
    {
        string name = Path.GetFileName(target).ToLowerInvariant();
        if (name is not ("pwsh.exe" or "powershell.exe" or "cmd.exe")) return false;
        foreach (string argument in LinkBrowser.Split(arguments))
        {
            string flag = argument.ToLowerInvariant();
            if (name == "cmd.exe")
            {
                if (flag.StartsWith("/k", StringComparison.Ordinal)) return true;
                if (flag.StartsWith("/c", StringComparison.Ordinal)) return false;
            }
            else
            {
                if (flag is "-noexit" or "-noe") return true;
                if (flag is not ("-nologo" or "-nol" or "-noprofile" or "-nop" or "-login" or "-l" or "-interactive" or "-i")) return false;
            }
        }
        return true;
    }

    public static (string Executable, string Arguments, string WorkingDirectory) PrepareTarget(string path, string arguments, string? workingDirectory = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("程序或脚本不存在，请重新选择路径。");
        if (path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 || arguments.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("启动配置包含无法传递的字符。");
        string folder = string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(path)! : workingDirectory;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("程序的工作目录不存在。");
        string system = Environment.SystemDirectory;
        string tail = arguments.Length == 0 ? "" : " " + arguments;
        var (host, command) = Path.GetExtension(path).ToLowerInvariant() switch {
            ".exe" => (path, arguments),
            // cmd expands percent variables even inside quotes.
            ".cmd" or ".bat" when path.Contains('%') => throw new IOException("批处理脚本的完整路径不能包含 %。"),
            ".cmd" or ".bat" when path.StartsWith(@"\\", StringComparison.Ordinal) => throw new IOException("批处理脚本请使用本地路径或映射盘符。"),
            ".cmd" or ".bat" => (Path.Combine(system, "cmd.exe"), $"/d /v:off /s /c \"\"{path}\"{tail}\""),
            ".ps1" => (Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"), $"-NoLogo -NoProfile -NonInteractive -File \"{path}\"{tail}"),
            ".vbs" => (Path.Combine(system, "cscript.exe"), $"//NoLogo //B \"{path}\"{tail}"),
            ".sh" => (FindGitBash(), $"--noprofile --norc -- \"{path.Replace('\\', '/')}\"{tail}"),
            _ => throw new InvalidDataException("请选择支持的程序或脚本。")
        };
        if (!File.Exists(host)) throw new FileNotFoundException("找不到该脚本所需的解释器。");
        return (host, command, folder);
    }

    static string FindGitBash()
    {
        foreach (string hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            if (Microsoft.Win32.Registry.GetValue(hive + @"\SOFTWARE\GitForWindows", "InstallPath", null) is string install &&
                Path.IsPathFullyQualified(install))
            {
                string bash = Path.Combine(install, "bin", "bash.exe");
                if (File.Exists(bash)) return bash;
            }
        }
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") };
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string bash = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(bash)) return bash;
        }
        throw new FileNotFoundException("运行 .sh 需要安装 Git for Windows（Git Bash）。");
    }

    public static void ValidateEntry(LaunchApplication entry)
    {
        entry.Environment?.Validate();
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Application) || entry.Arguments == null)
            throw new InvalidDataException("软件需要名称、程序路径和有效的启动参数字段。");
        foreach (string text in new[] { entry.Id, entry.Name, entry.Application, entry.Arguments })
            if (text.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("软件配置不能包含换行符或空字符。");
        if (!string.IsNullOrEmpty(entry.WorkingDirectory) &&
            (!Path.IsPathFullyQualified(entry.WorkingDirectory) || entry.WorkingDirectory.IndexOfAny(['"', '\r', '\n', '\0']) >= 0))
            throw new InvalidDataException("程序的工作目录必须是有效的绝对路径。");
        if (!SupportsTarget(NormalizePath(entry.Application)))
            throw new InvalidDataException("请选择 .exe、.cmd、.bat、.ps1、.vbs 或 .sh 文件。");
    }
    static void Validate(ApplicationList value)
    {
        if (value.Version != 1 || value.Applications == null) throw new InvalidDataException("不支持的软件列表格式或版本。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in value.Applications) {
            if (entry == null) throw new InvalidDataException("软件条目不能为空。");
            ValidateEntry(entry);
            if (!ids.Add(entry.Id)) throw new InvalidDataException("软件条目 ID 不能重复。");
        }
        if (value.SelectedId != null && !ids.Contains(value.SelectedId)) throw new InvalidDataException("所选软件 ID 不在列表中。");
    }
}
