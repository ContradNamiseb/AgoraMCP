using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using AgoraMCP.Database;

namespace AgoraMCP.McpServer.Watch;

/// <summary>One directed-mail entry in the AGORA_DIRECTED envelope. JSON with explicit lowercase names, not delimiter-joined pairs: agent names are free-form and may contain ',' or ':' (thread #21 review #139, finding 2).</summary>
public sealed record DirectedMailEntry(
    [property: JsonPropertyName("thread")] long Thread,
    [property: JsonPropertyName("by")] string By);

/// <summary>What one wake-cycle of `agora watch` decided and did.</summary>
public sealed record WatchCycleResult
{
    public bool BaselineSeeded { get; init; }
    public IReadOnlyList<ThreadDelta> Deltas { get; init; } = [];
    /// <summary>Null = no spawn this cycle. "inert" = no exec configured; "busy" = a turn is already running; "backoff" and "failed-out ..." = stage-4 bounded execution.</summary>
    public string? SpawnNotRun { get; init; }
    public int? SpawnedPid { get; init; }
    /// <summary>Consecutive failed attempts on the current signature (-1 = no ledger in use).</summary>
    public int FailureCount { get; init; } = -1;
}

/// <summary>
/// The resident half of plan v1: sleep on THIS agent's auto-reset event (60s
/// coarse timeout as the backstop), and on each wake decide whether any peer
/// authored anything new — only a peer delta spawns the configured exec turn,
/// fire-and-forget, never two at once (the previous turn's process suppresses
/// the next spawn, which is also the burst coalescing: deltas that arrive mid-
/// turn merge into the next wake, and the child's exit self-signals the event
/// so the rediff happens immediately instead of after the coarse timeout).
/// The state file belongs to the spawned turn; the watch only reads it — so a
/// turn that dies before responding is retried on the next wake instead of
/// being lost. No exec configured = inert mode (log what would have spawned),
/// which is how v1 ships.
/// </summary>
public static class WatchRunner
{
    public sealed record Options
    {
        public required string DbPath { get; init; }
        public required string Agent { get; init; }
        /// <summary>Command line to spawn on a peer delta. Empty/null = inert mode.</summary>
        public string? Exec { get; init; }
        public string? WorkingDirectory { get; init; }
        public string StatePath { get; init; } = "";
        public string HeartbeatPath { get; init; } = "";
        public string LogDirectory { get; init; } = "";
        public double CoarseWaitSeconds { get; init; } = 60;

        // BOUNDED EXECUTION (thread #23 stage 4): a hung child must not block
        // spawns forever, and a repeatedly-failing turn must not hammer.
        public double TurnTimeoutSeconds { get; init; } = 900;
        public int MaxTurnAttempts { get; init; } = 3;
        public double RetryBackoffSeconds { get; init; } = 60;
    }

    /// <summary>
    /// Per-watcher bounded-execution ledger (stage 4). Deliberately watcher-
    /// MEMORY, not the state file: the file belongs to the spawned turn and
    /// the watch only reads it — mixing retry bookkeeping into it would break
    /// that contract. Consequence, stated plainly: a watcher restart resets
    /// the failure budget and the work re-attempts — at-least-once preserved,
    /// and no durability is claimed for the failure counter. The durable
    /// states (cursors, receipts, handoffs) live in SQLite; the BOARD-visible
    /// failure signature is stage 1's delivery view (observed-not-acked).
    /// </summary>
    public sealed class TurnLedger
    {
        public Process? InFlight { get; internal set; }
        public DateTime SpawnedAtUtc { get; internal set; }
        /// <summary>The delta signature the in-flight (or just-finished) turn was spawned for.</summary>
        public string Signature { get; internal set; } = "";
        /// <summary>A turn was spawned and has not yet had its outcome evaluated.</summary>
        public bool AwaitingOutcome { get; internal set; }
        public int ConsecutiveFailures { get; internal set; }
        public DateTime? NextAttemptAllowedUtc { get; internal set; }
    }

    public static string DefaultStatePath(string agent) =>
        Path.Combine(Environment.CurrentDirectory, "local", $"last-seen-{agent}.json");

    public static void TouchHeartbeat(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
    }

    public static bool HeartbeatIsStale(string path, double staleMinutes)
    {
        if (!File.Exists(path)) return true;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(staleMinutes);
    }

    /// <summary>Single-instance guard: a duplicate watch (e.g. from the dead-man task) exits harmlessly.</summary>
    internal static Mutex? TryAcquireSingleton(string agent)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unixMutex = new Mutex(true, $"agoramcp-watch-{agent}", out var gotUnix);
            return gotUnix ? unixMutex : null;
        }
        var mutex = new Mutex(true, $@"Local\agoramcp-watch-{agent}", out var acquired);
        if (!acquired) { mutex.Dispose(); return null; }
        return mutex;
    }

    /// <summary>The resident loop. Ctrl+C or process kill stops it; the dead-man task relaunches when the heartbeat goes stale.</summary>
    public static int Run(Options options)
    {
        var singleton = TryAcquireSingleton(options.Agent);
        if (singleton is null)
        {
            Console.Error.WriteLine($"watch: another instance for agent '{options.Agent}' is already running — exiting.");
            return 0;
        }
        using var _ = singleton;

        Log(options, $"watch: resident for agent '{options.Agent}' on {options.DbPath} (exec: {(options.Exec is { Length: > 0 } ? "configured" : "INERT - none configured")})");

        // This watcher owns ONE auto-reset event that only the broadcast
        // touches — sole-consumer resets cannot race, which is the fix for the
        // fan-out blocker (shared manual-reset + per-watcher Reset could starve
        // a second watcher).
        using var ownEvent = NamedBoardEvent.OpenOwnEvent(options.DbPath, options.Agent);

        // Ordering is load-bearing (codex-sol, thread #5 item 1): SEED first,
        // THEN register, THEN run one immediate catch-up cycle before blocking.
        // The old order (register before seed) meant a peer post landing
        // between registration and seeding was signaled AND absorbed into the
        // baseline — consumed on the first WaitOne, invisible to the diff,
        // gone. Seed-first closes that window: anything after the seed is
        // above the cursor, and anything before the first WaitOne finds the
        // event pre-signaled.
        SeedBaseline(options);
        RegisterEvent(options);

        var turn = (Process?)null;
        var ledger = new TurnLedger();

        // Immediate catch-up cycle before the first blocking wait: catches
        // posts that landed between seeding/registration and now.
        var cycle = RunOnce(options, ref turn, ownEvent, ledger);
        Log(options, $"watch: startup -> {Describe(cycle)}");

        while (true)
        {
            var signaled = ownEvent?.WaitOne(TimeSpan.FromSeconds(options.CoarseWaitSeconds)) ?? false;
            // Auto-reset + sole consumer: the reset already happened inside
            // WaitOne; a post landing mid-cycle is picked up on the next wake
            // (registry re-registration keeps us discoverable to writers).
            RegisterEvent(options);
            cycle = RunOnce(options, ref turn, ownEvent, ledger);
            Log(options, $"watch: {(signaled ? "signaled" : "coarse-timeout")} -> {Describe(cycle)}");
        }
    }

    /// <summary>Seed the baseline if the state file is fresh/absent/corrupt/legacy (no spawn on arrival). Internal: the seed→register adversarial test replays the window through this exact step.</summary>
    internal static void SeedBaseline(Options options)
    {
        var state = WatchState.Load(options.StatePath, out var freshBaseline);
        if (!freshBaseline) return;
        using var seedDb = AgoraDatabase.Open(options.DbPath);
        BoardDiff.SeedFromBoard(seedDb, options.Agent, state);
        state.Save(options.StatePath);
    }

    internal static void RegisterEvent(Options options) =>
        NamedBoardEvent.Register(options.DbPath, options.Agent);

    /// <summary>One wake-cycle: heartbeat, diff, decide, (maybe) spawn. Public for the test matrix. An exited turn is disposed at the top, so a finished child never suppresses the next spawn and never leaks its handle. With a <paramref name="ledger"/>, the turn is bounded: hung children are killed at TurnTimeoutSeconds, failures back off exponentially, and after MaxTurnAttempts the signature is given up on until new mail arrives (stage 4).</summary>
    public static WatchCycleResult RunOnce(Options options, ref Process? turnInFlight,
        EventWaitHandle? selfWake = null, TurnLedger? ledger = null)
    {
        // STAGE 4 TIMEOUT: a hung child must not block spawns forever. Kill the
        // whole tree (cmd /c shelling wraps the real work) and count the loss.
        if (ledger is not null && ledger.InFlight is { HasExited: false } alive
            && DateTime.UtcNow - ledger.SpawnedAtUtc > TimeSpan.FromSeconds(options.TurnTimeoutSeconds))
        {
            var pid = alive.Id;
            try
            {
                alive.Kill(entireProcessTree: true);
                alive.WaitForExit(5000); // reap the tree before the next spawn
            }
            catch (InvalidOperationException) { /* exited between the check and the kill */ }
            turnInFlight?.Dispose();
            turnInFlight = null;
            ledger.InFlight = null;
            ledger.AwaitingOutcome = false; // the kill IS the counted outcome — no second count
            ledger.ConsecutiveFailures++;
            ledger.NextAttemptAllowedUtc = DateTime.UtcNow + BackoffFor(options, ledger.ConsecutiveFailures);
            Log(options, $"watch: turn pid={pid} exceeded {options.TurnTimeoutSeconds}s — killed the process tree; " +
                $"failure #{ledger.ConsecutiveFailures}, retry after backoff.");
        }

        // A disposed Process wrapper must be treated as an exited turn, not
        // crash the loop: HasExited throws InvalidOperationException on a
        // disposed instance. The ledger's alias is cleared too — it must never
        // outlive the wrapper it points at.
        if (turnInFlight is not null)
        {
            var dead = false;
            try { dead = turnInFlight.HasExited; }
            catch (InvalidOperationException) { dead = true; }
            if (dead)
            {
                turnInFlight.Dispose();
                turnInFlight = null;
                if (ledger is not null) ledger.InFlight = null;
            }
        }

        TouchHeartbeat(options.HeartbeatPath);

        using var db = AgoraDatabase.Open(options.DbPath);
        var state = WatchState.Load(options.StatePath, out var freshBaseline);
        if (freshBaseline)
        {
            // Re-derive from the board once: a first run or a corrupt file
            // seeds cursors as "everything current" — it must NOT spawn on
            // what already existed when it arrived.
            BoardDiff.SeedFromBoard(db, options.Agent, state);
            state.Save(options.StatePath);
            return Result(ledger, baselineSeeded: true);
        }

        var deltas = BoardDiff.Compute(db, options.Agent, state);

        // STAGE 4 OUTCOME: a finished turn is judged against the signature it
        // was spawned for. Unchanged signature = the mail was not cleared
        // (crash, reject, no-op) — count it and back off. Changed or cleared
        // signature = progress or new work — the failure budget resets, so old
        // failures never poison a fresh attempt.
        if (ledger is not null && ledger.AwaitingOutcome && turnInFlight is null)
        {
            var sig = Signature(deltas);
            if (deltas.Count > 0 && sig == ledger.Signature)
            {
                ledger.ConsecutiveFailures++;
                ledger.NextAttemptAllowedUtc = DateTime.UtcNow + BackoffFor(options, ledger.ConsecutiveFailures);
                Log(options, $"watch: the turn did not clear its mail (signature unchanged) — " +
                    $"failure #{ledger.ConsecutiveFailures}, next attempt not before " +
                    $"{ledger.NextAttemptAllowedUtc:HH:mm:ss}UTC.");
            }
            else
            {
                if (ledger.ConsecutiveFailures > 0)
                    Log(options, "watch: mail cleared or new mail arrived — failure budget reset.");
                ledger.ConsecutiveFailures = 0;
                ledger.NextAttemptAllowedUtc = null;
            }
            ledger.AwaitingOutcome = false;
        }

        if (deltas.Count == 0)
            return Result(ledger, deltas: deltas);

        if (string.IsNullOrWhiteSpace(options.Exec))
            return Result(ledger, deltas: deltas, spawnNotRun: "inert");

        if (turnInFlight is { HasExited: false })
            return Result(ledger, deltas: deltas, spawnNotRun: "busy");

        // STAGE 4 BOUNDS: give up on a signature after MaxTurnAttempts
        // consecutive failures — but NEW MAIL resets the budget even here (a
        // failed-out state with no turn in flight never re-evaluates the
        // budget otherwise, so fresh work would be stuck behind old failures
        // forever). Changed signature = new work = fresh budget.
        if (ledger is not null)
        {
            if (ledger.ConsecutiveFailures >= options.MaxTurnAttempts)
            {
                if (Signature(deltas) != ledger.Signature)
                {
                    Log(options, "watch: new mail changed the signature — failure budget reset.");
                    ledger.ConsecutiveFailures = 0;
                    ledger.NextAttemptAllowedUtc = null;
                }
                else
                {
                    return Result(ledger, deltas: deltas,
                        spawnNotRun: $"failed-out after {ledger.ConsecutiveFailures} attempts — giving up until new mail arrives");
                }
            }
            if (ledger.NextAttemptAllowedUtc is { } notBefore && DateTime.UtcNow < notBefore)
                return Result(ledger, deltas: deltas,
                    spawnNotRun: $"backoff (next attempt not before {notBefore:HH:mm:ss}UTC)");
        }

        turnInFlight = Spawn(options, deltas, selfWake);
        if (ledger is not null && turnInFlight is not null)
        {
            ledger.InFlight = turnInFlight;
            ledger.SpawnedAtUtc = DateTime.UtcNow;
            ledger.Signature = Signature(deltas);
            ledger.AwaitingOutcome = true;
            Log(options, $"watch: attempt #{ledger.ConsecutiveFailures + 1} for signature {ledger.Signature}");
        }
        return Result(ledger, deltas: deltas, spawnedPid: turnInFlight?.Id);
    }

    /// <summary>The delta signature a turn is spawned for and judged by: per-thread latest peer message id.</summary>
    internal static string Signature(IReadOnlyList<ThreadDelta> deltas) =>
        string.Join(";", deltas.Select(d => $"{d.ThreadId}:{(d.PeerMessageIds.Count > 0 ? d.PeerMessageIds[^1] : 0)}"));

    /// <summary>Exponential backoff, base RetryBackoffSeconds, capped at one hour.</summary>
    private static TimeSpan BackoffFor(Options options, int failures) =>
        TimeSpan.FromSeconds(Math.Min(options.RetryBackoffSeconds * Math.Pow(2, failures - 1), 3600));

    private static WatchCycleResult Result(TurnLedger? ledger, bool baselineSeeded = false,
        IReadOnlyList<ThreadDelta>? deltas = null, string? spawnNotRun = null, int? spawnedPid = null) =>
        new()
        {
            BaselineSeeded = baselineSeeded,
            Deltas = deltas ?? [],
            SpawnNotRun = spawnNotRun,
            SpawnedPid = spawnedPid,
            FailureCount = ledger?.ConsecutiveFailures ?? -1,
        };

    private static Process? Spawn(Options options, IReadOnlyList<ThreadDelta> deltas, EventWaitHandle? selfWake)
    {
        var summary = string.Join("; ", deltas.Select(d => d.ToString()));
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {options.Exec}",
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Environment.CurrentDirectory
                : options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ACK CONTRACT (codex-sol, thread #5 item 3): the spawned turn is told
        // exactly who it is, where the shared state file lives, and which db —
        // so it can advance its own cursors via `agora ack` instead of the
        // watch guessing on its behalf.
        start.EnvironmentVariables["AGORA_AGENT"] = options.Agent;
        start.EnvironmentVariables["AGORA_DB"] = options.DbPath;
        start.EnvironmentVariables["AGORA_STATE"] = options.StatePath;

        // DIRECTED-MAIL SIGNAL (thread #21): when any delta in this cycle is a
        // thread addressed to this agent, the turn is told which threads and
        // by whom, as COMPACT JSON — [{"thread":21,"by":"peer-a"}] — because
        // agent names are free-form and comma/colon delimiters would break on
        // a creator named "a,b:c" (thread #21 review #139, finding 2). None
        // directed = empty string. Observed child behavior: with the empty
        // value, a cmd child's `if defined` reports the variable UNDEFINED —
        // the contract's empty/unset semantics, asserted exactly by the tests
        // (thread #21 reviews #151 and #162; observed behavior, not a claim
        // about Windows environment blocks generally).
        var entries = deltas.Where(d => d.DirectedToMe)
                            .Select(d => new DirectedMailEntry(d.ThreadId, d.DirectedBy!))
                            .ToList();
        start.EnvironmentVariables["AGORA_DIRECTED"] =
            entries.Count == 0 ? "" : JsonSerializer.Serialize(entries);

        var process = Process.Start(start);
        if (process is null) return null;

        // The turn's human-readable input (thread #21 review #124, finding 3):
        // one line per delta, directed mail reading "DIRECTED TO YOU by X" —
        // the envelope reaches the turn itself, not just its environment.
        // Tiny enough to always fit the pipe buffer; a child that neither
        // reads nor keeps stdin open must not break the spawn.
        try
        {
            process.StandardInput.WriteLine($"agora watch turn for {options.Agent}: {summary}");
            process.StandardInput.Close();
        }
        catch (IOException) { /* child vanished before reading — the log has the summary */ }

        // The child's exit self-signals this watcher's own event: deltas that
        // arrived while the child ran are re-diffed IMMEDIATELY instead of
        // after the coarse timeout (codex-sol, thread #5 item 2).
        if (selfWake is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => selfWake.Set();
        }

        PumpToLog(process, options);
        Log(options, $"watch: spawned turn pid={process.Id} for: {summary}");
        return process;
    }

    /// <summary>
    /// Acknowledge: advance one thread's cursors to upToMsgId — the external,
    /// defined way for a spawned turn to clear its inbox (codex-sol, thread #5
    /// item 3). The acked message's author is looked up so last_peer_msg_id
    /// moves only for peer-authored messages; the thread's current status is
    /// carried so a later status change still flags.
    /// </summary>
    public static void Acknowledge(Options options, long threadId, long upToMsgId, bool initialize = false)
    {
        // ZERO-DISK-SIDE-EFFECT VALIDATION FIRST (thread #23 review #270,
        // github-copilot #269): the requested thread and message are validated
        // against the database BEFORE any state file is inspected, backed up,
        // seeded, or written. An invalid invocation (a typo'd --thread or
        // --up-to, even with --initialize) must fail with zero disk side
        // effects — seeding or backing up before validation marked all mail
        // current (and replaced forensic state) on acks that then failed.
        using var db = AgoraDatabase.Open(options.DbPath);

        var message = db.Prepare(
            "SELECT m.author, t.status FROM messages m JOIN threads t ON t.id = m.thread_id WHERE m.id = $id AND m.thread_id = $t",
            "$id", "$t")
            .SetAll(upToMsgId, threadId)
            .Query(r => (Author: r.GetString("author"), Status: r.GetString("status")))
            .FirstOrDefault();

        // FirstOrDefault yields a default (null-field) tuple when no row matched.
        if (message.Author is null)
            throw new AgoraException(
                $"ack: message {upToMsgId} does not exist on thread {threadId}.");

        // The state-file KIND is load-bearing here (codex-sol blocker, thread
        // #12; cold-start report claude-opus #259, resolution #264/#266). An
        // ABSENT file has nothing to protect but must not be seeded IMPLICITLY
        // (github-copilot: implicit seeding marks all existing mail current) —
        // so a plain ack refuses and names --initialize, which seeds the full
        // baseline EXPLICITLY. An UNUSABLE file (corrupt / legacy / id-less)
        // is refused even with --initialize unless it is first moved aside —
        // seeding over an unreadable file would silently discard unknown
        // cursor state; the backup keeps it for forensics.
        var state = WatchState.LoadWithKind(options.StatePath, out var kind);
        if (kind != WatchState.WatchFileKind.Current && !initialize)
            throw new AgoraException(
                "ack: the state file is " + (kind == WatchState.WatchFileKind.Absent
                    ? "absent"
                    : "corrupt, legacy, or otherwise unusable") +
                " — refusing to write a partial state that would replay old mail on the " +
                "next cycle. Run `agora watch` once to seed the full baseline, or re-run " +
                "this ack with --initialize to seed it explicitly" +
                (kind == WatchState.WatchFileKind.Absent ? "." : " (an unusable file is backed up first)."));

        if (kind == WatchState.WatchFileKind.Unusable)
        {
            // NON-DESTRUCTIVE forensic backup only (thread #23 review #283):
            // copy without overwrite to a collision-resistant name, but do
            // NOT delete the original and do NOT save anything yet — the
            // original file stays on disk (still the unusable one) until the
            // authoritative DB receipt has succeeded and the single final
            // projection save replaces it. A failure before that leaves the
            // original in place.
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            string backup;
            do { backup = $"{options.StatePath}.bak-{stamp}-{Path.GetRandomFileName()[..8]}"; }
            while (File.Exists(backup));
            File.Copy(options.StatePath, backup, overwrite: false);
            Log(options, $"ack: unusable state file backed up (copy) at {backup}; original remains until the ack commits.");
        }

        if (kind != WatchState.WatchFileKind.Current)
        {
            // EXPLICIT baseline creation (--initialize): the same full-board
            // seed a watch startup performs, held IN MEMORY (thread #23 review
            // #283/#284: persisting the seeded baseline before the DB receipt
            // was the dual-write violation — a receipt failure after that
            // save would leave the file all-current and the watcher would
            // never retry the missing receipt). Nothing touches disk here.
            BoardDiff.SeedFromBoard(db, options.Agent, state);
            Log(options, $"ack: state file was {kind}; seeded the full baseline in memory (--initialize).");
        }

        var key = threadId.ToString();
        var cur = state.Threads.TryGetValue(key, out var existing)
            ? existing
            : new ThreadCursor(0, 0, message.Status);

        state.Threads[key] = new ThreadCursor(
            Math.Max(cur.LastSeenMsgId, upToMsgId),
            message.Author != options.Agent
                ? Math.Max(cur.LastPeerMsgId, upToMsgId)
                : cur.LastPeerMsgId,
            message.Status);

        // ONE AUTHORITATIVE ACK, PROJECTION SECOND (thread #23 reviews #176,
        // #283/#284): the shared DB receipt is written FIRST — the record
        // other agents and the chat view read. Only after it succeeds does
        // the single projection save replace the file on disk (including the
        // unusable original — its backup copy holds the forensics). A failure
        // in the receipt throws with the original file untouched; a failure
        // in the final save leaves the watcher retrying, and the re-ack is
        // idempotent. Either failure fails the ack loudly.
        RecipientStore.Acknowledge(db, threadId, options.Agent, upToMsgId);
        state.Save(options.StatePath);
    }

    /// <summary>Child output goes to the per-day log; pumping keeps the pipes from filling and blocking the turn.</summary>
    private static void PumpToLog(Process process, Options options)
    {
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(options, $"turn> {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(options, $"turn! {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static string Describe(WatchCycleResult r)
    {
        if (r.BaselineSeeded) return "baseline seeded (no spawn on arrival)";
        if (r.Deltas.Count == 0) return "no peer delta";
        var delta = $"{r.Deltas.Count} thread(s) with peer deltas: {string.Join("; ", r.Deltas)}";
        return r.SpawnedPid is int pid ? $"{delta} -> spawned pid={pid}"
             : $"{delta} -> not spawned ({r.SpawnNotRun})";
    }

    private static void Log(Options options, string line)
    {
        var text = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {line}";
        Console.WriteLine(text);
        if (string.IsNullOrWhiteSpace(options.LogDirectory)) return;
        try
        {
            Directory.CreateDirectory(options.LogDirectory);
            File.AppendAllText(Path.Combine(options.LogDirectory,
                $"watch-{DateTime.UtcNow:yyyyMMdd}.log"), text + Environment.NewLine);
        }
        catch (IOException) { /* logging must never take the watch down */ }
    }
}
