using System.ComponentModel;
using System.Diagnostics;

namespace DiskSpaceMonitor;

/// <summary>Where a disk map comes from and how it is (re)scanned.</summary>
internal abstract class MapSource
{
    public abstract string Title { get; }
    /// <summary>File holding the last finished scan.</summary>
    public abstract string CachePath { get; }
    public abstract bool Running { get; }
    /// <summary>One line about the scan in progress, or null.</summary>
    public abstract string? ProgressText { get; }
    /// <summary>Error of the last scan attempt, cleared when a new one starts.</summary>
    public string? LastError { get; protected set; }
    /// <summary>Something the user should know about how the scan ran (e.g. no admin rights).</summary>
    public string? Notice { get; protected set; }
    public virtual bool AutoScanWhenEmpty => false;
    public virtual bool CanElevate => false;
    public abstract void StartScan(bool tryAdmin);
    public override string ToString() => Title;
}

internal sealed class WslMapSource : MapSource
{
    private readonly WslAgentManager agent;
    private readonly AppLogger logger;
    private TreeScanStatus? status;
    private string? pendingRequest;
    private DateTimeOffset pendingSince;

    public WslMapSource(WslAgentManager agent, AppLogger logger) { this.agent = agent; this.logger = logger; }

    public override string Title => "WSL  /";
    public override string CachePath => agent.TreePath;
    public override bool AutoScanWhenEmpty => true;

    /// <summary>Until the agent reports our request, its status still describes the previous scan.</summary>
    private bool Pending => pendingRequest is not null && status?.RequestId != pendingRequest && DateTimeOffset.UtcNow - pendingSince < TimeSpan.FromMinutes(1);

    public override bool Running => Pending || status?.State == "running";

    public override string? ProgressText => Pending ? "Запрос отправлен, ждём агента WSL…" :
        status?.State == "running" ? $"Сканирование WSL… {status.Files:N0} файлов, {Units.Bytes(status.Bytes)}" : null;

    public void Update(TreeScanStatus? next)
    {
        status = next;
        if (pendingRequest is not null && !Pending) pendingRequest = null;
        LastError = status?.State == "error" && !Pending ? "Ошибка сканирования WSL: " + status.Error : null;
    }

    public override void StartScan(bool tryAdmin)
    {
        pendingRequest = agent.RequestTreeScan();
        pendingSince = DateTimeOffset.UtcNow;
        LastError = null;
        logger.Info("WSL tree scan requested");
    }
}

internal sealed class WindowsMapSource : MapSource
{
    private readonly string root;
    private readonly AppLogger logger;
    private ScanProgress? localProgress;
    private Task? localScan;
    private Process? helper;
    private bool helperFailedReported;
    private static bool adminDeclined;

    public WindowsMapSource(string root, string dataDirectory, AppLogger logger)
    {
        this.root = root;
        this.logger = logger;
        CachePath = Path.Combine(dataDirectory, $"windows-tree-{char.ToUpperInvariant(root[0])}.json");
    }

    public override string Title => $"Windows  {root}";
    public override string CachePath { get; }
    public override bool CanElevate => !WindowsTreeScanner.IsElevated();
    private string ProgressPath => CachePath + ".progress";
    private string ErrorPath => CachePath + ".error";

    public override bool Running
    {
        get
        {
            if (localScan is { IsCompleted: false }) return true;
            if (helper is null) return false;
            bool exited;
            try { exited = helper.HasExited; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { exited = true; }
            if (!exited) return true;
            if (!helperFailedReported)
            {
                helperFailedReported = true;
                int? code = null;
                try { code = helper.ExitCode; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                if (code is not 0)
                {
                    var detail = File.Exists(ErrorPath) ? File.ReadAllText(ErrorPath) : $"код {code?.ToString() ?? "?"}";
                    LastError = "Сканирование с правами администратора не удалось: " + detail;
                    logger.Error($"Elevated tree scan of {root} failed: {detail}");
                }
                else logger.Info($"Elevated tree scan of {root} finished");
            }
            return false;
        }
    }

    public override string? ProgressText
    {
        get
        {
            if (!Running) return null;
            var progress = helper is not null ? WindowsTreeScanner.ReadProgress(ProgressPath) : localProgress;
            var mode = helper is not null || WindowsTreeScanner.IsElevated() ? "с правами администратора" : "без прав администратора";
            if (progress is null) return $"Сканирование {root} ({mode})… запуск";
            return $"Сканирование {root} ({mode})… {progress.Files:N0} файлов, {Units.Bytes(progress.Bytes)}   {progress.CurrentPath}";
        }
    }

    /// <summary>
    /// Elevated already: scan here. Otherwise ask UAC for a helper process; if the user
    /// declines (or it fails), scan without admin rights and say so.
    /// </summary>
    public override void StartScan(bool tryAdmin)
    {
        if (Running) return;
        LastError = null;
        Notice = null;
        if (WindowsTreeScanner.IsElevated()) { StartLocal(elevated: true); return; }
        if (tryAdmin && !adminDeclined && TryStartHelper()) return;
        Notice = adminDeclined
            ? "Права администратора не получены (отказ в запросе UAC) — сканирую без них: защищённые папки будут пропущены."
            : "Сканирование без прав администратора: защищённые папки будут пропущены.";
        StartLocal(elevated: false);
    }

    private bool TryStartHelper()
    {
        try
        {
            try { File.Delete(ErrorPath); File.Delete(ProgressPath); } catch (IOException) { }
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                // "C:" not "C:\": a trailing backslash before a quote would escape it.
                Arguments = $"{WindowsTreeScanner.ScanArgument} {root.TrimEnd('\\')} --output \"{CachePath}\""
            };
            helper?.Dispose();
            helper = Process.Start(info);
            helperFailedReported = false;
            logger.Info($"Elevated tree scan of {root} started");
            return helper is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            adminDeclined = true; // ERROR_CANCELLED: don't ask again this session
            logger.Info("UAC prompt declined; scanning without admin rights");
            return false;
        }
        catch (Exception ex)
        {
            logger.Error("Could not start elevated tree scan", ex);
            return false;
        }
    }

    private void StartLocal(bool elevated)
    {
        helper = null;
        var progress = localProgress = new ScanProgress();
        logger.Info($"Tree scan of {root} started in-process; elevated={elevated}");
        localScan = Task.Run(() =>
        {
            try
            {
                var backup = elevated && WindowsTreeScanner.EnableBackupPrivilege();
                var snapshot = WindowsTreeScanner.Scan(root, progress, CancellationToken.None, WindowsTreeScanner.WindowsDirectoryLinks());
                snapshot.BackupPrivilege = backup;
                snapshot.Save(CachePath);
                logger.Info($"Tree scan of {root} finished; files={snapshot.ScannedFiles}; seconds={snapshot.DurationSeconds}; errors={snapshot.ErrorCount}");
            }
            catch (Exception ex)
            {
                LastError = "Ошибка сканирования: " + ex.Message;
                logger.Error($"Tree scan of {root} failed", ex);
            }
        });
    }
}
