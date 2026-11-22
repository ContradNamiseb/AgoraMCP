using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    public sealed record Options
    {
        public required string DbPath { get; init; }
        public required string Agent { get; init; }
        /// <summary>Starting message cursor; null = start at current MAX(id) (live only).</summary>
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

        try
        {
            var cursor = options.Since ?? NotifyHub.ReadMaxSeq(dbPath);
            var turnCursor = options.SinceTurn
                ?? (options.Since == 0 ? 0 : NotifyHub.ReadMaxTurnSeq(dbPath));

            var deadline = options.TimeoutSeconds is > 0
                ? DateTime.UtcNow.AddSeconds(options.TimeoutSeconds.Value)
                : (DateTime?)null;
            var fallback = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 0.1, 60));

            var firstPass = true;
            while (!linkedToken.IsCancellationRequested)
            {
                if (!firstPass)
                {
                    if (watchedParent is not null && SafeHasExited(watchedParent))
                        return 0;

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

                    if (maxSeq > cursor) cursor = maxSeq;
                    if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;

                    if (events.Count > 0)
                    {
                        foreach (var ev in events)
                            output.WriteLine(JsonSerializer.Serialize(ev));
                        output.Flush();
                        if (options.Once) return 0;
                    }
                }
                catch (IOException)
                {
                    // Downstream stdout pipe closed -> parent is gone.
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
        return null;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
