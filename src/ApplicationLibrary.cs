using System.Text.Encodings.Web;
using System.Text.Json;

sealed record LaunchApplication
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Application { get; init; } = "";
    public string Arguments { get; init; } = "";
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
    public static string ResolvePath(string application) => System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(application), AppContext.BaseDirectory);
    public static void ValidateEntry(LaunchApplication entry)
    {
        entry.Environment?.Validate();
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Application) || entry.Arguments == null)
            throw new InvalidDataException("软件需要名称、程序路径和有效的启动参数字段。");
        foreach (string text in new[] { entry.Id, entry.Name, entry.Application, entry.Arguments })
            if (text.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidDataException("软件配置不能包含换行符或空字符。");
        if (!System.IO.Path.GetExtension(ResolvePath(entry.Application)).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 Windows .exe 程序。");
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
