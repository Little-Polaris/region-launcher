using System.Runtime.InteropServices;
using System.Text;

static class PackageExecutable
{
    const int InsufficientBuffer = 122;

    public static string Resolve(string executable) =>
        Resolve(executable, RegisteredPackages, InstalledPath, File.Exists);

    internal static string Resolve(string executable, Func<string, IEnumerable<string>> packages,
        Func<string, string?> installedPath, Func<string, bool> exists)
    {
        // Store installs may use WindowsApps on a different volume.
        for (string? folder = Path.GetDirectoryName(executable); folder != null; folder = Path.GetDirectoryName(folder))
        {
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "WindowsApps", StringComparison.OrdinalIgnoreCase))
                continue;
            var old = Parse(Path.GetFileName(folder));
            if (old == null) break;

            string relative = Path.GetRelativePath(folder, executable);
            foreach (var candidate in packages(old.Value.Family).Select(fullName => (FullName: fullName, Id: Parse(fullName)))
                .Where(candidate => candidate.Id is { } id &&
                    id.Family.Equals(old.Value.Family, StringComparison.OrdinalIgnoreCase) &&
                    id.Architecture.Equals(old.Value.Architecture, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Id!.Value.Version))
            {
                var root = installedPath(candidate.FullName);
                if (root == null || !Path.IsPathFullyQualified(root)) continue;
                string current = Path.Combine(root, relative);
                if (exists(current)) return current;
            }
            break;
        }
        return executable;
    }

    static (string Family, Version Version, string Architecture)? Parse(string fullName)
    {
        var parts = fullName.Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length != 13 || parts[3].Length != 0 ||
            parts[2].ToLowerInvariant() is not ("x86" or "x64" or "arm" or "arm64" or "neutral") ||
            !Version.TryParse(parts[1], out var version) || version.Revision < 0)
            return null;
        return (parts[0] + "_" + parts[4], version, parts[2]);
    }

    static IEnumerable<string> RegisteredPackages(string family)
    {
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero) != InsufficientBuffer ||
            count == 0 || count > 4096 || length == 0 || length > 1024 * 1024) return [];
        IntPtr names = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0) return [];
            var result = new List<string>((int)count);
            for (int i = 0; i < count; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size)) is string name) result.Add(name);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(names);
        }
    }

    static string? InstalledPath(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != InsufficientBuffer || length == 0 || length > 32768)
            return null;
        var buffer = new StringBuilder((int)length);
        return GetPackagePathByFullName(fullName, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string fullName, ref uint length, [Out] StringBuilder? path);
}
