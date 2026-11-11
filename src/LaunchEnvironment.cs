sealed record LaunchEnvironment
{
    public int GeoId { get; init; } = 244;
    public string Locale { get; init; } = "en-US";
    public string TimeZone { get; init; } = "Pacific Standard Time";
    public bool AutoDst { get; init; } = true;
    static readonly Lazy<HashSet<int>> regions = new(() => WindowsSettings.Regions().Select(r => r.GeoId).ToHashSet());
    static readonly Lazy<HashSet<string>> languages = new(() => WindowsSettings.Languages().Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    static readonly Lazy<HashSet<string>> zones = new(() => WindowsSettings.TimeZones().Select(z => z.Id).ToHashSet(StringComparer.Ordinal));
    public void Validate()
    {
        if (!regions.Value.Contains(GeoId) || Locale == null || !languages.Value.Contains(Locale) ||
            TimeZone == null || !zones.Value.Contains(TimeZone))
            throw new InvalidDataException("软件的国家或地区、语言或时区无效，请使用本机 Windows 支持的选项。");
    }
}
