using System.Reflection;
using System.Runtime.InteropServices;
using AgoraMCP.Database;
using AgoraMCP.McpServer.Watch;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Strictly read-only diagnostic health check (`agora doctor [--db PATH]`):
/// reports binary build, SQLite WAL/schema/migration health (without mutating
/// or migrating the DB), active NamedBoardEvent listeners, session beacons,
/// per-session hook cursors (for the default DB), and recent `notify.log`
/// entries scoped to `--db`.
/// </summary>
public static class Doctor
{
    public static int Run(string dbPath, TextWriter? stdout = null)
    {
        var @out = stdout ?? Console.Out;
        var fullDb = Path.GetFullPath(dbPath);
        var defaultDb = Path.GetFullPath(Config.ResolveDbPath(null));
        var isDefaultDb = string.Equals(
            fullDb,
            defaultDb,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
                // Strictly read-only: when no -wal/-shm sidecar exists, open with
                // immutable=1 so SQLite creates ZERO -wal/-shm files on a cleanly closed DB;
                // when -wal exists, use standard ReadOnly so live WAL writes remain visible.
                var hasWalSidecar = File.Exists(fullDb + "-wal") || File.Exists(fullDb + "-shm");
                var connStr = hasWalSidecar
                    ? new SqliteConnectionStringBuilder
                    {
                        DataSource = fullDb,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()
                    : $"Data Source={new Uri(fullDb).AbsoluteUri}?mode=ro&immutable=1;Pooling=False";
                using var db = new SqliteConnection(connStr);
                db.Open();

                var journal = db.Scalar<string>("PRAGMA journal_mode");
                if (!hasWalSidecar && IsWalHeader(fullDb))
                    journal = "wal";
                var userVer = db.Scalar<long>("PRAGMA user_version");
                var needsMigration = userVer < Migrations.CurrentVersion;

                var hasThreads = TableExists(db, "threads");
                var hasMessages = TableExists(db, "messages");
                var hasTurns = TableExists(db, "turn_events");

                var threads = hasThreads ? db.Scalar<long>("SELECT COUNT(*) FROM threads") : 0;
                var messages = hasMessages ? db.Scalar<long>("SELECT COUNT(*) FROM messages") : 0;
                var turns = hasTurns ? db.Scalar<long>("SELECT COUNT(*) FROM turn_events") : 0;

                var hasAtMsgCol = hasTurns && ColumnExists(db, "turn_events", "at_msg_id");
                var zeroAtText = hasAtMsgCol
                    ? db.Scalar<long>("SELECT COUNT(*) FROM turn_events WHERE at_msg_id = 0").ToString()
                    : "col-missing";
                var hasTrig = db.Scalar<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='trg_turn_events_at_msg_id'") > 0;

                var statusLabel = needsMigration ? "NEEDS MIGRATION" : "OK";
                @out.WriteLine($"  db status: {statusLabel} ({sizeKb:0.0} KB, journal={journal}, user_version={userVer}/{Migrations.CurrentVersion})");
                @out.WriteLine($"  rows:      threads={threads}, messages={messages}, turn_events={turns} (at_msg_id=0: {zeroAtText}, trigger={(hasTrig ? "ok" : "MISSING")})");
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

        if (!isDefaultDb)
        {
            @out.WriteLine("  session beacons / hook cursors: (skipped — inspecting custom --db)");
        }
        else
        {
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
        }

        // Recent notify.log scoped to this DB
        var logPath = NotifyLog.LogPathForDb(fullDb);
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

    private static bool IsWalHeader(string dbPath)
    {
        try
        {
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> hdr = stackalloc byte[20];
            return fs.Read(hdr) >= 20 && hdr[18] == 2 && hdr[19] == 2;
        }
        catch
        {
            return false;
        }
    }

    private static bool TableExists(SqliteConnection db, string table) =>
        db.Prepare("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t", "$t")
            .Set("$t", table)
            .Query(r => r.GetInt64(0))
            .FirstOrDefault() > 0;

    private static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
