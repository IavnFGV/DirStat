using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DiskSpaceMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\DiskSpaceMonitor.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MonitorContext()); }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить Disk Space Monitor: {ex.Message}\nПроверьте права записи рядом с EXE.",
                "Disk Space Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class MonitorContext : ApplicationContext
{
    private readonly string dataDirectory = AppContext.BaseDirectory;
    private readonly string configPath;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private readonly HistoryStore history;
    private readonly WslAgentManager? agent;
    private readonly MonitorConfig config;
    private readonly Dictionary<string, DateTimeOffset> alertTimes = new();
    private readonly StatusForm statusForm = new();
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem analysisItem;
    private List<DiskReading> drives = new();
    private WslSnapshot? wsl;
    private Trend wslTrend = new(0, 0, null);
    private DangerLevel previousWslLevel = DangerLevel.Normal;
    private bool polling, blink;
    private DateTimeOffset lastAnalysisRequest;

    public MonitorContext()
    {
        Directory.CreateDirectory(dataDirectory);
        configPath = Path.Combine(dataDirectory, "config.json");
        config = MonitorConfig.Load(configPath);
        history = new HistoryStore(Path.Combine(dataDirectory, "history.csv"), config.HistoryRetentionDays);
        if (config.WslEnabled) agent = new WslAgentManager(config.WslDistribution, Path.Combine(AppContext.BaseDirectory, "wsl", "agent.py"), dataDirectory, config.WslAgentIntervalSeconds);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать состояние", null, (_, _) => ShowStatus());
        menu.Items.Add("Обновить сейчас", null, async (_, _) => await RefreshAsync(true));
        menu.Items.Add("Запустить / перезапустить агент WSL", null, async (_, _) => { if (agent is not null) await agent.EnsureRunningAsync(true); await RefreshAsync(true); });
        analysisItem = new ToolStripMenuItem("Анализировать каталоги WSL", null, (_, _) => RequestAnalysis());
        analysisItem.Enabled = agent is not null;
        menu.Items.Add(analysisItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть конфигурацию", null, (_, _) => Open(configPath));
        menu.Items.Add("Открыть каталог данных", null, (_, _) => Open(dataDirectory));
        startupItem = new ToolStripMenuItem("Автозапуск с Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled(), CheckOnClick = false };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Disk Space Monitor", Icon = MakeIcon(Color.LimeGreen) };
        tray.DoubleClick += (_, _) => ShowStatus();
        timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += async (_, _) => await RefreshAsync(false);
        timer.Start();
        _ = RefreshAsync(true);
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("DiskSpaceMonitor") is string value && value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (IsStartupEnabled()) key.DeleteValue("DiskSpaceMonitor", false);
        else key.SetValue("DiskSpaceMonitor", $"\"{Environment.ProcessPath}\"");
        startupItem.Checked = IsStartupEnabled();
    }

    private void RequestAnalysis()
    {
        if (agent is null || DateTimeOffset.UtcNow - lastAnalysisRequest < TimeSpan.FromMinutes(10)) return;
        try { agent.RequestAnalysis(); lastAnalysisRequest = DateTimeOffset.UtcNow; analysisItem.Enabled = false; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка анализа", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task RefreshAsync(bool force)
    {
        if (polling) return;
        polling = true;
        try
        {
            if (agent is not null) await agent.EnsureRunningAsync();
            var now = DateTimeOffset.UtcNow;
            var next = new List<DiskReading>();
            var values = new Dictionary<string, double>();
            foreach (var name in config.Drives)
            {
                try
                {
                    var drive = new DriveInfo(name);
                    var total = drive.TotalSize / 1073741824d;
                    var free = drive.AvailableFreeSpace / 1073741824d;
                    var used = total - free;
                    var trendSamples = history.Get(name).ToList();
                    trendSamples.Add(new UsageSample(now, used));
                    var trend = Metrics.CalculateTrend(trendSamples, free, now);
                    var level = Metrics.Level(free, config);
                    next.Add(new DiskReading(name, total, used, free, total > 0 ? used / total * 100 : 0, trend, level));
                    values[name] = used;
                    Alert(name, level, trend, free);
                }
                catch (Exception ex) { next.Add(new DiskReading(name, 0, 0, 0, 0, new Trend(0, 0, null), DangerLevel.Normal, ex.Message)); }
            }
            drives = next;
            wsl = agent?.Read();
            if (wsl is not null)
            {
                var age = now - wsl.Timestamp;
                if (age < TimeSpan.FromSeconds(Math.Max(30, config.WslAgentIntervalSeconds * 3)))
                {
                    var name = "WSL /";
                    var trendSamples = history.Get(name).ToList();
                    trendSamples.Add(new UsageSample(now, wsl.UsedGiB));
                    wslTrend = Metrics.CalculateTrend(trendSamples, wsl.AvailableGiB, now);
                    var wslLevel = Metrics.Level(wsl.AvailableGiB, config);
                    Alert(name, wslLevel, wslTrend, wsl.AvailableGiB);
                    if (wslLevel >= DangerLevel.Critical && previousWslLevel < DangerLevel.Critical) RequestAnalysis();
                    previousWslLevel = wslLevel;
                    values[name] = wsl.UsedGiB;
                }
            }
            history.Record(values, now);
            UpdateTray(now);
            if (statusForm.Visible) statusForm.UpdateData(drives, wsl, WslStatus(now), config, wslTrend, VhdxLocator.Get(agent?.Distribution ?? config.WslDistribution));
            if (force && statusForm.Visible) statusForm.Activate();
            analysisItem.Enabled = agent is not null && now - lastAnalysisRequest >= TimeSpan.FromMinutes(10);
        }
        catch (Exception ex)
        {
            var message = "Ошибка обновления: " + ex.Message;
            tray.Text = message[..Math.Min(63, message.Length)];
        }
        finally { polling = false; }
    }

    private void Alert(string name, DangerLevel level, Trend trend, double free)
    {
        if (level >= DangerLevel.Warning) Notify(name, level.ToString(), $"{name}: свободно {free:F1} GiB ({level})");
        if (trend.GrowthGiBPerMinute >= config.RapidGrowthGiBPerMinute) Notify(name, "rapid", $"Быстрый рост: {name}, {trend.GrowthGiBPerMinute:F2} GiB/мин");
    }

    private void Notify(string name, string kind, string message)
    {
        var key = name + kind;
        var now = DateTimeOffset.UtcNow;
        if (alertTimes.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(config.AlertCooldownMinutes)) return;
        alertTimes[key] = now;
        tray.ShowBalloonTip(5000, "Disk Space Monitor", message, ToolTipIcon.Warning);
    }

    private string WslStatus(DateTimeOffset now) => !config.WslEnabled ? "отключен" : wsl is null ? agent?.Status ?? "недоступен" : now - wsl.Timestamp > TimeSpan.FromSeconds(Math.Max(30, config.WslAgentIntervalSeconds * 3)) ? "устаревшие метрики" : "работает";

    private void UpdateTray(DateTimeOffset now)
    {
        var free = drives.Where(d => d.Error is null).Select(d => d.FreeGiB).Concat(wsl is not null && WslStatus(now) == "работает" ? new[] { wsl.AvailableGiB } : Array.Empty<double>()).DefaultIfEmpty(double.NaN).Min();
        var level = double.IsNaN(free) ? DangerLevel.Normal : Metrics.Level(free, config);
        blink = !blink;
        var color = level == DangerLevel.Emergency ? (blink ? Color.Red : Color.White) : level == DangerLevel.Critical ? Color.Red : level == DangerLevel.Warning ? Color.Gold : Color.LimeGreen;
        var old = tray.Icon;
        tray.Icon = MakeIcon(color);
        old?.Dispose();
        var freeText = double.IsNaN(free) ? "нет данных" : $"{free:F1} GiB";
        tray.Text = $"Мин. остаток: {freeText}; WSL: {WslStatus(now)}"[..Math.Min(63, $"Мин. остаток: {freeText}; WSL: {WslStatus(now)}".Length)];
    }

    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 26, 26);
        g.DrawEllipse(Pens.Black, 3, 3, 26, 26);
        var handle = bitmap.GetHicon();
        using var source = Icon.FromHandle(handle);
        var result = (Icon)source.Clone();
        DestroyIcon(handle);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    private void ShowStatus()
    {
        statusForm.UpdateData(drives, wsl, WslStatus(DateTimeOffset.UtcNow), config, wslTrend, VhdxLocator.Get(agent?.Distribution ?? config.WslDistribution));
        statusForm.Show();
        statusForm.WindowState = FormWindowState.Normal;
        statusForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop(); timer.Dispose();
        agent?.Dispose();
        tray.Visible = false; tray.Dispose();
        statusForm.Dispose();
        base.ExitThreadCore();
    }
}
