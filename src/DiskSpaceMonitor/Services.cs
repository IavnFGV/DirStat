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
        if (expired) Rewrite();
    }

    private void Add(string name, UsageSample sample)
    {
        if (!samples.TryGetValue(name, out var list)) samples[name] = list = new();
        list.Add(sample);
    }

    public IReadOnlyList<UsageSample> Get(string name) => samples.TryGetValue(name, out var list) ? list : Array.Empty<UsageSample>();

    public void Record(IReadOnlyDictionary<string, double> current, DateTimeOffset now)
    {
        if (now - lastWrite < TimeSpan.FromSeconds(30)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "timestamp,name,used_gib\n", Encoding.UTF8);
        var lines = current.Select(pair => $"{now:O},{pair.Key},{pair.Value.ToString("R", CultureInfo.InvariantCulture)}");
        File.AppendAllLines(path, lines, Encoding.UTF8);
        foreach (var pair in current) Add(pair.Key, new UsageSample(now, pair.Value));
        lastWrite = now;
        Prune(now);
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
    private readonly string distribution, script, output, request;
    private readonly int interval;
    private Process? process;
    private DateTimeOffset nextRetry;
    private bool disposed;
    public string Status { get; private set; } = "Запуск";

    public WslAgentManager(string distribution, string script, string dataDirectory, int interval)
    {
        this.distribution = distribution;
        this.script = script;
        this.interval = interval;
        output = Path.Combine(dataDirectory, "wsl-metrics.json");
        request = Path.Combine(dataDirectory, "wsl-analysis-request.json");
    }

    private async Task<string> WslPathAsync(string windowsPath)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        p.StartInfo.ArgumentList.Add("-d"); p.StartInfo.ArgumentList.Add(distribution);
        p.StartInfo.ArgumentList.Add("--"); p.StartInfo.ArgumentList.Add("wslpath"); p.StartInfo.ArgumentList.Add("-a"); p.StartInfo.ArgumentList.Add("-u"); p.StartInfo.ArgumentList.Add(windowsPath);
        p.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await p.WaitForExitAsync(timeout.Token);
        var value = (await p.StandardOutput.ReadToEndAsync()).Trim();
        if (p.ExitCode != 0 || value.Length == 0) throw new InvalidOperationException("WSL дистрибутив недоступен: " + (await p.StandardError.ReadToEndAsync()).Trim());
        return value;
    }

    public async Task EnsureRunningAsync(bool force = false)
    {
        if (disposed || (!force && process is { HasExited: false }) || (!force && DateTimeOffset.UtcNow < nextRetry)) return;
        Stop();
        try
        {
            var paths = await Task.WhenAll(WslPathAsync(script), WslPathAsync(output), WslPathAsync(request));
            var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-d", distribution, "--", "python3", "-u", paths[0], "--output", paths[1], "--request", paths[2], "--interval", interval.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
            process = new Process { StartInfo = info };
            process.Start();
            Status = "Агент запущен";
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(20);
        }
        catch (Exception ex)
        {
            Status = "WSL недоступен: " + ex.Message;
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }

    public WslSnapshot? Read()
    {
        if (process is { HasExited: true }) Status = "WSL недоступен: агент завершился";
        try
        {
            if (!File.Exists(output)) return null;
            return WslSnapshot.Parse(File.ReadAllText(output));
        }
        catch (Exception ex) { Status = "Ошибка JSON WSL: " + ex.Message; return null; }
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

    public void Dispose() { disposed = true; Stop(); }
}
