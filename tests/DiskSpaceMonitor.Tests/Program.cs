using DiskSpaceMonitor;
using System.Drawing;
using System.Text;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS " + name);
    count++;
}

var config = new MonitorConfig();
Check(Metrics.Level(31, config) == DangerLevel.Normal, "normal level");
Check(Metrics.Level(29, config) == DangerLevel.Warning, "warning level");
Check(Metrics.Level(14, config) == DangerLevel.Critical, "critical level");
Check(Metrics.Level(7, config) == DangerLevel.Emergency, "emergency level");
Check(Metrics.EffectiveWslFree(929, 355) == 355, "WSL limited by host free space");
Check(Metrics.EffectiveWslFree(10, 355) == 10, "WSL limited by ext4 free space");
Check(Metrics.EffectiveWslFree(929, null) == 929, "WSL without VHDX location");
var now = DateTimeOffset.UtcNow;
var trend = Metrics.CalculateTrend(new[] { new UsageSample(now.AddMinutes(-5), 10), new UsageSample(now, 15) }, 10, now);
Check(Math.Abs(trend.DeltaGiB - 5) < 0.0001, "delta");
Check(Math.Abs(trend.GrowthGiBPerMinute - 1) < 0.0001, "growth rate");
Check(trend.Eta == TimeSpan.FromMinutes(10), "ETA");
Check(Metrics.CalculateTrend(new[] { new UsageSample(now.AddMinutes(-5), 15), new UsageSample(now, 10) }, 10, now).Eta is null, "no ETA on shrink");
var json = """{"schemaVersion":1,"timestamp":"2026-09-21T12:00:00Z","hostname":"ubuntu","totalGiB":100,"usedGiB":60,"availableGiB":35,"freeGiB":40,"reservedGiB":5,"usedPercent":60,"totalWriteBytesPerSecond":2048,"topWriter":{"pid":123,"command":"python3","writeBytesPerSecond":2048}}""";
var snapshot = WslSnapshot.Parse(json);
Check(snapshot.Hostname == "ubuntu" && snapshot.TopWriter?.Pid == 123 && snapshot.ReservedGiB == 5 && snapshot.TotalWriteBytesPerSecond == 2048, "JSON parse");
try { WslSnapshot.Parse(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")); throw new Exception("schema validation failed"); }
catch (InvalidDataException) { Check(true, "schema validation"); }
var wslNames = WslDiscovery.ParseList(Encoding.Unicode.GetBytes("Ubuntu\r\ndocker-desktop\r\n"));
Check(wslNames.SequenceEqual(new[] { "Ubuntu", "docker-desktop" }), "WSL UTF-16 list");
Check(WslDiscovery.Choose(new[] { "docker-desktop", "Ubuntu" }) == "Ubuntu", "WSL automatic selection");
Check(WslDiscovery.NormalizeWindowsPath(@"D:\projects\DirStat\wsl\agent.py") == "D:/projects/DirStat/wsl/agent.py", "WSL path argument");
// Treemap layout: areas proportional, inside bounds, no overlaps (classic 6x4 example).
var sizes = new double[] { 6, 6, 4, 3, 2, 2, 1 };
var bounds = new RectangleF(0, 0, 6, 4);
var rects = TreemapLayout.Squarify(sizes, bounds);
Check(rects.Select((r, i) => Math.Abs(r.Width * r.Height - sizes[i])).All(d => d < 0.001), "treemap areas proportional");
Check(rects.All(r => r.Left >= -0.001 && r.Top >= -0.001 && r.Right <= 6.001 && r.Bottom <= 4.001), "treemap inside bounds");
var overlaps = false;
for (var i = 0; i < rects.Length; i++)
    for (var j = i + 1; j < rects.Length; j++)
    {
        var a = rects[i]; var b = rects[j];
        if (Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 0.001 && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 0.001) overlaps = true;
    }
Check(!overlaps, "treemap rectangles do not overlap");
Check(rects.Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width)) < 3, "treemap aspect ratios stay squarish");
var unordered = TreemapLayout.Squarify(new double[] { 1, 0, 3 }, new RectangleF(0, 0, 4, 1));
Check(Math.Abs(unordered[0].Width * unordered[0].Height - 1) < 0.001 && unordered[1].IsEmpty && Math.Abs(unordered[2].Width - 3) < 0.001, "treemap keeps input order and skips zero sizes");

// Disk tree: parents linked, paths built, deep nesting accepted.
var deep = new StringBuilder();
for (var i = 0; i < 300; i++) deep.Append($"{{\"n\":\"d{i}\",\"s\":10,\"k\":\"d\",\"c\":[");
deep.Append("{\"n\":\"leaf.bin\",\"s\":10,\"k\":\"f\",\"e\":\".bin\"}");
for (var i = 0; i < 300; i++) deep.Append("]}");
var treeJson = $$$"""{"schemaVersion":1,"requestId":"r1","root":"/","timestamp":"2026-11-25T12:00:00Z","scannedBytes":10,"scannedFiles":1,"extensions":[{"ext":".bin","size":10,"count":1}],"tree":{"n":"/","s":10,"k":"d","c":[{{{deep}}}]}}""";
var tree = DiskTreeSnapshot.Parse(treeJson);
var leaf = tree.Tree!.DominantLeaf();
Check(leaf.Name == "leaf.bin" && leaf.Extension == ".bin", "tree dominant leaf");
Check(leaf.FullPath.StartsWith("/d0/d1/d2/") && leaf.FullPath.EndsWith("/d299/leaf.bin"), "tree full path");
Check(tree.Tree.FullPath == "/" && leaf.PathFromRoot().Count() == 302, "tree root path and depth");
var small = DiskTreeSnapshot.Parse("""{"schemaVersion":1,"tree":{"n":"/","s":5,"k":"d","c":[{"n":"","s":5,"k":"o","fc":7}]}}""");
Check(small.Tree!.Children[0].DisplayName.Contains('7') && small.Tree.Children[0].FullPath.StartsWith("/<мелкие"), "folded node naming");
try { DiskTreeSnapshot.Parse("""{"schemaVersion":2,"tree":{"n":"/"}}"""); throw new Exception("tree schema validation failed"); }
catch (InvalidDataException) { Check(true, "tree schema validation"); }
Check(Units.Bytes(1536) == "2 KiB" && Units.Bytes(3L << 30) == $"{3.0:F2} GiB", "byte formatting");
// File types: same rules as wsl/treescan.py.
Check(FileTypes.Extension("movie.MP4") == ".mp4" && FileTypes.Extension("libssl.so.3") == ".so" && FileTypes.Extension("libfoo.so.1.2.3") == ".so", "file type versions and case");
Check(FileTypes.Extension("README") == FileTypes.NoExtension && FileTypes.Extension(".bashrc") == FileTypes.NoExtension && FileTypes.Extension("name.") == FileTypes.NoExtension, "file type without extension");
Check(FileTypes.Extension("archive.tar.gz") == ".gz" && FileTypes.Extension("blob.d41d8cd98f00b204e98") == FileTypes.NoExtension && FileTypes.Extension("log.1") == ".1", "file type suffix rules");

// Tree accumulator: top-K eviction keeps bytes, small items fold, unaccounted space added.
var accumulator = new TreeAccumulator(@"C:\");
var bigDir = accumulator.AddDir(accumulator.Root, "big");
for (var i = 1; i <= TreeAccumulator.KeepFilesPerDirectory + 10; i++) accumulator.AddFile(bigDir, $"f{i}.bin", i * 1_000_000L, ".bin");
accumulator.AddFile(accumulator.Root, "tiny.txt", 100, ".txt");
var expectedBytes = Enumerable.Range(1, TreeAccumulator.KeepFilesPerDirectory + 10).Sum(i => i * 1_000_000L) + 100;
var built = accumulator.Build(@"C:\", expectedBytes + 500_000_000L);
Check(built.ScannedBytes == expectedBytes && built.Tree!.Size == expectedBytes + 500_000_000L, "accumulator totals with unaccounted");
Check(built.Tree!.Children.Single(c => c.Kind == "u").Size == 500_000_000L && built.UnaccountedBytes == 500_000_000L, "accumulator unaccounted node");
var bigNode = built.Tree.Children.First(c => c.Name == "big");
Check(bigNode.Size == expectedBytes - 100 && bigNode.Children.Where(c => c.Kind == "f").All(c => c.Size >= 11_000_000L), "accumulator keeps largest files");
Check(bigNode.Children.Sum(c => c.Size) == bigNode.Size, "accumulator children sum to parent");
Check(bigNode.Children.Single(c => c.Name == "f34.bin").FullPath == @"C:\big\f34.bin" && bigNode.Children[0].Kind == "o", "windows full path");

var tempDirectory = Path.Combine(Path.GetTempPath(), "disk-space-monitor-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDirectory);
try
{
    var historyPath = Path.Combine(tempDirectory, "history.csv");
    File.WriteAllText(historyPath, $"timestamp,name,used_gib\n{now.AddMinutes(-10):O},C:\\,10\n{now.AddMinutes(-2):O},C:\\,12\n");
    var store = new HistoryStore(historyPath, 30);
    Check(store.GetRecent("C:\\", now).Count == 1, "history reads only recent samples");
    var logPath = Path.Combine(tempDirectory, "monitor.log");
    var logger = new AppLogger(logPath, 256);
    for (var i = 0; i < 10; i++) logger.Info(new string('x', 80));
    Check(File.Exists(logPath + ".1") && new FileInfo(logPath).Length <= 256, "bounded log rotation");
    // Real scan: hard link counted once when checked, file sizes rounded to clusters.
    var scanRoot = Path.Combine(tempDirectory, "scan");
    Directory.CreateDirectory(Path.Combine(scanRoot, "sub"));
    File.WriteAllBytes(Path.Combine(scanRoot, "sub", "data.bin"), new byte[3 * 1024 * 1024]);
    File.WriteAllBytes(Path.Combine(scanRoot, "note.txt"), new byte[10]);
    Check(CreateHardLinkW(Path.Combine(scanRoot, "link.bin"), Path.Combine(scanRoot, "sub", "data.bin"), IntPtr.Zero), "test hard link created");
    var plain = WindowsTreeScanner.Scan(scanRoot, new ScanProgress(), CancellationToken.None);
    var deduped = WindowsTreeScanner.Scan(scanRoot, new ScanProgress(), CancellationToken.None, _ => true);
    Check(plain.ScannedFiles == 3 && deduped.ScannedFiles == 2 && deduped.HardLinkDuplicateBytes >= 3 * 1024 * 1024, "windows scan hard link dedup");
    Check(deduped.ScannedBytes % 512 == 0 && deduped.ScannedBytes >= 3 * 1024 * 1024 + 512, "windows scan cluster sizes");
    Check(deduped.Extensions.Any(e => e.Ext == ".bin") && deduped.Tree!.FullPath == scanRoot && deduped.UnaccountedBytes == 0, "windows scan subfolder root");
    var savedPath = Path.Combine(tempDirectory, "tree.json");
    deduped.Save(savedPath);
    var reloaded = DiskTreeSnapshot.Parse(File.ReadAllText(savedPath));
    Check(reloaded.ScannedBytes == deduped.ScannedBytes && reloaded.HardLinkDuplicateBytes == deduped.HardLinkDuplicateBytes && reloaded.Tree!.DominantLeaf().Name is "data.bin" or "link.bin", "tree save and reload");
    Directory.Delete(scanRoot, true);
    File.Delete(savedPath);
    File.Delete(historyPath);
    File.Delete(logPath);
    File.Delete(logPath + ".1");
}
finally { Directory.Delete(tempDirectory); }
Console.WriteLine($"{count} tests passed");

[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
