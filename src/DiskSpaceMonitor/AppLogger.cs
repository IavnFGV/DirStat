using System.Text;

namespace DiskSpaceMonitor;

public sealed class AppLogger
{
    private readonly string path;
    private readonly long maxBytes;
    private readonly object gate = new();

    public AppLogger(string path, long maxBytes = 1_048_576)
    {
        this.path = path;
        this.maxBytes = Math.Max(256, maxBytes);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : message + ": " + exception);

    private void Write(string level, string message)
    {
        var safe = message.Replace('\r', ' ').Replace('\n', ' ');
        if (safe.Length > 4000) safe = safe[..4000];
        var line = $"{DateTimeOffset.UtcNow:O} {level} {safe}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maxBytes)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { /* Logging must never stop monitoring. */ }
        }
    }
}
