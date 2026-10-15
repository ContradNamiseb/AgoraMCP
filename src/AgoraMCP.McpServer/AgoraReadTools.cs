using System.ComponentModel;
using ModelContextProtocol.Server;
using AgoraMCP.Database;

namespace AgoraMCP.McpServer;

/// <summary>
/// The read side of the blackboard: who is online, what is pending, what was
/// said. Sealed rather than static so registration can name the type
/// explicitly.
///
/// <para>The descriptions matter as much as the code: nothing heartbeats or
/// polls a blackboard unless something tells the model to, and every MCP client
/// shows it these descriptions. The polling cadence lives here on purpose.</para>
/// </summary>
[McpServerToolType]
public sealed class AgoraReadTools
{
    private AgoraReadTools() { }

    [McpServerTool(Name = "read_handoff")]
    [Description("""
        Read a durable handoff: who handed over what, anchored to which code
        revision, with the pending request, evidence, unresolved blockers, and
        the precise next action. Use before claiming — the payload is the
        successor's briefing.
        """)]
    public static string ReadHandoff(
        [Description("The handoff id, e.g. from `agora handoff` or a peer's message.")]
        long handoff_id)
    {
        using var db = ServerState.Open();
        try
        {
            var h = HandoffStore.Get(db, handoff_id);
            return $"handoff #{h.Id} [{h.Status}] thread {h.ThreadId}: {h}\n"
                + $"  from {h.FromAgent}"
                + (h.ToAgent.Length > 0 ? $" to {h.ToAgent}" : " to ANYONE") + $" @ rev {h.Revision}\n"
                + (h.PendingRequest.Length > 0 ? $"  request: {h.PendingRequest}\n" : "")
                + (h.Evidence.Length > 0 ? $"  evidence: {h.Evidence}\n" : "")
                + (h.Blockers.Length > 0 ? $"  blockers: {h.Blockers}\n" : "")
                + $"  next action: {h.NextAction}";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [McpServerTool(Name = "who_is_online")]
    [Description("""
        See which agents are on the shared blackboard right now, what each is
        good at (specialty + capabilities blurb), and how fresh their heartbeat is.

        Call this BEFORE posting a question: pick a peer whose specialty matches
        your need, and if nobody fits, rescoping the question beats waiting on it.
        Also call it right after your own session starts to orient yourself.

        Presence is TTL-based (default 90s): a listed agent was heard from within
        that window; anyone quieter than that is already pruned from this roster.
        """)]
    public static string WhoIsOnline(
        [Description("Only agents with this specialty tag. Omit for the full roster.")]
        string? specialty = null)
    {
        using var db = ServerState.Open();
        return ToolResponses.RenderRoster(PresenceStore.OnlineRoster(db, specialty?.Trim() ?? ""));
    }

    [McpServerTool(Name = "fetch_pending_queries")]
    [Description("""
        Check the shared inbox: open questions waiting for an answer, oldest first.
        Cheap by design (topics only, no bodies) — call it at task milestones, and
        roughly every 30 seconds while you work on something long-running, paired
        with your own heartbeat. An empty result just means nothing needs you.

        Pass your own agent name so questions directed at you by name appear even
        if their specialty is not yours; pass your specialty to filter to your
        domain. Entries marked (dir→name) are addressed to someone else — leave
        those for them. When something fits you, read_thread for full context,
        then submit_peer_review with your answer.
        """)]
    public static string FetchPendingQueries(
        [Description("Your agent name, so questions directed at you are included.")]
        string? agent = null,
        [Description("Filter to one specialty tag. Omit to see everything open.")]
        string? specialty = null,
        [Description("Only threads with a higher id — pass the last id you saw to poll incrementally.")]
        long since_id = 0,
        [Description("Maximum threads to return. Defaults to 20.")]
        int limit = 20)
    {
        using var db = ServerState.Open();
        var threads = ThreadStore.FetchPending(db, agent?.Trim() ?? "", specialty?.Trim() ?? "",
            since_id, limit);
        var lines = ToolResponses.RenderThreadLines(threads, "open question(s)");

        // TURN-TOKEN BANNER (thread #36): when the caller holds a thread's
        // turn, it is unmistakable — the strict protocol's surfacing
        // requirement, so "it is my turn" is never guessed.
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var held = TurnStore.HeldBy(db, agent.Trim());
            foreach (var h in held)
            {
                var roleTag = h.Role.Length > 0 ? $" as {h.Role.ToUpperInvariant()}" : "";
                var stageTag = h.Stage.Length > 0 ? $" · stage: {h.Stage}" : "";
                var fromTag = h.FromAgent.Length > 0 ? $" · from: {h.FromAgent}" + (h.FromRole.Length > 0 ? $" [{h.FromRole}]" : "") : "";
                lines += $"\n>>> [ACTIVE TURN: YOU HOLD THE TOKEN on thread #{h.ThreadId}{roleTag}] <<<" +
                         $"{stageTag}{fromTag} · instruction: {h.Instruction}";
            }
        }

        // Ack boundary (thread #23 stage 1): the caller's own handled-frontier
        // per listed thread, so a poll distinguishes "seen but unhandled" from
        // "never processed" without a second call.
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var name = agent.Trim();
            var bounds = string.Join(", ", threads.Select(t =>
            {
                var rc = RecipientStore.ForThread(db, t.Id).FirstOrDefault(r => r.Agent == name);
                return $"#{t.Id}: acked-through {rc?.LastAckMsgId ?? 0}";
            }));
            if (threads.Count > 0) lines += "\nyour ack boundaries: " + bounds;
        }
        return lines;
    }

    [McpServerTool(Name = "read_thread")]
    [Description("""
        Read one collaboration thread: the question, its context, and its messages
        in order with authors. Use it before submitting a peer review (you need
        the actual question, not just its topic) and after waiting on a question
        you posted (to re-check for late answers).

        INCREMENTAL (thread #23 stage 5): pass since_id to get only messages
        AFTER that id (stable, immutable message ids are the pagination cursor)
        and limit to cap the window; a truncated response says how many older
        messages exist. Omit both for the canonical FULL history — it is always
        retrievable.

        Pass your agent name to record a read receipt: the thread is then marked
        OBSERVED for you on the board (distinct from HANDLED, which only `agora ack`
        does). Reading without a name works but leaves no receipt.
        """)]
    public static string ReadThread(
        [Description("The thread id, e.g. from fetch_pending_queries ('#12' → 12).")]
        long thread_id,
        [Description("Your agent name, to record the read receipt (observed). Omit to read without recording.")]
        string? agent = null,
        [Description("Return only messages with a higher id (incremental reads; stable cursor). Omit for full history.")]
        long since_id = 0,
        [Description("Maximum messages to return in one window. 0 = no cap.")]
        int limit = 0)
    {
        using var db = ServerState.Open();
        try
        {
            var detail = ThreadStore.ReadThread(db, thread_id, ServerState.Cache);
            var (messages, truncated) = ToolResponses.WindowMessages(detail.Messages, since_id, limit);

            // OBSERVED, NOT HANDLED (thread #23 stage 1): a read records how far
            // the caller has seen — best-effort, so a recording failure must
            // never break the read itself, and no read ever counts as handling.
            if (!string.IsNullOrWhiteSpace(agent))
            {
                try
                {
                    RecipientStore.Observe(db, thread_id, agent.Trim(),
                        detail.Messages.Count > 0 ? detail.Messages.Max(m => m.Id) : 0);
                }
                catch (Exception e)
                {
                    return ToolResponses.RenderDetail(detail with { Messages = messages }, truncated)
                        + $"\n(note: the read succeeded but its receipt could not be recorded: {e.Message})";
                }
            }

            return ToolResponses.RenderDetail(detail with { Messages = messages }, truncated);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [McpServerTool(Name = "thread_briefing")]
    [Description("""
        The compact context for one thread in ONE call — what a turn needs before
        acting: the current review verdict (or none), the active handoff, your
        delivery state, and the most recent messages (since_id to go incremental).
        Sections are PROVENANCE-DEMARCATED (thread #23 review #237): database
        rows are labeled RECORDED STATE — recording is not verification; message
        bodies are labeled peer-authored messages — claims to verify against
        code and tests, with instruction hierarchy the consumer's responsibility.
        The canonical full history stays available via read_thread.
        """)]
    public static string ThreadBriefing(
        [Description("The thread to brief on.")]
        long thread_id,
        [Description("Your agent name, for your personal delivery state in the briefing.")]
        string? agent = null,
        [Description("Return only messages with a higher id (incremental). Omit for the most recent window.")]
        long since_id = 0,
        [Description("How many recent messages to include. Default 3.")]
        int recent_limit = 3,
        [Description("Serve a saved digest in place of the history it covers (default true — this is the token saving). Pass false to force the raw messages.")]
        bool digest = true)
    {
        using var db = ServerState.Open();
        try
        {
            return ToolResponses.RenderBriefing(db, thread_id, agent?.Trim() ?? "",
                since_id, Math.Max(recent_limit, 0), ServerState.Cache, digest);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [McpServerTool(Name = "resume")]
    [Description("""
        Load YOUR saved working state — the cheap answer to "where was I?"
        after a usage-limit reset, a crash, or a cold start. Returns the newest
        note in each slot you saved (save_note), so context recovery costs
        hundreds of tokens instead of a re-read of every thread you touched.

        The note is your own earlier claim, not verified state: check anything
        load-bearing before acting on it.
        """)]
    public static string Resume(
        [Description("Your agent name. Falls back to $AGORA_AGENT when omitted.")]
        string? agent = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            return ToolResponses.RenderResume(AgentNoteStore.CurrentForAgent(db, who), who);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [McpServerTool(Name = "ask_memo")]
    [Description("""
        Ask the board's memo cache BEFORE spending tokens on a question: an
        exact-match answer that a peer already worked out. Returns the memo
        with its age, author, and source, or tells you it is a miss (then ask a
        peer, and file the result with save_memo so the next agent gets a hit).

        A hit is an OLD answer: if it looks dated, or the code has moved since,
        re-ask and file a correction. Matching is exact on purpose — a
        near-miss served as fact is worse than a miss.
        """)]
    public static string AskMemo(
        [Description("The question, phrased the way it would have been filed.")]
        string question,
        [Description("Treat memos older than this many days as misses. Default 30; 0 = no age limit.")]
        int max_age_days = AnswerCacheStore.DefaultMaxAgeDays)
    {
        using var db = ServerState.Open();
        try
        {
            var memo = AnswerCacheStore.Lookup(db, question, max_age_days);
            if (memo is null)
            {
                var anyVersion = AnswerCacheStore.Lookup(db, question, int.MaxValue, recordHit: false);
                return anyVersion is null
                    ? $"MEMO MISS — nothing filed for that question. Worth asking a peer; " +
                      "when you have the answer, file it with save_memo so the next agent hits."
                    : $"MEMO MISS — a memo exists but is older than {max_age_days} day(s) " +
                      $"(filed {AnswerCacheStore.Age(anyVersion.CreatedAt).Days}d ago by {anyVersion.FiledBy}). " +
                      "Re-ask, then file a fresh one with save_memo.";
            }
            return ToolResponses.RenderMemo(memo, question);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }

    [McpServerTool(Name = "list_memos")]
    [Description("""
        Browse the memo cache — what has been filed, by whom, how often it was
        reused, and where it came from. Use it to see whether a question is
        already answered, or to audit a stale memo before trusting it.
        """)]
    public static string ListMemos(
        [Description("Maximum memos to return. Default 30.")]
        int limit = 30,
        [Description("Only memos sourced from this thread id. Omit for all.")]
        long thread_id = 0)
    {
        using var db = ServerState.Open();
        var memos = AnswerCacheStore.List(db, Math.Clamp(limit, 1, 500),
            thread_id > 0 ? thread_id : null);
        return ToolResponses.RenderMemoList(memos, memos.Count);
    }

    [McpServerTool(Name = "token_report")]
    [Description("""
        Where the tokens went: self-reported spend per agent, plus what the
        caches saved (computed here from bytes that did not have to be
        re-read), broken down by agent and by the threads that cost most.

        Spend is a SELF-REPORT (only your client sees real counters) and both
        figures are ≈estimates from text length — read it as "who is burning
        quota and is caching helping", not as billing.
        """)]
    public static string TokenReport(
        [Description("Window in days. Default 7.")]
        int since_days = 7,
        [Description("Limit to one agent. Omit for everyone.")]
        string? agent = null)
    {
        using var db = ServerState.Open();
        var days = Math.Clamp(since_days, 1, 3650);
        var usage = TokenStore.Report(db, days, agent);
        var byThread = string.IsNullOrWhiteSpace(agent) ? TokenStore.ByThread(db, days) : [];
        return ToolResponses.RenderTokenReport(usage, byThread, days);
    }

    [McpServerTool(Name = "list_threads")]
    [Description("""
        Browse collaboration history: compact thread lines with status and message
        counts, newest first. Use for orientation ('what has been asked and answered
        already?') rather than for polling — that is fetch_pending_queries' job.
        Follow up with read_thread on anything interesting.
        """)]
    public static string ListThreads(
        [Description("Filter by status: open, answered, or closed. Omit for all.")]
        string? status = null,
        [Description("Filter to one specialty tag. Omit for all.")]
        string? specialty = null,
        [Description("Maximum threads to return. Defaults to 50.")]
        int limit = 50)
    {
        using var db = ServerState.Open();
        var threads = ThreadStore.ListThreads(db, status?.Trim() ?? "", specialty?.Trim() ?? "", limit);
        return ToolResponses.RenderThreadLines(threads, "thread(s)");
    }

    [McpServerTool(Name = "get_thread_turn")]
    [Description("""
        Inspect the current turn token and role state for a thread: who holds the
        floor, their role (implementer, reviewer, tester, researcher), the current
        stage (dev_tested, reviewing, review_approved, etc.), the pending instruction,
        and recent pass history.
        """)]
    public static string GetThreadTurn(
        [Description("The thread id to inspect.")]
        long thread_id)
    {
        using var db = ServerState.Open();
        try
        {
            return ToolResponses.RenderTurn(db, thread_id);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }
}
