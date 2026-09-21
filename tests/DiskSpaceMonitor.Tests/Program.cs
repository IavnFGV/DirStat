using DiskSpaceMonitor;
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
    File.Delete(historyPath);
    File.Delete(logPath);
    File.Delete(logPath + ".1");
}
finally { Directory.Delete(tempDirectory); }
Console.WriteLine($"{count} tests passed");
