using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSpaceMonitor;

public sealed class MonitorConfig
{
    public List<string> Drives { get; set; } = new();
    public string WslDistribution { get; set; } = "Ubuntu";
    public bool WslEnabled { get; set; } = true;
    public int WslAgentIntervalSeconds { get; set; } = 5;
    public double WarningFreeGiB { get; set; } = 30;
    public double CriticalFreeGiB { get; set; } = 15;
    public double EmergencyFreeGiB { get; set; } = 8;
    public double RapidGrowthGiBPerMinute { get; set; } = 1;
    public int AlertCooldownMinutes { get; set; } = 10;
    public int HistoryRetentionDays { get; set; } = 30;

    public static MonitorConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var config = new MonitorConfig { Drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions.Pretty));
            return config;
        }
        var loaded = JsonSerializer.Deserialize<MonitorConfig>(File.ReadAllText(path), JsonOptions.Default) ?? throw new InvalidDataException("Пустая конфигурация");
        if (loaded.Drives is null || loaded.WslAgentIntervalSeconds < 2 || loaded.HistoryRetentionDays < 1 || loaded.AlertCooldownMinutes < 0 ||
            loaded.EmergencyFreeGiB < 0 || loaded.CriticalFreeGiB < loaded.EmergencyFreeGiB || loaded.WarningFreeGiB < loaded.CriticalFreeGiB || loaded.RapidGrowthGiBPerMinute < 0)
            throw new InvalidDataException("Некорректные пороги или интервалы в конфигурации");
        return loaded;
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new() { PropertyNameCaseInsensitive = true };
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
}

public enum DangerLevel { Normal, Warning, Critical, Emergency }

public sealed record UsageSample(DateTimeOffset Timestamp, double UsedGiB);

public sealed record Trend(double DeltaGiB, double GrowthGiBPerMinute, TimeSpan? Eta);

public static class Metrics
{
    public static DangerLevel Level(double freeGiB, MonitorConfig config) =>
        freeGiB < config.EmergencyFreeGiB ? DangerLevel.Emergency :
        freeGiB < config.CriticalFreeGiB ? DangerLevel.Critical :
        freeGiB < config.WarningFreeGiB ? DangerLevel.Warning : DangerLevel.Normal;

    public static Trend CalculateTrend(IReadOnlyList<UsageSample> samples, double freeGiB, DateTimeOffset now)
    {
        var recent = samples.Where(s => s.Timestamp >= now.AddMinutes(-5) && s.Timestamp <= now).OrderBy(s => s.Timestamp).ToList();
        if (recent.Count < 2) return new Trend(0, 0, null);
        var minutes = (recent[^1].Timestamp - recent[0].Timestamp).TotalMinutes;
        if (minutes <= 0) return new Trend(0, 0, null);
        var delta = recent[^1].UsedGiB - recent[0].UsedGiB;
        var rate = Math.Max(0, delta / minutes);
        return new Trend(delta, rate, rate > 0 && freeGiB > 0 ? TimeSpan.FromMinutes(freeGiB / rate) : null);
    }
}

public sealed class WslSnapshot
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Hostname { get; set; } = "";
    public double TotalGiB { get; set; }
    public double UsedGiB { get; set; }
    public double AvailableGiB { get; set; }
    public double FreeGiB { get; set; }
    public double ReservedGiB { get; set; }
    public double UsedPercent { get; set; }
    public WriterInfo? TopWriter { get; set; }
    public List<WriterInfo> Processes { get; set; } = new();
    public List<DirectoryInfoResult> Directories { get; set; } = new();
    public DateTimeOffset? DirectoryAnalysisTimestamp { get; set; }

    public static WslSnapshot Parse(string json)
    {
        var result = JsonSerializer.Deserialize<WslSnapshot>(json, JsonOptions.Default) ?? throw new InvalidDataException("Пустой JSON агента");
        if (result.SchemaVersion != 1 || result.Timestamp == default || result.TotalGiB < 0 || result.UsedGiB < 0 || result.AvailableGiB < 0 || result.FreeGiB < 0)
            throw new InvalidDataException("Неподдерживаемая схема или некорректные метрики WSL");
        return result;
    }
}

public sealed class WriterInfo
{
    public int Pid { get; set; }
    public string Command { get; set; } = "";
    public double WriteBytesPerSecond { get; set; }
}

public sealed class DirectoryInfoResult
{
    public string Path { get; set; } = "";
    public double SizeGiB { get; set; }
    public string? Error { get; set; }
}
