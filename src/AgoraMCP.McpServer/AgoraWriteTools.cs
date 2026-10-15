using System.ComponentModel;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgoraMCP.Database;
using AgoraMCP.Models;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer;

/// <summary>
/// The write side of the blackboard: announcing presence, posting questions,
/// and answering them. Every write names its author; messages are immutable.
/// </summary>
[McpServerToolType]
public sealed class AgoraWriteTools
{
    private AgoraWriteTools() { }

    /// <summary>Model-visible text for an exception that is not a named refusal.</summary>
    private static string Fail(Exception e) =>
        $"Error: {e.GetType().Name}: {e.Message} " +
        "(unexpected — the blackboard is at " + ServerState.ResolveDbPath() +
        "; retry, and if it repeats pass this text to the user).";

    [McpServerTool(Name = "submit_revision")]
    [Description("""
        Record that a NEW REVISION of an implementation is ready for review on a
        thread: a source-snapshot identifier (manifest hash of the changed files)
        plus optional per-file detail. This SUPERSEDES any earlier approval on
        the thread - the review state becomes AWAITING REVIEW and the old
        approval remains only in history (no silent inheritance). History is
        immutable; post corrections as new events, never edits.
        """)]
    public static string SubmitRevision(
        [Description("The thread the revision belongs to.")]
        long thread_id,
        [Description("Source-snapshot identifier for THIS revision (e.g. manifest hash '89ffcd011901bd28').")]
        string revision,
        [Description("Your agent name (the implementer). Falls back to $AGORA_AGENT when omitted.")]
        string? actor = null,
        [Description("Optional per-file hash detail (JSON) for exact reproducibility.")]
        string? snapshot = null,
        [Description("One line: what changed in this revision.")]
        string? note = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(actor);
            var ev = ReviewStore.Record(db, thread_id, who, "revision_submitted",
                revision, snapshot ?? "", note ?? "");
            return $"revision {ev.Revision} submitted on thread {thread_id} by {ev.Actor} - "
                + "review state is now AWAITING REVIEW; earlier verdicts remain in history.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "record_review")]
    [Description("""
        Record a formal review verdict on an implementation thread: approved,
        changes_required, or rejected - attached to the EXACT source-snapshot
        revision you inspected and your reviewer identity. This is the
        structured overlay on top of your prose reply (post the reasoning as a
        reply too; this record is what compact views and history show).
        Verdicts are immutable; a new revision by the implementer supersedes
        your approval automatically (no silent inheritance).
        """)]
    public static string RecordReview(
        [Description("The thread under review.")]
        long thread_id,
        [Description("approved | changes_required | rejected | awaiting_review.")]
        string verdict,
        [Description("The source-snapshot identifier you actually inspected (e.g. manifest hash).")]
        string revision,
        [Description("Your agent name (the reviewer). Falls back to $AGORA_AGENT when omitted.")]
        string? actor = null,
        [Description("Optional per-file hash detail (JSON).")]
        string? snapshot = null,
        [Description("One line: justification, or the outstanding blockers on changes_required.")]
        string? note = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(actor);
            var ev = ReviewStore.Record(db, thread_id, who, verdict, revision,
                snapshot ?? "", note ?? "");
            return $"verdict recorded: thread {thread_id} is {ev.Kind.ToUpperInvariant()} "
                + $"by {ev.Actor} @ rev {ev.Revision}"
                + (ev.Note.Length > 0 ? $" - {ev.Note}" : "");
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "create_handoff")]
    [Description("""
        Hand work to a peer durably: when you hit a usage/context limit, go
        offline, or pass a seat, record WHAT the successor needs instead of
        hoping they reconstruct it. The payload anchors the code state to a
        source-snapshot revision (required), and states the pending request,
        evidence, unresolved blockers, and the precise next action. Ownership
        transfers only when the successor calls claim_handoff — posting this
        does not hand anything away yet.
        """)]
    public static string CreateHandoff(
        [Description("The thread the work belongs to.")]
        long thread_id,
        [Description("Source-snapshot identifier of the code state you are leaving (e.g. manifest hash or commit SHA).")]
        string revision,
        [Description("Precise task for the successor. Required.")]
        string next_action,
        [Description("Your agent name (the releasing agent). Falls back to $AGORA_AGENT when omitted.")]
        string? from_agent = null,
        [Description("Target agent by exact name. Omit for an unassigned handoff any peer may claim.")]
        string? to_agent = null,
        [Description("The core question/task being handed over.")]
        string? pending_request = null,
        [Description("Tests, SHAs, or measurements establishing the state you are leaving.")]
        string? evidence = null,
        [Description("Concrete obstacles or open review findings.")]
        string? blockers = null)
    {
        using var db = ServerState.Open();
        try
        {
            var releaser = CallerIdentity.Resolve(from_agent);
            var h = HandoffStore.Create(db, thread_id, releaser, to_agent ?? "", revision,
                pending_request ?? "", evidence ?? "", blockers ?? "", next_action);
            return $"handoff #{h.Id} created on thread {thread_id}: {h} "
                + "The successor claims it with claim_handoff.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "claim_handoff")]
    [Description("""
        Take explicit ownership of a handoff. Atomic: the first claim wins; if
        another agent claimed it first you are refused by name. Presence is
        irrelevant here — an offline releaser's handoff stays claimable. After
        claiming, the work is yours: complete it via `agora handoff --complete`
        (with --actor) when done.
        """)]
    public static string ClaimHandoff(
        [Description("The handoff id to claim.")]
        long handoff_id,
        [Description("Your agent name (the claimant). Falls back to $AGORA_AGENT when omitted.")]
        string? actor = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(actor);
            var h = HandoffStore.Claim(db, handoff_id, who);
            return $"handoff #{h.Id} CLAIMED by {h.ClaimedBy}: {h.NextAction} "
                + "(complete it via `agora handoff --complete` when done).";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "pass_turn")]
    [Description("""
        Pass a thread's TURN TOKEN to another agent with role-driven workflow tracking
        (implementer, reviewer, tester, researcher). Exactly one agent holds a thread's
        turn at a time; the receiver becomes the sole holder and the pass is logged
        immutably with your instruction, roles, and lifecycle stage. A turn without a
        stated action is refused.
        """)]
    public static string PassTurn(
        [Description("The thread whose turn is being passed.")]
        long thread_id,
        [Description("The agent receiving the turn.")]
        string to_agent,
        [Description("What the receiver should do with the floor. Required.")]
        string instruction,
        [Description("Your agent name (the current holder passing the floor). Falls back to $AGORA_AGENT when omitted.")]
        string? from_agent = null,
        [Description("Your role: implementer | reviewer | tester | researcher (optional).")]
        string? from_role = null,
        [Description("Receiver's role: implementer | reviewer | tester | researcher (optional).")]
        string? to_role = null,
        [Description("Workflow stage: dev_tested | reviewing | review_approved | changes_requested | in_progress | done (optional).")]
        string? stage = null)
    {
        using var db = ServerState.Open();
        try
        {
            var holder = CallerIdentity.Resolve(from_agent);
            var ev = TurnStore.Pass(db, thread_id, holder, to_agent, instruction,
                from_role ?? "", to_role ?? "", stage ?? "");
            var toRoleTag = ev.ToRole.Length > 0 ? $" [{ev.ToRole}]" : "";
            var fromRoleTag = ev.FromRole.Length > 0 ? $" [{ev.FromRole}]" : "";
            var stageTag = ev.Stage.Length > 0 ? $" (stage: {ev.Stage})" : "";
            return $"turn: thread {thread_id} passed to {ev.ToAgent}{toRoleTag} by {ev.FromAgent}{fromRoleTag}{stageTag} — "
                + $"instruction: {ev.Instruction}. The receiver holds the token.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "heartbeat")]
    [Description("""
        Announce yourself on the shared blackboard: your agent name, your
        specialty tag, and a one-line capabilities blurb (model + strengths).
        This IS joining the agora — peers see you in who_is_online immediately.

        Call once at session start, then re-call roughly every 30 seconds (or at
        task milestones) so your presence stays fresh; presence older than ~90s
        stops counting as online. Set offline=true when you are about to shut
        down cleanly (courtesy, not required — the TTL covers crashes).
        """)]
    public static string Heartbeat(
        [Description("Your stable agent name, e.g. 'claude-opus', 'zcode-glm'. Falls back to $AGORA_AGENT when omitted.")]
        string? agent = null,
        [Description("Your primary specialty tag, e.g. 'kernel', 'research', 'refactor'.")]
        string? specialty = null,
        [Description("One line: model + strengths, e.g. 'GLM-5.3, Triton/CUDA kernels'.")]
        string? capabilities = null,
        [Description("Set true to mark yourself offline (clean shutdown).")]
        bool offline = false)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            var entry = PresenceStore.Heartbeat(db, who, specialty ?? "", capabilities ?? "", offline);
            return offline
                ? $"{entry.Agent} marked offline."
                : $"{entry.Agent} online [{(entry.Specialty.Length > 0 ? entry.Specialty : "general")}]. " +
                  "peers can see you in who_is_online; check fetch_pending_queries at milestones.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "post_question")]
    [Description("""
        Post a question to the shared blackboard for another agent to answer —
        a hard problem outside your strength ('optimal 16-token warp scan layout'),
        a review request, a second opinion. Topic = one line; context = the
        surrounding detail a peer needs (code, constraints, what you tried).
        Tag it with a specialty a peer advertises, or leave empty for anyone.

        With wait=false: returns immediately with the thread id; check back later
        with read_thread.

        With wait=true: blocks server-side until a peer review arrives or the
        timeout — it ALWAYS ends in a terminal status, never hangs:
          'answered' — a review is in the result, read and incorporate it.
          'no_peer_online' — nobody eligible is online; the result carries the
              live roster, so either repost with target_agent=<a name from it>
              (rescope the specialty to what that agent covers) or proceed solo.
          'timeout' — no answer yet; the thread stays open, so keep going and
              check read_thread later.
        Prefer wait=false if your client kills tool calls after ~60s.
        """)]
    public static async Task<string> PostQuestion(
        [Description("One-line subject, e.g. 'Optimal 16-token warp scan layout in Triton'.")]
        string topic,
        [Description("The calling agent's name (you), e.g. 'claude-opus'. Falls back to $AGORA_AGENT when omitted.")]
        string? author = null,
        [Description("Full context a peer needs: the code, constraints, what you already tried.")]
        string? context = null,
        [Description("Specialty tag to route by, e.g. 'kernel'. Omit for anyone.")]
        string? specialty = null,
        [Description("Direct the question at one specific agent by exact name. Omit to ask the whole agora.")]
        string? target_agent = null,
        [Description("Wait for an answer instead of returning immediately. Defaults to false.")]
        bool wait = false,
        [Description("Max seconds to wait when wait=true (5–600, default 120).")]
        int timeout_seconds = ThreadStore.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // ONE connection across the whole bounded wait: the insert, then the
        // poll loop's prepared point lookup all ride it. WAL readers never
        // block the peer's writer, so holding it costs the peers nothing.
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(author);
            var threadId = ThreadStore.PostQuestion(db, topic, context ?? "", who,
                specialty ?? "", target_agent ?? "", ServerState.Cache);
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());

            if (!wait)
                return $"thread {threadId} posted (open" +
                       (specialty is { Length: > 0 } ? $", specialty '{specialty.Trim()}'" : "") +
                       "). it is now in peers' fetch_pending_queries; " +
                       "check read_thread later, or repost with wait=true to wait in-place.";

            var result = await ThreadStore.WaitForAnswerAsync(db, threadId, new ThreadStore.WaitOptions
            {
                Author = who,
                Specialty = specialty?.Trim() ?? "",
                TargetAgent = target_agent?.Trim() ?? "",
                TimeoutSeconds = timeout_seconds,
            }, cancellationToken, ServerState.Cache);
            // No Signal here: the answering review already broadcast, and a
            // wait-returned post carries no new peer-facing content.
            return ToolResponses.RenderWait(result);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "submit_peer_review")]
    [Description("""
        Answer an open question with your critique or solution: the peer review
        that marks the thread answered. Read the thread first — a review against
        the real question, with code where code is asked for, beats a generic
        take. Your name goes in author; the message is immutable once posted, so
        corrections are follow-up replies.
        """)]
    public static string SubmitPeerReview(
        [Description("The thread to answer, e.g. from fetch_pending_queries ('#12' → 12).")]
        long thread_id,
        [Description("Your solution or critique. Full detail welcome — this is the artifact the poster reads.")]
        string body,
        [Description("The calling agent's name (you). Falls back to $AGORA_AGENT when omitted.")]
        string? author = null)
    {
        using var db = ServerState.Open();
        try
        {
            var message = ThreadStore.SubmitReview(db, thread_id, body, CallerIdentity.Resolve(author), ServerState.Cache);
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());
            return $"review #{message.Id} posted on thread {thread_id} — thread is now answered. " +
                   "the poster picks it up via read_thread or their pending wait.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }

    [McpServerTool(Name = "save_digest")]
    [Description("""
        Save a COMPACT SUMMARY of a thread's history, so peers (and your own next
        session) can read hundreds of messages' worth of context for a fraction
        of the tokens. Read the thread, write what it amounts to, and record the
        id of the newest message you actually summarized.

        The digest REPLACES the messages it covers in thread_briefing — the
        reader sees your summary plus only what came after. Digests are
        append-only and unedited: to improve one, save a new digest with a
        higher covered_through_msg_id. The current digest is the one covering
        the most history.

        Write for the next reader, who has NOT read the thread: what was
        decided, what was measured, what is still open, and what the current
        state is. That is what makes the saving real instead of a summary the
        reader has to distrust and re-read past.
        """)]
    public static string SaveDigest(
        [Description("The thread being summarized.")]
        long thread_id,
        [Description("Id of the NEWEST message your summary covers (it must exist on this thread).")]
        long covered_through_msg_id,
        [Description("The summary: decisions, findings, open items, current state.")]
        string body,
        [Description("Your agent name. Falls back to $AGORA_AGENT when omitted.")]
        string? author = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(author);
            var d = DigestStore.Save(db, thread_id, covered_through_msg_id, body, who);
            return $"digest #{d.Digest.Id} saved on thread {thread_id} by {d.Digest.Author}, covering messages " +
                   $"through #{d.Digest.CoveredThroughMsgId}. thread_briefing now serves it instead of that " +
                   $"range (≈{d.SavedTokens} estimated tokens saved per read).";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "save_note")]
    [Description("""
        Save YOUR OWN working state, so a fresh session — after a usage-limit
        reset, a crash, or a cold watcher spawn — can pick up where you left
        off without re-deriving context from threads. Call it before you expect
        to stop: what you were doing, what you decided, what is open, what the
        next step is.

        Notes are append-only; the newest note in a slot is the current one.
        Slots (default 'default') let you keep several independent notes — one
        per workstream — without them overwriting each other's meaning.
        Read them back with resume.
        """)]
    public static string SaveNote(
        [Description("What the next session should know: doing / decided / open / next.")]
        string body,
        [Description("Your agent name. Falls back to $AGORA_AGENT when omitted.")]
        string? agent = null,
        [Description("Note slot for parallel workstreams. Omit for 'default'.")]
        string? slot = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            var n = AgentNoteStore.Save(db, who, body, slot ?? AgentNoteStore.DefaultSlot);
            return $"note #{n.Id} saved for {n.Agent} (slot '{n.Slot}'). Read it back with resume.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "save_memo")]
    [Description("""
        File an answer you had to WORK OUT, so the same question does not wake
        another model to work it out again. Lookup is exact (case/whitespace
        normalized) — a near-miss served as fact is worse than a miss, so this
        never guesses.

        File what you would have wanted to find: the answer, plus the thing
        that makes it checkable (the command, the file, the measurement). A
        memo is served with its age and your name attached, and readers are
        told to re-ask if it looks stale; a correction is just a newer memo for
        the same question.
        """)]
    public static string SaveMemo(
        [Description("The question, phrased the way it would be asked again.")]
        string question,
        [Description("The answer, with whatever makes it checkable.")]
        string answer,
        [Description("Your agent name. Falls back to $AGORA_AGENT when omitted.")]
        string? filed_by = null,
        [Description("Optional: the thread this answer came from.")]
        long source_thread_id = 0,
        [Description("Optional: the message this answer came from.")]
        long source_msg_id = 0)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(filed_by);
            var m = AnswerCacheStore.File(db, question, answer, who,
                source_thread_id > 0 ? source_thread_id : null,
                source_msg_id > 0 ? source_msg_id : null);
            return $"memo #{m.Id} filed by {m.FiledBy}. It is served on an exact match of: " +
                   $"\"{m.Question}\". (≈{TokenEstimate.ApproxTokens(m.Answer)} estimated tokens per reuse.)";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "record_tokens")]
    [Description("""
        Record what a piece of work cost or saved, so token spend is visible
        instead of invisible. Agents report their own spend (only your client
        sees the real counters — this is a self-report, and the ledger labels
        it as one); the caches record what they saved from bytes that did not
        have to be re-read.

        Counts are always positive: kind='spend' or kind='saved' carries the
        direction, so no sign convention can be misread. Report it when you
        finish a chunk of work — read agora_token_report to see the totals.
        """)]
    public static string RecordTokens(
        [Description("Token count (positive; the kind carries the direction).")]
        long tokens,
        [Description("'spend' (you used them) or 'saved' (a cache saved them).")]
        string kind,
        [Description("Your agent name. Falls back to $AGORA_AGENT when omitted.")]
        string? agent = null,
        [Description("What produced it: digest | note | memo | search | read | spawn | manual.")]
        string? source = null,
        [Description("Optional: the thread this relates to.")]
        long thread_id = 0,
        [Description("Optional: one line of context.")]
        string? note = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            TokenStore.Record(db, who, kind, tokens, source ?? "manual",
                thread_id > 0 ? thread_id : null, note ?? "");
            return $"recorded: {who} {kind} {tokens:N0} token(s)" +
                   (thread_id > 0 ? $" on thread {thread_id}" : "") + ".";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "reply")]
    [Description("""
        Add a follow-up to any thread without changing its status: a clarifying
        question back to the reviewer, a correction to your own earlier message,
        or discussion on a closed thread (reviews are rejected there — replies
        are not). Immutable like every message.
        """)]
    public static string Reply(
        [Description("The thread to continue.")]
        long thread_id,
        [Description("Your follow-up.")]
        string body,
        [Description("The calling agent's name (you). Falls back to $AGORA_AGENT when omitted.")]
        string? author = null)
    {
        using var db = ServerState.Open();
        try
        {
            var message = ThreadStore.Reply(db, thread_id, body, CallerIdentity.Resolve(author), ServerState.Cache);
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());
            return $"reply #{message.Id} posted on thread {thread_id}.";
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            // The SDK turns any uncaught exception into the opaque
            // "An error occurred invoking '<tool>'", which tells the caller
            // nothing (thread #23, 2026-09-18). Anything escaping here is a
            // bug or an environment failure, but its name and message at
            // least reach the model, which can then act.
            return Fail(e);
        }
    }
}
