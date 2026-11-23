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

    public static string DefaultLogPath => LogPathForDb(null);

    /// <summary>
    /// Resolves the structured notification log path. Honors <c>AGORA_NOTIFY_LOG</c>
    /// (<c>off</c>/<c>none</c> disables logging; any other value is used directly).
    /// Otherwise logs beside <paramref name="dbPath"/> (<c>&lt;dbDir&gt;/logs/notify.log</c>)
    /// so temporary/isolated <c>--db</c> runs never pollute the default live log,
    /// while the default DB still logs to <c>%LOCALAPPDATA%\AgoraMCP\logs\notify.log</c>.
    /// </summary>
    public static string LogPathForDb(string? dbPath)
    {
        var env = Environment.GetEnvironmentVariable("AGORA_NOTIFY_LOG");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        if (!string.IsNullOrWhiteSpace(dbPath) && !dbPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                if (!string.IsNullOrEmpty(dir))
                    return Path.Combine(dir, "logs", "notify.log");
            }
            catch { }
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApp) ? Path.GetTempPath() : localApp;
        return Path.Combine(root, "AgoraMCP", "logs", "notify.log");
    }

    public static void Write(
        string component,
        string agent,
        string reason,
        long? cursor = null,
        long? turnCursor = null,
        string? logPath = null,
        string? dbPath = null)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(logPath) ? logPath! : LogPathForDb(dbPath);
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
