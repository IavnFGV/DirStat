using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceMonitor;

/// <summary>Live counters of a running scan; written by the scan thread, read for display only.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Bytes;
    public string? CurrentPath;
}

/// <summary>
/// Builds the pruned tree the same way as wsl/treescan.py: the largest files of each
/// directory are kept while walking, then everything below a size threshold is folded
/// into one "small items" node per directory.
/// </summary>
public sealed class TreeAccumulator
{
    public const int KeepFilesPerDirectory = 24;
    public const long MaxNodesHint = 60000;
    public const long MinThreshold = 64 * 1024;
    public const int MaxExtensions = 300;

    public sealed class Dir
    {
        public Dir(string name) { Name = name; }
        public string Name { get; }
        public List<Dir> Dirs { get; } = new();
        public PriorityQueue<(long Size, string Name, string Ext), long> Top { get; } = new();
        public long Small, SmallCount, Files, Size, Count;
    }

    private readonly List<Dir> all = new();
    private readonly Dictionary<string, (long Size, long Count)> extensions = new(StringComparer.Ordinal);

    public TreeAccumulator(string rootName) { Root = AddDir(null, rootName); }

    public Dir Root { get; }
    public long Files { get; private set; }
    public long Bytes { get; private set; }

    public Dir AddDir(Dir? parent, string name)
    {
        var dir = new Dir(name);
        parent?.Dirs.Add(dir);
        all.Add(dir);
        return dir;
    }

    public void AddFile(Dir dir, string name, long size, string ext)
    {
        Files++; Bytes += size; dir.Files++;
        extensions[ext] = extensions.TryGetValue(ext, out var stat) ? (stat.Size + size, stat.Count + 1) : (size, 1);
        if (dir.Top.Count < KeepFilesPerDirectory) { dir.Top.Enqueue((size, name, ext), size); return; }
        if (size > dir.Top.Peek().Size)
        {
            var evicted = dir.Top.Dequeue();
            dir.Small += evicted.Size; dir.SmallCount++;
            dir.Top.Enqueue((size, name, ext), size);
        }
        else { dir.Small += size; dir.SmallCount++; }
    }

    public DiskTreeSnapshot Build(string root, long? usedBytes)
    {
        // Directories were added parents-first, so reverse order is bottom-up.
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var dir = all[i];
            dir.Size = dir.Small + dir.Top.UnorderedItems.Sum(item => item.Element.Size) + dir.Dirs.Sum(d => d.Size);
            dir.Count = dir.Files + dir.Dirs.Sum(d => d.Count);
        }
        var threshold = Math.Max(MinThreshold, Bytes / MaxNodesHint);
        var tree = Emit(Root, threshold);
        var unaccounted = usedBytes is { } used ? used - Bytes : 0;
        if (unaccounted > threshold)
        {
            tree.Children.Add(new DiskTreeNode { Kind = "u", Size = unaccounted });
            tree.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            tree.Size += unaccounted;
        }
        var ranked = extensions.OrderByDescending(e => e.Value.Size).ToList();
        var list = ranked.Take(MaxExtensions).Select(e => new ExtensionStat { Ext = e.Key, Size = e.Value.Size, Count = e.Value.Count }).ToList();
        if (ranked.Count > MaxExtensions)
            list.Add(new ExtensionStat { Ext = FileTypes.OtherTypes, Size = ranked.Skip(MaxExtensions).Sum(e => e.Value.Size), Count = ranked.Skip(MaxExtensions).Sum(e => e.Value.Count) });
        tree.LinkParents();
        return new DiskTreeSnapshot
        {
            SchemaVersion = 1, Root = root, Timestamp = DateTimeOffset.UtcNow, ScannedBytes = Bytes, ScannedFiles = Files,
            UnaccountedBytes = Math.Max(0, unaccounted), Extensions = list, Tree = tree
        };
    }

    private static DiskTreeNode Emit(Dir dir, long threshold)
    {
        var node = new DiskTreeNode { Name = dir.Name, Size = dir.Size, Kind = "d", FileCount = dir.Count };
        long small = dir.Small, smallCount = dir.SmallCount;
        foreach (var child in dir.Dirs)
        {
            if (child.Size >= threshold) node.Children.Add(Emit(child, threshold));
            else { small += child.Size; smallCount += child.Count; }
        }
        foreach (var (item, _) in dir.Top.UnorderedItems)
        {
            if (item.Size >= threshold) node.Children.Add(new DiskTreeNode { Name = item.Name, Size = item.Size, Kind = "f", Extension = item.Ext });
            else { small += item.Size; smallCount++; }
        }
        if (small > 0) node.Children.Add(new DiskTreeNode { Kind = "o", Size = small, FileCount = smallCount });
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        return node;
    }
}

/// <summary>
/// Scans a Windows volume with the normal directory API (no MFT reading). Without
/// elevation, protected folders are skipped and their space shows as "unaccounted";
/// elevated with SeBackupPrivilege, almost everything is readable.
/// </summary>
public static class WindowsTreeScanner
{
    private const int RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;
    public const string ScanArgument = "--scan-tree";

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, FileAttributes Attributes);

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <param name="checkLinks">Directories whose files are checked for hard links (opening each file
    /// is slow, so only where links are common: C:\Windows holds ~12 GiB of WinSxS duplicates).</param>
    public static DiskTreeSnapshot Scan(string root, ScanProgress progress, CancellationToken cancel, Func<string, bool>? checkLinks = null)
    {
        var watch = Stopwatch.StartNew();
        root = Path.GetFullPath(root);
        var cluster = ClusterSize(root);
        var accumulator = new TreeAccumulator(root);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        var stack = new Stack<(string Path, TreeAccumulator.Dir Dir)>();
        stack.Push((root, accumulator.Root));
        var seenLinks = new HashSet<(uint Volume, ulong Index)>();
        long duplicates = 0, cloudFiles = 0, cloudBytes = 0;
        var errors = 0;
        var samples = new List<string>();
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (path, dir) = stack.Pop();
            progress.CurrentPath = path;
            var linkCheck = checkLinks?.Invoke(path) == true;
            try
            {
                var entries = new FileSystemEnumerable<Entry>(path, (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes), options);
                foreach (var entry in entries)
                {
                    var reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (entry.IsDirectory)
                    {
                        // Junctions, symlinks and mounted volumes point elsewhere: do not count twice.
                        if (!reparse) stack.Push((Path.Join(path, entry.Name), accumulator.AddDir(dir, entry.Name)));
                        continue;
                    }
                    var attributes = (int)entry.Attributes;
                    if ((attributes & (RecallOnOpen | RecallOnDataAccess)) != 0 || (entry.Attributes & FileAttributes.Offline) != 0)
                    {
                        cloudFiles++; cloudBytes += entry.Length; // OneDrive placeholder: not on this disk
                        continue;
                    }
                    var size = reparse ? 0 : (entry.Length + cluster - 1) / cluster * cluster;
                    if (linkCheck && size > 0 && LinkIdentity(Path.Join(path, entry.Name)) is { Links: > 1 } link && !seenLinks.Add(link.Id))
                    {
                        duplicates += size;
                        continue;
                    }
                    accumulator.AddFile(dir, entry.Name, size, reparse ? FileTypes.NotFile : FileTypes.Extension(entry.Name));
                    progress.Files = accumulator.Files;
                    progress.Bytes = accumulator.Bytes;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                errors++;
                if (samples.Count < 20) samples.Add($"{path}: {ex.Message}");
            }
        }
        long? used = null;
        try { var drive = new DriveInfo(root); used = drive.TotalSize - drive.TotalFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        // Only a whole-volume scan can be compared with the volume's used space.
        var snapshot = accumulator.Build(root, string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase) ? used : null);
        snapshot.DurationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        snapshot.ErrorCount = errors;
        snapshot.ErrorSamples = samples;
        snapshot.HardLinkDuplicateBytes = duplicates;
        snapshot.CloudFiles = cloudFiles;
        snapshot.CloudBytes = cloudBytes;
        snapshot.Elevated = IsElevated();
        return snapshot;
    }

    /// <summary>Hard links live mostly in the Windows directory (WinSxS ↔ System32).</summary>
    public static Func<string, bool> WindowsDirectoryLinks()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        return path => (path.TrimEnd('\\') + "\\").StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static (uint Links, (uint Volume, ulong Index) Id)? LinkIdentity(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return (info.NumberOfLinks, (info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow));
    }

    private static long ClusterSize(string root)
    {
        try
        {
            if (GetDiskFreeSpaceW(Path.GetPathRoot(root)!, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
                return Math.Max(512, (long)sectorsPerCluster * bytesPerSector);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { }
        return 4096;
    }

    /// <summary>
    /// Administrators hold SeBackupPrivilege but it is disabled by default. Enabled, it lets
    /// directory enumeration (opened with FILE_FLAG_BACKUP_SEMANTICS by .NET) bypass ACLs,
    /// so protected folders such as System Volume Information become countable.
    /// </summary>
    public static bool EnableBackupPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return false;
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid)) return false;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED when the token lacks it
        }
    }

    /// <summary>
    /// Entry point of the elevated helper: DiskSpaceMonitor.exe --scan-tree C: --output file.
    /// Writes progress to file.progress while running and errors to file.error.
    /// </summary>
    public static int RunCommandLine(string[] args)
    {
        var root = ArgumentValue(args, ScanArgument);
        var output = ArgumentValue(args, "--output");
        if (root is null || output is null) return 2;
        if (root.Length == 2 && root[1] == ':') root += "\\";
        var progressPath = output + ".progress";
        var errorPath = output + ".error";
        try { File.Delete(errorPath); } catch (IOException) { }
        var progress = new ScanProgress();
        using var done = new CancellationTokenSource();
        var reporter = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                try { WriteProgress(progressPath, progress); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { await Task.Delay(500, done.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            var backup = EnableBackupPrivilege();
            var snapshot = Scan(root, progress, CancellationToken.None, WindowsDirectoryLinks());
            snapshot.BackupPrivilege = backup;
            snapshot.Save(output);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(errorPath, ex.Message); } catch (IOException) { }
            return 1;
        }
        finally
        {
            done.Cancel();
            reporter.Wait(2000);
            try { File.Delete(progressPath); } catch (IOException) { }
        }
    }

    public static void WriteProgress(string path, ScanProgress progress)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { files = progress.Files, bytes = progress.Bytes, path = progress.CurrentPath }));
        File.Move(temp, path, true);
    }

    public static ScanProgress? ReadProgress(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(WslAgentManager.ReadShared(path));
            var root = document.RootElement;
            return new ScanProgress
            {
                Files = root.GetProperty("files").GetInt64(), Bytes = root.GetProperty("bytes").GetInt64(),
                CurrentPath = root.TryGetProperty("path", out var p) ? p.GetString() : null
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
}
