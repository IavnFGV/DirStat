using Microsoft.Win32;

namespace DiskSpaceMonitor;

internal sealed record VhdxInfo(string Path, double SizeGiB);

internal static class VhdxLocator
{
    public static VhdxInfo? Get(string distribution)
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
                var fileName = key?.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                if (basePath.Length == 0 || fileName != Path.GetFileName(fileName)) return null;
                var path = Path.Combine(basePath, fileName);
                if (File.Exists(path)) return new VhdxInfo(path, new FileInfo(path).Length / 1073741824d);
            }
        }
        catch { }
        return null;
    }
}
