using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DiskSpaceMonitor;

public sealed record DiskReading(string Name, double TotalGiB, double UsedGiB, double FreeGiB, double UsedPercent, Trend Trend, DangerLevel Level, string? Error = null);

public sealed class HistoryStore
{
    private readonly string path;
    private readonly Dictionary<string, List<UsageSample>> samples = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset lastWrite;
    private DateTimeOffset lastPrune = DateTimeOffset.UtcNow;
    private readonly int retentionDays;

    public HistoryStore(string path, int retentionDays)
    {
        this.path = path;
        this.retentionDays = retentionDays;
        if (!File.Exists(path)) return;
        var expired = false;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length != 3 || !DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var used)) continue;
            if (timestamp < DateTimeOffset.UtcNow.AddDays(-retentionDays)) { expired = true; continue; }
            Add(fields[1], new UsageSample(timestamp, used));
            if (timestamp > lastWrite) lastWrite = timestamp;
        }
        foreach (var list in samples.Values) list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        if (expired) Rewrite();
    }

    private void Add(string name, UsageSample sample)
    {
        if (!samples.TryGetValue(name, out var list)) samples[name] = list = new();
        list.Add(sample);
    }

    public IReadOnlyList<UsageSample> GetRecent(string name, DateTimeOffset now)
    {
        if (!samples.TryGetValue(name, out var list)) return Array.Empty<UsageSample>();
        var cutoff = now.AddMinutes(-5);
        var recent = new List<UsageSample>(12);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Timestamp < cutoff) break;
            if (list[i].Timestamp <= now) recent.Add(list[i]);
        }
        recent.Reverse();
        return recent;
    }

    public void Record(IReadOnlyDictionary<string, double> current, DateTimeOffset now)
    {
        if (now - lastWrite < TimeSpan.FromSeconds(30)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "timestamp,name,used_gib\n", Encoding.UTF8);
        var lines = current.Select(pair => $"{now:O},{pair.Key},{pair.Value.ToString("R", CultureInfo.InvariantCulture)}");
        File.AppendAllLines(path, lines, Encoding.UTF8);
        foreach (var pair in current) Add(pair.Key, new UsageSample(now, pair.Value));
        lastWrite = now;
        if (now.Date > lastPrune.Date) { Prune(now); lastPrune = now; }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-retentionDays);
        var changed = false;
        foreach (var list in samples.Values) changed |= list.RemoveAll(s => s.Timestamp < cutoff) > 0;
        if (!changed) return;
        Rewrite();
    }

    private void Rewrite()
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, Encoding.UTF8))
        {
            writer.WriteLine("timestamp,name,used_gib");
            foreach (var pair in samples)
                foreach (var sample in pair.Value)
                    writer.WriteLine($"{sample.Timestamp:O},{pair.Key},{sample.UsedGiB.ToString("R", CultureInfo.InvariantCulture)}");
        }
        File.Move(temp, path, true);
    }
}

public sealed class WslAgentManager : IDisposable
{
    private readonly string configuredDistribution, script, output, request;
    private readonly AppLogger logger;
    private string distribution = "";
    private readonly int interval;
    private Process? process;
    private DateTimeOffset nextRetry;
    private DateTimeOffset launchTime;
    private int consecutiveFailures;
    private bool exitReported;
    private string? lastReadError;
    private DateTime lastMetricsWrite;
    private long lastMetricsLength;
    private WslSnapshot? cachedSnapshot;
    private bool disposed;
    public string Status { get; private set; } = "Запуск";
    public string? Distribution => distribution.Length == 0 ? null : distribution;

    public WslAgentManager(string distribution, string script, string dataDirectory, int interval, AppLogger logger)
    {
        configuredDistribution = distribution;
        this.logger = logger;
        this.script = script;
        this.interval = interval;
        output = Path.Combine(dataDirectory, "wsl-metrics.json");
        request = Path.Combine(dataDirectory, "wsl-analysis-request.json");
    }

    private async Task<string> WslPathAsync(string windowsPath)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        p.StartInfo.ArgumentList.Add("-d"); p.StartInfo.ArgumentList.Add(distribution);
        p.StartInfo.ArgumentList.Add("--"); p.StartInfo.ArgumentList.Add("wslpath"); p.StartInfo.ArgumentList.Add("-a"); p.StartInfo.ArgumentList.Add("-u"); p.StartInfo.ArgumentList.Add(WslDiscovery.NormalizeWindowsPath(windowsPath));
        p.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var value = (await p.StandardOutput.ReadToEndAsync()).Trim();
        if (p.ExitCode != 0 || value.Length == 0) throw new InvalidOperationException("Не удалось преобразовать путь для WSL: " + (await p.StandardError.ReadToEndAsync()).Trim());
        return value;
    }

    public async Task EnsureRunningAsync(bool force = false)
    {
        if (disposed) return;
        var now = DateTimeOffset.UtcNow;
        if (force) { consecutiveFailures = 0; logger.Info("Manual WSL agent restart"); }
        if (!force && process is { HasExited: false })
        {
            if (now - launchTime > TimeSpan.FromMinutes(1)) consecutiveFailures = 0;
            return;
        }
        if (process is { HasExited: true } && !exitReported)
        {
            consecutiveFailures++;
            nextRetry = now.AddSeconds(RetrySeconds());
            Status = "WSL недоступен: агент завершился";
            logger.Error($"WSL agent exited with code {process.ExitCode}; retry in {RetrySeconds()}s");
            exitReported = true;
        }
        if (!force && now < nextRetry) return;
        Stop();
        try
        {
            distribution = await WslDiscovery.ResolveAsync(configuredDistribution);
            var paths = await Task.WhenAll(WslPathAsync(script), WslPathAsync(output), WslPathAsync(request));
            var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-d", distribution, "--", "python3", "-u", paths[0], "--output", paths[1], "--request", paths[2], "--interval", interval.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
            process = new Process { StartInfo = info };
            process.Start();
            Status = "Агент запущен: " + distribution;
            launchTime = now;
            exitReported = false;
            nextRetry = now.AddSeconds(20);
            logger.Info($"WSL agent started; distribution={distribution}; pid={process.Id}; interval={interval}s");
        }
        catch (Exception ex)
        {
            Stop();
            consecutiveFailures++;
            Status = "WSL недоступен: " + ex.Message;
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(RetrySeconds());
            logger.Error($"WSL agent start failed; retry in {RetrySeconds()}s", ex);
        }
    }

    private int RetrySeconds() => Math.Min(300, 30 * (1 << Math.Min(4, Math.Max(0, consecutiveFailures - 1))));

    public WslSnapshot? Read()
    {
        if (process is { HasExited: true }) Status = "WSL недоступен: агент завершился";
        try
        {
            var file = new FileInfo(output);
            if (!file.Exists) return null;
            if (cachedSnapshot is not null && file.LastWriteTimeUtc == lastMetricsWrite && file.Length == lastMetricsLength)
                return cachedSnapshot;
            var snapshot = WslSnapshot.Parse(File.ReadAllText(output));
            lastMetricsWrite = file.LastWriteTimeUtc;
            lastMetricsLength = file.Length;
            cachedSnapshot = snapshot;
            if (lastReadError is not null) { logger.Info("WSL metrics JSON readable again"); lastReadError = null; }
            return snapshot;
        }
        catch (Exception ex)
        {
            Status = "Ошибка JSON WSL: " + ex.Message;
            if (lastReadError != ex.Message) { logger.Error("WSL metrics JSON read failed", ex); lastReadError = ex.Message; }
            return null;
        }
    }

    public void RequestAnalysis()
    {
        var temp = request + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { requestId = Guid.NewGuid().ToString("N"), timestamp = DateTimeOffset.UtcNow }));
        File.Move(temp, request, true);
    }

    private void Stop()
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        process.Dispose();
        process = null;
    }

    public void Dispose() { disposed = true; logger.Info("Stopping WSL agent"); Stop(); }
}

public static class WslDiscovery
{
    public static string NormalizeWindowsPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    public static IReadOnlyList<string> ParseList(byte[] output)
    {
        var isUtf16 = output.Length >= 2 && (output[0] == 0xff && output[1] == 0xfe ||
            output.Where((_, index) => index % 2 == 1).Count(value => value == 0) > output.Length / 6);
        var text = isUtf16 ? Encoding.Unicode.GetString(output) : Encoding.UTF8.GetString(output);
        return text.TrimStart('\ufeff').Split(new[] { '\r', '\n', '\0' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string Choose(IReadOnlyList<string> names)
    {
        var selected = names.FirstOrDefault(name => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase));
        return selected ?? throw new InvalidOperationException("В WSL нет пользовательского дистрибутива");
    }

    public static async Task<string> ResolveAsync(string configured)
    {
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)) return configured;
        using var process = new Process { StartInfo = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--list");
        process.StartInfo.ArgumentList.Add("--quiet");
        process.Start();
        using var memory = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(memory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        await outputTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("Не удалось получить список дистрибутивов WSL");
        return Choose(ParseList(memory.ToArray()));
    }
}
