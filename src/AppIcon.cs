using System.Reflection;

static class AppIcon
{
    public static Icon Load(Size size) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RegionLauncher.AppIcon.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var icon = new Icon(stream, size);
        return (Icon)icon.Clone();
    }
}
