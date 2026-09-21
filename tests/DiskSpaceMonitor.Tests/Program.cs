using DiskSpaceMonitor;

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
var now = DateTimeOffset.UtcNow;
var trend = Metrics.CalculateTrend(new[] { new UsageSample(now.AddMinutes(-5), 10), new UsageSample(now, 15) }, 10, now);
Check(Math.Abs(trend.DeltaGiB - 5) < 0.0001, "delta");
Check(Math.Abs(trend.GrowthGiBPerMinute - 1) < 0.0001, "growth rate");
Check(trend.Eta == TimeSpan.FromMinutes(10), "ETA");
Check(Metrics.CalculateTrend(new[] { new UsageSample(now.AddMinutes(-5), 15), new UsageSample(now, 10) }, 10, now).Eta is null, "no ETA on shrink");
var json = """{"schemaVersion":1,"timestamp":"2026-09-21T12:00:00Z","hostname":"ubuntu","totalGiB":100,"usedGiB":60,"availableGiB":35,"freeGiB":40,"reservedGiB":5,"usedPercent":60,"topWriter":{"pid":123,"command":"python3","writeBytesPerSecond":2048}}""";
var snapshot = WslSnapshot.Parse(json);
Check(snapshot.Hostname == "ubuntu" && snapshot.TopWriter?.Pid == 123 && snapshot.ReservedGiB == 5, "JSON parse");
try { WslSnapshot.Parse(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")); throw new Exception("schema validation failed"); }
catch (InvalidDataException) { Check(true, "schema validation"); }
Console.WriteLine($"{count} tests passed");
