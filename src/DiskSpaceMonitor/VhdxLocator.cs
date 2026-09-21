using Microsoft.Win32;

namespace DiskSpaceMonitor;

internal static class VhdxLocator
{
    public static double? SizeGiB(string distribution)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (!string.Equals(key?.GetValue("DistributionName") as string, distribution, StringComparison.OrdinalIgnoreCase)) continue;
                var basePath = Environment.ExpandEnvironmentVariables(key?.GetValue("BasePath") as string ?? "");
                var path = Path.Combine(basePath, "ext4.vhdx");
                if (File.Exists(path)) return new FileInfo(path).Length / 1073741824d;
            }
        }
        catch { }
        return null;
    }
}
