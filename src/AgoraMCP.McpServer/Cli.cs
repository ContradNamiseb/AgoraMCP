using AgoraMCP.Database;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer;

/// <summary>
/// Terminal verbs over the same store the MCP tools use — also the test and
/// debug path for everything that does not need a client. All commands accept
/// --db to point at the shared blackboard explicitly.
/// </summary>
internal static class Cli
{
    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var @out = stdout ?? Console.Out;
        var err = stderr ?? Console.Error;
        var dbFlag = Option(args, "--db");

        try
        {
            using var db = AgoraDatabase.Open(Config.ResolveDbPath(dbFlag));
            var rest = Rest(args);

            switch (args.FirstOrDefault())
            {
                case "ack":
                {
                    var agent = Option(args, "--agent");
                    var thread = long.TryParse(Option(args, "--thread"), out var t) ? t : 0;
                    var upTo = long.TryParse(Option(args, "--up-to"), out var u) ? u : 0;
                    if (string.IsNullOrWhiteSpace(agent) || thread == 0 || upTo == 0)
                    {
                        err.WriteLine("ack: --agent, --thread and --up-to are required "
                            + "(acknowledge that thread T is processed through message id UP-TO).");
                        return 2;
                    }
                    var statePath = Option(args, "--state")
                                    ?? WatchRunner.DefaultStatePath(agent.Trim());
                    var initialize = args.Contains("--initialize");
                    WatchRunner.Acknowledge(
                        new WatchRunner.Options
                        {
                            DbPath = Config.ResolveDbPath(dbFlag),
                            Agent = agent.Trim(),
                            StatePath = statePath,
                        }, thread, upTo, initialize);
                    @out.WriteLine($"ack: thread {thread} cursor advanced to message {upTo} for {agent.Trim()}." +
                        (initialize ? " (state file was seeded explicitly via --initialize)" : ""));
                    return 0;
                }

                case "help" or "--help" or "-h":
                    @out.WriteLine(HelpText);
                    return 0;

                case "list":
                {
                    var status = Option(args, "--status") ?? "";
                    var specialty = Option(args, "--specialty") ?? "";
                    var limit = int.TryParse(Option(args, "--limit"), out var n) ? n : 50;
                    @out.WriteLine(ToolResponses.RenderThreadLines(
                        ThreadStore.ListThreads(db, status, specialty, limit), "thread(s)"));
                    return 0;
                }

                case "read" when rest.Length > 0 && long.TryParse(rest[0], out var id):
                    @out.WriteLine(ToolResponses.RenderDetail(ThreadStore.ReadThread(db, id)));
                    return 0;

                case "search" when rest.Length > 0:
                {
                    var limit = int.TryParse(Option(args, "--limit"), out var n) ? n : 10;
                    @out.WriteLine(ToolResponses.RenderHits(ThreadStore.Search(db, string.Join(' ', rest), limit)));
                    return 0;
                }

                case "stats":
                    // A fresh cache instance so the budget line reflects this
                    // machine's configuration; a one-shot CLI holds no chat.
                    @out.WriteLine(ToolResponses.RenderStats(ThreadStore.GetStats(db, db.ConnectionString,
                        cache: new MessageCache(MessageCache.ResolveBudget()))));
                    return 0;

                case "online":
                    @out.WriteLine(ToolResponses.RenderRoster(PresenceStore.OnlineRoster(db)));
                    return 0;

                case "review":
                {
                    // Dual-mode: with --verdict it records an immutable event;
                    // without, it prints the thread's full review history and
                    // the derived current state (thread #23 stage 2).
                    var thread = long.TryParse(Option(args, "--thread"), out var rt) ? rt : 0;
                    if (thread == 0)
                    {
                        err.WriteLine("review: --thread is required.");
                        return 2;
                    }
                    var verdict = Option(args, "--verdict");
                    if (verdict is null)
                    {
                        var current = ReviewStore.Current(db, thread);
                        @out.WriteLine(current is null
                            ? $"review: thread {thread} has no review events."
                            : $"review: thread {thread} — {current}");
                        foreach (var ev in ReviewStore.ForThread(db, thread))
                            @out.WriteLine($"  #{ev.Id} [{ev.CreatedAt}] {ev.Actor}: {ev.Kind} "
                                + $"@ rev {ev.Revision}" + (ev.Note.Length > 0 ? $" — {ev.Note}" : ""));
                        return 0;
                    }
                    var actor = Option(args, "--reviewer") ?? "";
                    var revision = Option(args, "--revision") ?? "";
                    if (actor.Length == 0 || revision.Length == 0)
                    {
                        err.WriteLine("review: recording needs --reviewer and --revision " +
                            "(the snapshot identifier you actually inspected).");
                        return 2;
                    }
                    var snapshotFile = Option(args, "--snapshot-file");
                    var snapshot = snapshotFile is { Length: > 0 } ? File.ReadAllText(snapshotFile) : "";
                    var evnt = ReviewStore.Record(db, thread, actor.Trim(), verdict.Trim(),
                        revision.Trim(), snapshot, Option(args, "--note") ?? "");
                    @out.WriteLine($"review: thread {thread} is {evnt.Kind.ToUpperInvariant()} " +
                        $"by {evnt.Actor} @ rev {evnt.Revision}.");
                    return 0;
                }

                case "revise" when long.TryParse(Option(args, "--thread"), out var rvThread):
                {
                    var actor = Option(args, "--author") ?? "";
                    var revision = Option(args, "--revision") ?? "";
                    if (actor.Length == 0 || revision.Length == 0)
                    {
                        err.WriteLine("revise: --author and --revision are required " +
                            "(a new revision supersedes the current verdict; history keeps it).");
                        return 2;
                    }
                    var evnt = ReviewStore.Record(db, rvThread, actor.Trim(),
                        "revision_submitted", revision.Trim(), "", Option(args, "--note") ?? "");
                    @out.WriteLine($"revise: thread {rvThread} is now AWAITING REVIEW — " +
                        $"revision {evnt.Revision} submitted by {evnt.Actor}.");
                    return 0;
                }

                case "brief" when long.TryParse(Option(args, "--thread"), out var brThread):
                {
                    // Thread #23 stage 5: the compact context — verified state
                    // (review/handoff/delivery) plus the most recent messages,
                    // trust-demarcated. Same renderer the MCP briefing uses.
                    var agent = Option(args, "--agent") ?? "";
                    var since = long.TryParse(Option(args, "--since"), out var bs) ? bs : 0;
                    var limit = int.TryParse(Option(args, "--recent"), out var br) ? br : 3;
                    @out.WriteLine(ToolResponses.RenderBriefing(db, brThread, agent.Trim(),
                        since, Math.Max(limit, 0), cache: null,
                        useDigest: !args.Contains("--no-digest")));
                    return 0;
                }

                case "digest" when long.TryParse(Option(args, "--thread"), out var dgThread):
                {
                    var through = long.TryParse(Option(args, "--through"), out var dgThrough) ? dgThrough : 0;
                    var body = Body(args);
                    var author = CallerIdentity.Resolve(Option(args, "--author"));
                    if (body is null || through == 0)
                    {
                        err.WriteLine("digest: --thread, --through MSG_ID and a body are required " +
                            "(--body TEXT or --body-file PATH).");
                        return 2;
                    }
                    var saved = DigestStore.Save(db, dgThread, through, body, author);
                    @out.WriteLine($"digest #{saved.Digest.Id} saved on thread {dgThread} " +
                        $"by {saved.Digest.Author}, covering messages through #{saved.Digest.CoveredThroughMsgId} " +
                        $"(≈{saved.SavedTokens} estimated tokens saved per read).");
                    return 0;
                }

                case "note":
                {
                    var body = Body(args);
                    if (body is null)
                    {
                        err.WriteLine("note: a body is required (--body TEXT or --body-file PATH).");
                        return 2;
                    }
                    var who = CallerIdentity.Resolve(Option(args, "--agent"));
                    var slot = Option(args, "--slot") ?? AgentNoteStore.DefaultSlot;
                    var saved = AgentNoteStore.Save(db, who, body, slot);
                    @out.WriteLine($"note #{saved.Id} saved for {saved.Agent} (slot '{saved.Slot}').");
                    return 0;
                }

                case "resume":
                {
                    var who = CallerIdentity.Resolve(Option(args, "--agent"));
                    @out.WriteLine(ToolResponses.RenderResume(
                        AgentNoteStore.CurrentForAgent(db, who), who));
                    return 0;
                }

                case "memo":
                {
                    var question = Option(args, "--question") ?? "";
                    var answer = Body(args) ?? Option(args, "--answer") ?? "";
                    var thread = long.TryParse(Option(args, "--thread"), out var mTh) ? mTh : 0;
                    var msg = long.TryParse(Option(args, "--msg"), out var mMsg) ? mMsg : 0;
                    if (question.Length == 0 || answer.Length == 0)
                    {
                        err.WriteLine("memo: --question and an answer are required " +
                            "(--answer TEXT, or --body/--body-file for a long one).");
                        return 2;
                    }
                    var filed = AnswerCacheStore.File(db, question, answer,
                        CallerIdentity.Resolve(Option(args, "--author")),
                        thread > 0 ? thread : null, msg > 0 ? msg : null);
                    @out.WriteLine($"memo #{filed.Id} filed by {filed.FiledBy}.");
                    return 0;
                }

                case "ask" when Option(args, "--question") is { Length: > 0 } asked:
                {
                    var maxAge = int.TryParse(Option(args, "--max-age-days"), out var ma)
                        ? ma : AnswerCacheStore.DefaultMaxAgeDays;
                    var hit = AnswerCacheStore.Lookup(db, asked, maxAge);
                    if (hit is null)
                    {
                        var anyVersion = AnswerCacheStore.Lookup(db, asked, int.MaxValue, recordHit: false);
                        @out.WriteLine(anyVersion is null
                            ? "MEMO MISS — nothing filed for that question."
                            : $"MEMO MISS — filed {AnswerCacheStore.Age(anyVersion.CreatedAt).Days}d ago " +
                              $"(older than {maxAge}d); re-ask, then file a fresh one with memo.");
                        return 0;
                    }
                    @out.WriteLine(ToolResponses.RenderMemo(hit, asked));
                    return 0;
                }

                case "memos":
                {
                    var limit = int.TryParse(Option(args, "--limit"), out var ml) ? ml : 30;
                    var thread = long.TryParse(Option(args, "--thread"), out var mt) ? mt : 0;
                    var memos = AnswerCacheStore.List(db, Math.Clamp(limit, 1, 500),
                        thread > 0 ? thread : null);
                    @out.WriteLine(ToolResponses.RenderMemoList(memos, memos.Count));
                    return 0;
                }

                case "tokens":
                {
                    var days = int.TryParse(Option(args, "--days"), out var td) ? td : 7;
                    var only = Option(args, "--agent");
                    var usage = TokenStore.Report(db, Math.Clamp(days, 1, 3650), only);
                    var byThread = string.IsNullOrWhiteSpace(only)
                        ? TokenStore.ByThread(db, Math.Clamp(days, 1, 3650)) : [];
                    @out.WriteLine(ToolResponses.RenderTokenReport(usage, byThread, days));
                    return 0;
                }

                case "handoff":
                {
                    // Thread #23 stage 3: view a thread's handoffs; the claimant
                    // completes via --complete, the releaser supersedes via
                    // --supersede (named actors only — no TTL usurpation).
                    var thread = long.TryParse(Option(args, "--thread"), out var ht) ? ht : 0;
                    var complete = long.TryParse(Option(args, "--complete"), out var hc) ? hc : 0;
                    var supersede = long.TryParse(Option(args, "--supersede"), out var hs) ? hs : 0;
                    var actor = Option(args, "--actor") ?? "";
                    if (complete > 0 || supersede > 0)
                    {
                        if (actor.Length == 0)
                        {
                            err.WriteLine("handoff: --complete/--supersede need --actor (transitions are named; no impersonation).");
                            return 2;
                        }
                        var done = complete > 0
                            ? HandoffStore.Complete(db, complete, actor)
                            : HandoffStore.Supersede(db, supersede, actor);
                        @out.WriteLine($"handoff #{done.Id} is now {done.Status.ToUpperInvariant()} (by {actor}).");
                        return 0;
                    }
                    if (thread == 0)
                    {
                        err.WriteLine("handoff: --thread is required (or --complete/--supersede with --actor).");
                        return 2;
                    }
                    var list = HandoffStore.ForThread(db, thread);
                    @out.WriteLine(list.Count == 0
                        ? $"handoff: thread {thread} has no handoffs."
                        : $"handoffs on thread {thread}:");
                    foreach (var h in list)
                        @out.WriteLine($"  #{h.Id} {h}");
                    return 0;
                }

                case "pending" when Option(args, "--agent") is { Length: > 0 } pendingAgent:
                {
                    // Thread #23 stage 1: per-recipient delivery visibility —
                    // what is pending (unread), observed (read but unhandled),
                    // and handled (acked) for one agent.
                    var agent = pendingAgent.Trim();
                    var states = RecipientStore.DeliveryFor(db, agent);
                    @out.WriteLine($"delivery states for {agent}:");
                    foreach (var s in states)
                    {
                        var state = s.Handled ? $"handled (acked through {s.LastAckMsgId})"
                            : s.Observed ? $"observed through {s.LastSeenMsgId}, UNHANDLED"
                            : $"PENDING: {s.MaxMsgId - s.LastSeenMsgId} unread (newest {s.MaxMsgId})";
                        @out.WriteLine($"  #{s.ThreadId} [{s.Status}] "
                            + (s.DirectedTo.Length > 0 ? $"dir→{s.DirectedTo} " : "")
                            + $"{s.Topic} — {state}");
                    }
                    @out.WriteLine($"totals: {states.Count(s => s.Pending)} pending, "
                        + $"{states.Count(s => s.Observed)} observed-unhandled, "
                        + $"{states.Count(s => s.Handled)} handled.");
                    return 0;
                }

                case "turn":
                {
                    var thread = long.TryParse(Option(args, "--thread"), out var tt) ? tt : 0;
                    if (thread == 0)
                    {
                        err.WriteLine("turn: --thread is required.");
                        return 2;
                    }
                    var passTo = Option(args, "--pass-to");
                    if (passTo is null)
                    {
                        @out.WriteLine(ToolResponses.RenderTurn(db, thread));
                        return 0;
                    }
                    var from = Option(args, "--from") ?? "";
                    var instruction = Option(args, "--instruction") ?? "";
                    var fromRole = Option(args, "--from-role") ?? "";
                    var toRole = Option(args, "--to-role") ?? "";
                    var stage = Option(args, "--stage") ?? "";
                    var evnt = TurnStore.Pass(db, thread, from, passTo, instruction, fromRole, toRole, stage);
                    NamedBoardEvent.Broadcast(Config.ResolveDbPath(dbFlag));
                    var toRoleTag = evnt.ToRole.Length > 0 ? $" [{evnt.ToRole}]" : "";
                    var fromRoleTag = evnt.FromRole.Length > 0 ? $" [{evnt.FromRole}]" : "";
                    var stageTag = evnt.Stage.Length > 0 ? $" (stage: {evnt.Stage})" : "";
                    @out.WriteLine($"turn: thread {thread} passed to {evnt.ToAgent}{toRoleTag} by {evnt.FromAgent}{fromRoleTag}{stageTag} — instruction: {evnt.Instruction}");
                    return 0;
                }

                default:
                    err.WriteLine("usage: agora <command> [args] [--db PATH]");
                    err.WriteLine(HelpText);
                    return 2;
            }
        }
        catch (AgoraException e)
        {
            err.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    public const string HelpText = """
        agora — the shared blackboard between agents on this machine

        Commands:
          serve [--db PATH]                    MCP over stdio (launched by an agent client)
          watch --agent NAME [--exec CMD]      resident: wake on peer posts, spawn CMD once per burst
              [--db PATH] [--cwd DIR]          inert without --exec (logs would-be spawns)
              [--state P] [--heartbeat P]      bounded execution: --timeout SEC kills a hung
              [--log-dir DIR] [--interval SEC] turn tree; --max-attempts N + --backoff SEC bound
              [--timeout S] [--max-attempts N] retries of a failing turn (new mail resets the
              [--backoff SEC]                  budget); turn env AGORA_AGENT/DB/STATE +
                                               AGORA_DIRECTED (JSON); delta summary on stdin
          chat [--db PATH] [--agent NAME]      local live board view (terminal; Q quits);
              [--interval SEC]                 viewer: --agent, then $AGORA_AGENT, else
                                               neutral "DIRECTED TO name" labels
          ack --agent NAME --thread ID         acknowledge a thread processed through message
              --up-to MSG_ID [--state P]       id UP-TO (advances the watcher cursor);
              [--initialize]                   seeds an absent state file explicitly (an
                                               unusable file is backed up first)
          list [--status S] [--specialty S]    compact thread list, newest first
          read <id>                            one thread in full
          search <keywords...> [--limit N]     full-text search over topics and bodies
          online                               who has heartbeated within the TTL
          pending --agent NAME                 per-thread delivery states: pending / observed /
                                               handled (reads mark observed; only ack marks handled)
          review --thread ID                   review history + current state; with
              [--verdict V] [--reviewer N]     --verdict/--reviewer/--revision records an immutable
              [--revision R] [--note T]        verdict attached to that source snapshot
          revise --thread ID --author N        submit a new revision: supersedes the current
              --revision R [--note T]          verdict (state -> awaiting review; history keeps it)
          brief --thread ID [--agent N]        compact context: verified state (review/handoff/
              [--since ID] [--recent K]        delivery) + recent messages, trust-demarcated;
              [--no-digest]                    serves a saved digest in place of the history
                                               it covers (--no-digest forces the raw range)
          digest --thread ID --through MSG_ID  save a compact summary of a thread through a
              --body TEXT | --body-file PATH   message id, so peers read the summary instead
              [--author N]                     of the whole history (the token saving)
          note --body TEXT | --body-file PATH  save YOUR working state (doing/decided/open/
              [--agent N] [--slot S]           next) so a fresh session resumes cheaply
          resume [--agent N]                   load the newest note per slot (where was I)
          memo --question Q --answer A         file an answer for exact-match reuse
              [--author N] [--thread ID]       ([--msg ID] records its source)
          ask --question Q [--max-age-days N]  look a question up in the memo cache first
          memos [--limit N] [--thread ID]      what has been filed, by whom, reuse counts
          tokens [--agent N] [--days N]        where the tokens went: self-reported spend +
                                               measured savings, by agent and by thread
          handoff --thread ID                  durable work handoffs: payload, revision anchor,
              [--complete ID | --supersede ID] claim state; --complete/--supersede need --actor
              [--actor NAME]
          turn --thread ID                     view turn state and pass history; with
              [--pass-to AGENT] [--from AGENT] --pass-to/--from/--instruction transfers the turn
              [--instruction TEXT]             token; optional [--from-role R] [--to-role R]
              [--from-role R] [--to-role R]    [--stage S] tracks role-driven workflow
              [--stage S]
          stats                                what the blackboard holds
          hub [--db PATH] [--port N]         resident push fan-out: loopback SSE over the
                                             SQLite log (Ctrl+C stops; subscribers resume)
          subscribe [--agent N] [--hub H:P]  agent sidecar: print one event JSON per line
                    [--since ID]             until killed (reconnects from last seq)
          tail --agent NAME [--db PATH]      hub-less push stream: waits on NamedBoardEvent
               [--since ID] [--since-turn N] (0% CPU) and prints one JSON event per line for
               [--dms-only] [--all] [--once] incoming DMs, directed mail, and turn passes
               [--timeout S] [--interval S]  (starts at now unless --since is given);
               [--parent-pid PID]            --exit-with-parent / --parent-pid /
               [--exit-with-parent]          --exit-on-stdin-eof exit when host dies;
               [--exit-on-stdin-eof]         --emit-ready prints initial cursor handshake
               [--emit-ready]
          hook-check --agent NAME            lifecycle hook check for SessionStart,
               [--format FMT] [--event EV]   UserPromptSubmit, PostToolUse, and Stop across
               [--record-session]            Codex, Cursor, VS Code, Copilot, Kimi, ZCode,
               [--state P] [--max-blocks N]  Claude, and Antigravity (per-session cursor,
               [--dms-only] [--all]          fail-silent, ~200ms spawn on Windows)
          bridge --client codex|kimi-web|opencode
               [--agent NAME] [--session ID] hub-less idle-wake dispatcher waiting on
               [--session-file P] [--url U]  NamedBoardEvent (0% CPU) and injecting mail via
               [--session-max-age-sec S]     sanctioned loopback APIs (native codex.exe queue,
               [--token-file P] [--token T]  kimi web REST, opencode HTTP); --max-wakes /
               [--exe PATH] [--max-wakes N]  --max-global-wakes latch 60m cooldowns;
               [--max-global-wakes N]        --exit-with-parent / --parent-pid /
               [--parent-pid PID] [--once]   --exit-on-stdin-eof exit when host dies
               [--exit-with-parent] [--exit-on-stdin-eof] [--dms-only] [--all] [--dry-run] [--timeout S]
          doctor [--db PATH]                 inspect build, WAL/schema version, live event
                                             listeners, session beacons, hooks, and notify.log

        The database is shared by convention: --db flag, then $AGORA_DB, then
        %LOCALAPPDATA%\AgoraMCP\agora.db. Every agent that wants to share the
        blackboard must resolve the SAME file.
        """;

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    /// <summary>
    /// A body passed either inline (--body TEXT) or from a file (--body-file
    /// PATH). Digests and notes are long prose, and long inline arguments are
    /// hostile on Windows shells — the file path is the reliable route. NULL
    /// when neither was given; a missing FILE is a loud error, not an empty
    /// body, because silently saving "" would look like success.
    /// </summary>
    private static string? Body(string[] args)
    {
        var file = Option(args, "--body-file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
                throw new AgoraException($"--body-file: no such file '{file}'.");
            return File.ReadAllText(file).Trim();
        }
        var inline = Option(args, "--body");
        return string.IsNullOrWhiteSpace(inline) ? null : inline;
    }

    /// <summary>Positional arguments: everything that is not a flag or a flag's value.</summary>
    private static string[] Rest(string[] args)
    {
        var rest = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith('-')) { i++; continue; } // flag; skip its value too
            rest.Add(args[i]);
        }
        return [.. rest];
    }
}
