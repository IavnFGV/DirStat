using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DiskSpaceMonitor;

internal sealed record VhdxInfo(string Path, double SizeGiB, double? OnDiskGiB, double? HostFreeGiB);

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
                if (!File.Exists(path)) return null;
                double? hostFree = null;
                try { hostFree = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace / 1073741824d; } catch { }
                return new VhdxInfo(path, new FileInfo(path).Length / 1073741824d, AllocatedGiB(path), hostFree);
            }
        }
        catch { }
        return null;
    }

    private static double? AllocatedGiB(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return null;
        return (((ulong)high << 32) | low) / 1073741824d;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}
