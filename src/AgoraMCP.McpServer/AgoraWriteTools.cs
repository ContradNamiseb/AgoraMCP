using System.ComponentModel;
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

    /// <summary>
    /// Model-visible text for an exception that is not a named refusal. The SDK
    /// turns any uncaught exception into the opaque "An error occurred invoking
    /// '&lt;tool&gt;'", which tells the caller nothing (thread #23, 2026-09-18).
    /// Anything reaching here is a bug or an environment failure, but its name
    /// and message at least reach the model, which can then act.
    /// </summary>
    internal static string Fail(Exception e) =>
        $"Error: {e.GetType().Name}: {e.Message} " +
        "(unexpected — the blackboard is at " + ServerState.ResolveDbPath() +
        "; retry, and if it repeats pass this text to the user).";

    [McpServerTool(Name = "submit_revision")]
    [Description("""
        Announce a new revision of an implementation for review. Writes a
        review event; the thread's review state becomes AWAITING REVIEW and any
        earlier approval stays only in history. Returns one confirmation line.
        """)]
    public static string SubmitRevision(
        [Description("Thread id.")]
        long thread_id,
        [Description("Source-snapshot id of this revision, e.g. a commit SHA or manifest hash.")]
        string revision,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? actor = null,
        [Description("Per-file hash detail (JSON).")]
        string? snapshot = null,
        [Description("One line: what changed.")]
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
            return Fail(e);
        }
    }

    [McpServerTool(Name = "create_handoff")]
    [Description("""
        Hand work to a peer durably when you hit a limit or go offline. Writes
        a handoff anchored to a code revision; ownership moves only when the
        successor calls claim_handoff. Returns the handoff id.
        """)]
    public static string CreateHandoff(
        [Description("Thread id.")]
        long thread_id,
        [Description("Source-snapshot id of the code state you leave, e.g. a commit SHA.")]
        string revision,
        [Description("Exact task for the successor.")]
        string next_action,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? from_agent = null,
        [Description("Successor's agent name. Omit to let any peer claim it.")]
        string? to_agent = null,
        [Description("The question or task being handed over.")]
        string? pending_request = null,
        [Description("Tests, SHAs or measurements that establish the state.")]
        string? evidence = null,
        [Description("Open obstacles or review findings.")]
        string? blockers = null)
    {
        using var db = ServerState.Open();
        try
        {
            var releaser = CallerIdentity.Resolve(from_agent);
            var h = HandoffStore.Create(db, thread_id, releaser, to_agent ?? "", revision,
                pending_request ?? "", evidence ?? "", blockers ?? "", next_action);
            return $"handoff #{h.Id} created on thread {thread_id}: {h} "
                + "The successor claims it with claim_handoff."
                + ToolResponses.UnknownAgentNote(db, to_agent);
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

    [McpServerTool(Name = "claim_handoff")]
    [Description("""
        Take ownership of an open handoff. Writes atomically: the first claim
        wins and a later claimant is refused by name; unknown or closed ids
        are refused with the valid ones. Returns the next action.
        """)]
    public static string ClaimHandoff(
        [Description("Handoff id.")]
        long handoff_id,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
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
            return e.Message.Contains("Valid handoff ids", StringComparison.Ordinal)
                ? $"Error: {e.Message}"
                : $"Error: {e.Message} Valid handoff ids — {BoardHints.HandoffIds(db)}.";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "heartbeat")]
    [Description("""
        Join or refresh presence on the board so peers can route to you.
        Writes your presence row. Call at session start (with capabilities
        naming your model), then at milestones; presence lapses after ~90s
        unheard. offline=true on clean shutdown.
        """)]
    public static string Heartbeat(
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? agent = null,
        [Description("Your primary specialty.")]
        Specialty? specialty = null,
        [Description("One line: the model you are running + strengths.")]
        string? capabilities = null,
        [Description("Mark yourself offline.")]
        bool offline = false)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            var entry = PresenceStore.Heartbeat(db, who, specialty.Wire(), capabilities ?? "", offline);
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
            return Fail(e);
        }
    }

    [McpServerTool(Name = "post_question")]
    [Description("""
        Open a new thread asking peers for an answer, review or second opinion.
        Writes a thread. wait=false (preferred) returns the thread id at once.
        wait=true blocks up to timeout_seconds and always ends in one status:
        'answered' (the reply is included), 'no_peer_online' (includes the live
        roster: repost with target_agent from it, or proceed solo), or
        'timeout' (thread stays open; check later).
        """)]
    public static async Task<string> PostQuestion(
        [Description("One-line subject.")]
        string topic,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? author = null,
        [Description("Everything a peer needs: code, constraints, what you tried, the exact help wanted.")]
        string? context = null,
        [Description("Route to agents with this specialty. Omit for anyone.")]
        Specialty? specialty = null,
        [Description("Address one agent by exact name. Omit to ask everyone.")]
        string? target_agent = null,
        [Description("Block until answered or timed out. Default false.")]
        bool wait = false,
        [Description("Seconds to wait when wait=true (5–600, default 120).")]
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
            var routedTo = specialty.Wire();
            var threadId = ThreadStore.PostQuestion(db, topic, context ?? "", who,
                routedTo, target_agent ?? "", ServerState.Cache);
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());

            if (!wait)
                return $"thread {threadId} posted (open" +
                       (routedTo.Length > 0 ? $", specialty '{routedTo}'" : "") +
                       "). it is now in peers' fetch_pending_queries; " +
                       "check read_thread later, or repost with wait=true to wait in-place." +
                       ToolResponses.UnknownAgentNote(db, target_agent);

            var result = await ThreadStore.WaitForAnswerAsync(db, threadId, new ThreadStore.WaitOptions
            {
                Author = who,
                Specialty = routedTo,
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
            return Fail(e);
        }
    }

    [McpServerTool(Name = "save_digest")]
    [Description("""
        Save a summary of a thread's history so readers skip the messages it
        covers. Writes an append-only digest; thread_briefing serves the one
        covering the most history. covered_through_msg_id must be the newest
        message you actually summarized (an invalid id is refused with the
        thread's valid range). Write for a reader who has not read the thread:
        decisions, measurements, open items, current state.
        """)]
    public static string SaveDigest(
        [Description("Thread id.")]
        long thread_id,
        [Description("Id of the newest message the summary covers.")]
        long covered_through_msg_id,
        [Description("The summary.")]
        string body,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
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
        Save your own working state for your next session: doing, decided,
        open, next. Writes an append-only note; the newest per slot is
        current, and resume reads them back. Call before you expect to stop.
        """)]
    public static string SaveNote(
        [Description("The note: doing / decided / open / next.")]
        string body,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? agent = null,
        [Description("Workstream slot. Default 'default'.")]
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
        File an answer you had to work out, so ask_memo serves it next time.
        Writes a memo keyed by the exact question (case/whitespace
        normalised); a newer memo for the same question is the correction.
        Include what makes it checkable: the command, file or measurement.
        """)]
    public static string SaveMemo(
        [Description("The question, phrased as it would be asked again.")]
        string question,
        [Description("The answer, with whatever makes it checkable.")]
        string answer,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? filed_by = null,
        [Description("Thread the answer came from.")]
        long source_thread_id = 0,
        [Description("Message the answer came from.")]
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
        Record what a piece of work cost or saved. Writes a ledger row; the
        count is always positive and kind carries the direction. Spend is a
        self-report. Read totals with token_report.
        """)]
    public static string RecordTokens(
        [Description("Token count, positive.")]
        long tokens,
        [Description("spend = you used them; saved = a cache saved them.")]
        TokenKind kind,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? agent = null,
        [Description("What produced it. Default manual.")]
        TokenSource? source = null,
        [Description("Related thread.")]
        long thread_id = 0,
        [Description("One line of context.")]
        string? note = null)
    {
        using var db = ServerState.Open();
        try
        {
            var who = CallerIdentity.Resolve(agent);
            TokenStore.Record(db, who, kind.Wire(), tokens, source is null ? "manual" : source.Wire(),
                thread_id > 0 ? thread_id : null, note ?? "");
            return $"recorded: {who} {kind.Wire()} {tokens:N0} token(s)" +
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
}
