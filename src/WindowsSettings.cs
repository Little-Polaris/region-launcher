using System.Runtime.InteropServices;
using System.Text;

sealed record RegionOption(int GeoId, string Code, string Label)
{
    public override string ToString() => $"{Label} ({Code})";
}
sealed record LanguageOption(string Name, string Label)
{
    public override string ToString() => $"{Label} ({Name})";
}
static class WindowsSettings
{
    delegate bool GeoCallback(int id);
    delegate bool LocaleCallback([MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, nint param);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EnumSystemGeoID(int geoClass, int parent, GeoCallback callback);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetGeoInfoW(int id, int type, StringBuilder result, int size, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EnumSystemLocalesEx(LocaleCallback callback, uint flags, nint param, nint reserved);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetLocaleInfoEx(string locale, uint type, StringBuilder result, int size);
    public static List<RegionOption> Regions()
    {
        var result = new List<RegionOption>();
        if (!EnumSystemGeoID(16, 0, id =>
        {
            var code = new StringBuilder(16); var label = new StringBuilder(256);
            if (GetGeoInfoW(id, 4, code, code.Capacity, 0) > 1 && code.Length == 2 && code.ToString() != "XX" &&
                GetGeoInfoW(id, 8, label, label.Capacity, 0) > 1) result.Add(new(id, code.ToString(), label.ToString()));
            return true;
        })) throw new System.ComponentModel.Win32Exception();
        return result.OrderBy(r => r.Label).ToList();
    }
    public static List<LanguageOption> Languages()
    {
        var result = new List<LanguageOption>();
        if (!EnumSystemLocalesEx((name, flags, param) =>
        {
            var label = new StringBuilder(256);
            if (GetLocaleInfoEx(name, 2, label, label.Capacity) > 1) result.Add(new(name, label.ToString()));
            return true;
        }, 0x20, 0, 0)) throw new System.ComponentModel.Win32Exception();
        return result.OrderBy(l => l.Label).ToList();
    }
    public static List<TimeZoneInfo> TimeZones() => TimeZoneInfo.GetSystemTimeZones().ToList();
}
