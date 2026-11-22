using System.Reflection;
using System.Text;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Shared, best-effort structured append log at
/// <c>%LOCALAPPDATA%\AgoraMCP\logs\notify.log</c> (auto-rotated at ~2 MB) so
/// operators and `agora doctor` can inspect wake, cap, skip, and error events
/// across tail, bridge, hook-check, and channel-bridge without hunting per-client stderr.
/// </summary>
public static class NotifyLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string BuildVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public static string DefaultLogPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("AGORA_NOTIFY_LOG");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = string.IsNullOrWhiteSpace(localApp) ? Path.GetTempPath() : localApp;
            return Path.Combine(root, "AgoraMCP", "logs", "notify.log");
        }
    }

    public static void Write(
        string component,
        string agent,
        string reason,
        long? cursor = null,
        long? turnCursor = null,
        string? logPath = null)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(logPath) ? DefaultLogPath : logPath!;
            if (path.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var cursorPart = cursor.HasValue || turnCursor.HasValue
                ? $" cursor={cursor?.ToString() ?? "-"}/{turnCursor?.ToString() ?? "-"}"
                : "";
            var safeReason = (reason ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            var line = $"[{stamp}] pid={Environment.ProcessId} build={BuildVersion} component={component} agent={agent}{cursorPart} {safeReason}\n";

            lock (Sync)
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length >= MaxBytes)
                    {
                        var archive = path + ".1";
                        try { File.Move(path, archive, overwrite: true); }
                        catch { }
                    }
                }
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never break notification delivery or hooks.
        }
    }
}
