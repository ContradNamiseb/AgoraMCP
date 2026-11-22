using System.Reflection;
using System.Runtime.InteropServices;
using AgoraMCP.Database;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Diagnostic health check (`agora doctor [--db PATH]`): reports binary build,
/// SQLite WAL/schema/migration health, active NamedBoardEvent listeners,
/// session beacons, per-session hook cursors, and recent `notify.log` entries.
/// </summary>
public static class Doctor
{
    public static int Run(string dbPath, TextWriter? stdout = null)
    {
        var @out = stdout ?? Console.Out;
        var fullDb = Path.GetFullPath(dbPath);
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var procPath = Environment.ProcessPath ?? "(unknown)";

        @out.WriteLine($"agora doctor — build {ver} ({RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription})");
        @out.WriteLine($"  binary: {procPath}");
        @out.WriteLine($"  db:     {fullDb}");

        if (!File.Exists(fullDb))
        {
            @out.WriteLine("  db status: MISSING (will be initialized on first serve/write)");
        }
        else
        {
            try
            {
                var sizeKb = new FileInfo(fullDb).Length / 1024.0;
                using var db = AgoraDatabase.Open(fullDb);
                var journal = db.Scalar<string>("PRAGMA journal_mode");
                var userVer = db.Scalar<long>("PRAGMA user_version");
                var threads = db.Scalar<long>("SELECT COUNT(*) FROM threads");
                var messages = db.Scalar<long>("SELECT COUNT(*) FROM messages");
                var turns = db.Scalar<long>("SELECT COUNT(*) FROM turn_events");
                var zeroAt = db.Scalar<long>("SELECT COUNT(*) FROM turn_events WHERE at_msg_id = 0");
                var hasTrig = db.Scalar<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='trg_turn_events_at_msg_id'") > 0;

                @out.WriteLine($"  db status: OK ({sizeKb:0.0} KB, journal={journal}, user_version={userVer}/{Migrations.CurrentVersion})");
                @out.WriteLine($"  rows:      threads={threads}, messages={messages}, turn_events={turns} (at_msg_id=0: {zeroAt}, trigger={(hasTrig ? "ok" : "MISSING")})");
            }
            catch (Exception e)
            {
                @out.WriteLine($"  db status: ERROR ({e.Message})");
            }
        }

        // Watch/tail/bridge/channel event listeners
        var evDir = NamedBoardEvent.EventsDirectoryFor(fullDb);
        if (Directory.Exists(evDir))
        {
            var files = Directory.GetFiles(evDir, "*.event");
            var live = 0;
            var stale = 0;
            foreach (var f in files)
            {
                if (IsEventLive(f)) live += 1;
                else stale += 1;
            }
            @out.WriteLine($"  listeners ({evDir}): {files.Length} registered ({live} live, {stale} stale)");
        }
        else
        {
            @out.WriteLine($"  listeners: none ({evDir} not created yet)");
        }

        // Session beacons
        var sessionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgoraMCP", "sessions");
        if (Directory.Exists(sessionsDir))
        {
            var beacons = Directory.GetFiles(sessionsDir, "*.json");
            @out.WriteLine($"  session beacons ({sessionsDir}): {beacons.Length}");
            foreach (var b in beacons.Take(10))
            {
                var parsed = HookCheck.LoadSessionBeacon(b);
                if (parsed is null)
                {
                    @out.WriteLine($"    - {Path.GetFileName(b)}: unreadable");
                }
                else
                {
                    var ageNote = DateTimeOffset.TryParse(parsed.UpdatedAt, out var dt)
                        ? $"{(DateTimeOffset.UtcNow - dt).TotalMinutes:0.0}m ago"
                        : "missing/invalid updated_at";
                    @out.WriteLine($"    - {parsed.Agent}: session={parsed.SessionId} ({ageNote})");
                }
            }
        }
        else
        {
            @out.WriteLine("  session beacons: none");
        }

        // Hook cursor files
        var hooksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgoraMCP", "hooks");
        if (Directory.Exists(hooksDir))
        {
            var hookFiles = Directory.GetFiles(hooksDir, "hook-*.json");
            @out.WriteLine($"  hook cursors ({hooksDir}): {hookFiles.Length} file(s)");
        }
        else
        {
            @out.WriteLine("  hook cursors: none");
        }

        // Recent notify.log
        var logPath = NotifyLog.DefaultLogPath;
        if (logPath.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            logPath.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            @out.WriteLine("  notify.log: disabled (AGORA_NOTIFY_LOG=off)");
        }
        else if (File.Exists(logPath))
        {
            @out.WriteLine($"  notify.log: {logPath}");
            try
            {
                var tail = File.ReadLines(logPath).TakeLast(8).ToList();
                foreach (var line in tail)
                    @out.WriteLine($"    {line}");
            }
            catch (Exception e)
            {
                @out.WriteLine($"    (unreadable: {e.Message})");
            }
        }
        else
        {
            @out.WriteLine($"  notify.log: none yet ({logPath})");
        }

        return 0;
    }

    private static bool IsEventLive(string eventFile)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsWindowsEventLive(eventFile);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsWindowsEventLive(string eventFile)
    {
        try
        {
            var name = File.ReadAllText(eventFile).Trim();
            if (name.Length == 0) return false;
            using var h = EventWaitHandle.OpenExisting(name);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
