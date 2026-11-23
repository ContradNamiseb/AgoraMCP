using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Hub-less, zero-CPU board event streamer (`agora tail`): waits on its own
/// ephemeral Windows <see cref="NamedBoardEvent"/> (falling back to a coarse
/// async delay on non-Windows or missed signals) and writes one UTF-8 JSON
/// <see cref="BoardEvent"/> line to stdout whenever peer mail or a turn pass
/// arrives for the target agent.
///
/// <para>Supports <c>--parent-pid</c>, <c>--exit-with-parent</c>, and
/// <c>--exit-on-stdin-eof</c> so host-spawned tails exit promptly if their
/// parent process crashes without sending SIGTERM.</para>
/// </summary>
public static partial class BoardTail
{
    public const double DefaultResumeWindowSeconds = 24 * 60 * 60; // 24 hours
    public const int DefaultResumeLimit = 5;

    public sealed class TailCursorState
    {
        [JsonPropertyName("last_msg_id")]
        public long LastMsgId { get; set; }

        [JsonPropertyName("last_turn_id")]
        public long LastTurnId { get; set; }

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = "";
    }

    public sealed record Options
    {
        public required string DbPath { get; init; }
        public required string Agent { get; init; }
        /// <summary>Starting message cursor; null = start at current MAX(id) (or saved cursor when Resume=true).</summary>
        public long? Since { get; init; }
        /// <summary>Starting turn_events cursor; null = start at current MAX(id) (or 0 when Since == 0).</summary>
        public long? SinceTurn { get; init; }
        /// <summary>Include open broadcast threads in addition to directed mail and DMs.</summary>
        public bool IncludeAll { get; init; }
        /// <summary>Only emit direct messages (threads.kind = 'dm').</summary>
        public bool DmsOnly { get; init; }
        /// <summary>Exit 0 immediately after printing the first matching batch.</summary>
        public bool Once { get; init; }
        /// <summary>Optional maximum seconds to wait before exiting 0 cleanly.</summary>
        public double? TimeoutSeconds { get; init; }
        /// <summary>Coarse safety-net interval in seconds (default 5s Windows, 1s elsewhere).</summary>
        public double IntervalSeconds { get; init; } = NamedBoardEvent.IsSupported ? 5 : 1;
        /// <summary>Exit cleanly if this parent PID exits (cross-platform).</summary>
        public int? ParentPid { get; init; }
        /// <summary>Auto-detect current parent PID on Windows and exit cleanly if it terminates.</summary>
        public bool ExitWithParent { get; init; }
        /// <summary>Exit cleanly when stdin reaches EOF (for hosts that keep a stdin pipe open).</summary>
        public bool ExitOnStdinEof { get; init; }
        /// <summary>Emit an initial {"kind":"ready","seq":...,"turn_seq":...} control line right after cursor sampling (I2).</summary>
        public bool EmitReady { get; init; }
        /// <summary>Persist cursor beside --db and catch up unread mail missed while closed/restarting (ignored when --since is given).</summary>
        public bool Resume { get; init; }
        /// <summary>Max age (seconds) of a saved resume cursor before reseeding at MAX(id) (default 24h; 0 = unlimited).</summary>
        public double ResumeWindowSeconds { get; init; } = DefaultResumeWindowSeconds;
        /// <summary>Max unread events emitted on the initial startup catch-up pass (default 5; 0 = unlimited).</summary>
        public int ResumeLimit { get; init; } = DefaultResumeLimit;
        /// <summary>Optional explicit path for the --resume cursor file (defaults to &lt;dbDir&gt;/tail-state/tail-&lt;dbHash8&gt;-&lt;agentKey&gt;.json).</summary>
        public string? CursorFilePath { get; init; }
    }

    internal static string ResolveCursorPath(string dbPath, string agent, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath!;
        var fullDb = Path.GetFullPath(dbPath);
        var dbDir = Path.GetDirectoryName(fullDb);
        var root = string.IsNullOrEmpty(dbDir) ? Path.GetTempPath() : dbDir;
        var dbKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? fullDb.ToLowerInvariant() : fullDb)))[..8].ToLowerInvariant();
        return Path.Combine(root, "tail-state", $"tail-{dbKey}-{HookCheck.AgentFileKey(agent)}.json");
    }

    internal static TailCursorState? LoadCursorState(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TailCursorState>(HookCheck.ReadAllTextShared(path));
        }
        catch
        {
            return null;
        }
    }

    internal static void SaveCursorMonotonic(string path, long msgSeq, long turnSeq, int lockTimeoutMs = 500)
    {
        try
        {
            // Serialize read-max-write across concurrent tails sharing one resume file;
            // on timeout, skip this save so the next save or clean-exit refresh catches up.
            using var fileLock = HookCheck.AcquireStateLock(path, lockTimeoutMs);
            if (fileLock is null) return;

            var existing = LoadCursorState(path);
            var nextMsg = existing is not null ? Math.Max(existing.LastMsgId, msgSeq) : msgSeq;
            var nextTurn = existing is not null ? Math.Max(existing.LastTurnId, turnSeq) : turnSeq;
            HookCheck.WriteJsonAtomic(path, new TailCursorState
            {
                LastMsgId = nextMsg,
                LastTurnId = nextTurn,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            });
        }
        catch { }
    }

    public static async Task<int> RunAsync(
        Options options, TextWriter? stdout = null, CancellationToken token = default)
    {
        var agent = options.Agent.Trim();
        if (agent.Length == 0)
        {
            Console.Error.WriteLine("tail: --agent is required (or set AGORA_AGENT).");
            return 2;
        }

        if (stdout is null)
        {
            try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
            catch (IOException) { }
        }
        var output = stdout ?? Console.Out;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var linkedToken = linkedCts.Token;

        // Parent lifecycle guard (Finding #4): never leave orphaned tail processes
        // behind if Claude Desktop, Antigravity sidecar, or OpenCode crashes.
        Process? watchedParent = null;
        var targetParentPid = options.ParentPid
            ?? (options.ExitWithParent ? TryGetOwnParentPid() : null);

        if (targetParentPid is > 0)
        {
            if (!TryAttachParentWatcher(targetParentPid.Value, linkedCts, out watchedParent))
                return 0; // Parent is already gone.
        }

        if (options.ExitOnStdinEof)
        {
            _ = WatchStdinEofAsync(linkedCts);
        }

        var dbPath = Path.GetFullPath(options.DbPath);
        var listenerId = $"tail-{HookCheck.SanitizeFileSegment(agent)}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        EventWaitHandle? ownEvent = null;

        // Register BEFORE sampling the initial cursors so a write landing
        // right after ReadMaxSeq cannot slip between snapshot and wait.
        try
        {
            ownEvent = NamedBoardEvent.OpenOwnEvent(dbPath, listenerId);
            if (ownEvent is not null)
                NamedBoardEvent.Register(dbPath, listenerId);
        }
        catch (Exception)
        {
            ownEvent?.Dispose();
            ownEvent = null;
        }

        var cursorPath = options.Resume ? ResolveCursorPath(dbPath, agent, options.CursorFilePath) : null;
        long cursor = 0;
        long turnCursor = 0;
        var hasSampledCursor = false;
        var pipeBroken = false;

        try
        {
            var resumedFromSaved = false;
            if (options.Since is null && options.SinceTurn is null && cursorPath is not null)
            {
                var saved = LoadCursorState(cursorPath);
                var fresh = false;
                if (saved is not null)
                {
                    if (options.ResumeWindowSeconds <= 0)
                    {
                        fresh = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(saved.UpdatedAt) &&
                             DateTimeOffset.TryParse(saved.UpdatedAt, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var savedAt))
                    {
                        fresh = (DateTimeOffset.UtcNow - savedAt).TotalSeconds <= options.ResumeWindowSeconds;
                    }
                }

                if (fresh && saved is not null)
                {
                    cursor = saved.LastMsgId;
                    turnCursor = saved.LastTurnId;
                    resumedFromSaved = true;
                    NotifyLog.Write("tail", agent, $"started (resumed from {cursor}/{turnCursor})", cursor, turnCursor, dbPath: dbPath);
                }
                else
                {
                    cursor = NotifyHub.ReadMaxSeq(dbPath);
                    turnCursor = NotifyHub.ReadMaxTurnSeq(dbPath);
                    SaveCursorMonotonic(cursorPath, cursor, turnCursor);
                    var reason = saved is not null ? "started (resume cursor stale; seeded at MAX)" : "started (seeded cursor at MAX)";
                    NotifyLog.Write("tail", agent, reason, cursor, turnCursor, dbPath: dbPath);
                }
            }
            else
            {
                cursor = options.Since ?? NotifyHub.ReadMaxSeq(dbPath);
                turnCursor = options.SinceTurn
                    ?? (options.Since == 0 ? 0 : NotifyHub.ReadMaxTurnSeq(dbPath));
                NotifyLog.Write("tail", agent, "started", cursor, turnCursor, dbPath: dbPath);
            }
            hasSampledCursor = true;

            if (options.EmitReady)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "ready",
                    seq = cursor,
                    turn_seq = turnCursor,
                }));
                output.Flush();
            }

            var deadline = options.TimeoutSeconds is > 0
                ? DateTime.UtcNow.AddSeconds(options.TimeoutSeconds.Value)
                : (DateTime?)null;
            var fallback = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 0.1, 60));

            var checkStdoutPipe = stdout is null && OperatingSystem.IsWindows() && Console.IsOutputRedirected;

            var firstPass = true;
            while (!linkedToken.IsCancellationRequested)
            {
                var isStartupPass = firstPass;
                if (!firstPass)
                {
                    if (watchedParent is not null && SafeHasExited(watchedParent))
                        return 0;
                    if (checkStdoutPipe && IsWindowsStdoutPipeBroken())
                    {
                        pipeBroken = true;
                        return 0;
                    }

                    var wait = fallback;
                    if (deadline is { } dl)
                    {
                        var remaining = dl - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) return 0;
                        if (remaining < wait) wait = remaining;
                    }
                    try
                    {
                        await ChannelBridge.WaitForWakeAsync(ownEvent, wait, linkedToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
                firstPass = false;

                if (watchedParent is not null && SafeHasExited(watchedParent))
                    return 0;
                if (checkStdoutPipe && IsWindowsStdoutPipeBroken())
                {
                    pipeBroken = true;
                    return 0;
                }

                try
                {
                    var (events, maxSeq, maxTurnSeq) = NotifyHub.ReadCombinedSince(
                        dbPath,
                        cursor,
                        turnCursor,
                        agent,
                        directedOnly: !options.IncludeAll,
                        dmsOnly: options.DmsOnly,
                        excludeSelf: true);

                    if (resumedFromSaved && isStartupPass && options.ResumeLimit > 0 && events.Count > options.ResumeLimit)
                    {
                        var dropped = events.Count - options.ResumeLimit;
                        events = events.Skip(dropped).ToList();
                        events[0] = events[0] with { OmittedOlder = dropped };
                        NotifyLog.Write("tail", agent, $"resume catch-up capped to newest {options.ResumeLimit} ({dropped} older left in pending queries)", cursor, turnCursor, dbPath: dbPath);
                    }

                    if (events.Count > 0)
                    {
                        foreach (var ev in events)
                        {
                            output.WriteLine(JsonSerializer.Serialize(ev));
                            output.Flush();
                            if (checkStdoutPipe && IsWindowsStdoutPipeBroken())
                            {
                                pipeBroken = true;
                                return 0;
                            }
                            if (ev.MsgId > cursor) cursor = ev.MsgId;
                            else if (ev.Seq > cursor) cursor = ev.Seq;
                            if (ev.TurnSeq is long ts && ts > turnCursor) turnCursor = ts;
                            if (cursorPath is not null)
                                SaveCursorMonotonic(cursorPath, cursor, turnCursor);
                        }
                    }

                    var advanced = false;
                    if (maxSeq > cursor) { cursor = maxSeq; advanced = true; }
                    if (maxTurnSeq > turnCursor) { turnCursor = maxTurnSeq; advanced = true; }
                    if (advanced && cursorPath is not null)
                        SaveCursorMonotonic(cursorPath, cursor, turnCursor);

                    if (events.Count > 0 && options.Once) return 0;
                }
                catch (IOException)
                {
                    // Downstream stdout pipe closed -> parent is gone.
                    pipeBroken = true;
                    return 0;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"tail: read error ({e.Message}) — retrying.");
                }

                if (deadline is { } dlAfter && DateTime.UtcNow >= dlAfter)
                    return 0;
            }
            return 0;
        }
        finally
        {
            // Refresh updated_at on clean exit so the resume window counts from session close.
            if (cursorPath is not null && hasSampledCursor && !pipeBroken)
                SaveCursorMonotonic(cursorPath, cursor, turnCursor);
            if (ownEvent is not null)
            {
                NamedBoardEvent.Unregister(dbPath, listenerId);
                ownEvent.Dispose();
            }
            watchedParent?.Dispose();
        }
    }

    internal static bool TryAttachParentWatcher(
        int parentPid, CancellationTokenSource cts, out Process? parent)
    {
        parent = null;
        try
        {
            var proc = Process.GetProcessById(parentPid);
            if (SafeHasExited(proc))
            {
                proc.Dispose();
                return false;
            }

            // Guard against PID reuse: parent must have started before us.
            try
            {
                using var self = Process.GetCurrentProcess();
                if (proc.StartTime > self.StartTime)
                {
                    proc.Dispose();
                    return false;
                }
            }
            catch
            {
                // Access denied reading StartTime on restricted parent; still monitor exit.
            }

            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    try { cts.Cancel(); } catch { }
                };
            }
            catch
            {
                // Fall back to SafeHasExited check on each loop wake.
            }

            if (SafeHasExited(proc))
            {
                proc.Dispose();
                return false;
            }

            parent = proc;
            return true;
        }
        catch (ArgumentException)
        {
            // Parent PID does not exist anymore.
            return false;
        }
        catch
        {
            // Cannot open parent handle; continue without parent watcher rather than crashing.
            return true;
        }
    }

    internal static bool SafeHasExited(Process proc)
    {
        try { return proc.HasExited; }
        catch { return false; }
    }

    internal static async Task WatchStdinEofAsync(CancellationTokenSource cts)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buf = new byte[256];
            while (!cts.IsCancellationRequested)
            {
                var n = await stdin.ReadAsync(buf, cts.Token);
                if (n <= 0)
                {
                    try { cts.Cancel(); } catch { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            try { cts.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// Best-effort parent PID discovery using documented Win32 Toolhelp snapshot
    /// on Windows (or `/proc/self/stat` on Linux). Returns null if unavailable.
    /// </summary>
    internal static int? TryGetOwnParentPid()
    {
        if (OperatingSystem.IsWindows())
            return TryGetWindowsParentPid(Environment.ProcessId);

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText("/proc/self/stat");
                var closeParen = stat.LastIndexOf(')');
                if (closeParen > 0)
                {
                    var parts = stat[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var ppid) && ppid > 0)
                        return ppid;
                }
            }
            catch { }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var ppid = getppid();
                if (ppid > 0) return ppid;
            }
            catch { }
        }
        return null;
    }

    [DllImport("libc")]
    private static extern int getppid();

    private static int? TryGetWindowsParentPid(int pid)
    {
        const uint th32csSnapProcess = 0x00000002;
        var snap = CreateToolhelp32Snapshot(th32csSnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return null;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry))
                return null;

            do
            {
                if (entry.th32ProcessID == (uint)pid)
                    return entry.th32ParentProcessID > 0 ? (int)entry.th32ParentProcessID : null;
            } while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>
    /// Detects a closed stdout pipe on Windows (L12). .NET's ConsoleStream
    /// intentionally swallows ERROR_BROKEN_PIPE (109) / ERROR_NO_DATA (232) on
    /// Console.Out writes, so a zero-byte WriteFile probe against STD_OUTPUT_HANDLE
    /// lets tail exit cleanly as soon as the reader closes its end of the pipe.
    /// </summary>
    internal static bool IsWindowsStdoutPipeBroken()
    {
        if (!OperatingSystem.IsWindows()) return false;
        const int stdOutputHandle = -11;
        const uint fileTypePipe = 0x0003;
        const int errorBrokenPipe = 109;
        const int errorNoData = 232;

        var h = GetStdHandle(stdOutputHandle);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
        if (GetFileType(h) != fileTypePipe) return false;

        byte dummy = 0;
        if (WriteFile(h, ref dummy, 0, out _, IntPtr.Zero))
            return false;

        var err = Marshal.GetLastWin32Error();
        return err is errorBrokenPipe or errorNoData;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr hFile, ref byte lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
