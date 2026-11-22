using System.ComponentModel;
using ModelContextProtocol.Server;
using AgoraMCP.Database;
using AgoraMCP.Models;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer;

/// <summary>
/// Compound tools: one call for what a turn otherwise spends five on.
///
/// <para>Acting on a thread used to be read → reply → record_review →
/// pass_turn → (no MCP way to ack or close at all) → read again to see what
/// arrived meanwhile. Each hop is a model round trip that re-sends the whole
/// context, so the turn cost scales with the number of calls, not with the
/// work. <see cref="Respond"/> does all of it against one connection and
/// returns only what is new.</para>
/// </summary>
[McpServerToolType]
public sealed class AgoraWorkflowTools
{
    private AgoraWorkflowTools() { }

    [McpServerTool(Name = "respond")]
    [Description("""
        The one tool for acting on a thread (replaces reply, submit_peer_review,
        record_review and pass_turn). In this order: post body (kind reply, or
        review = marks answered), record verdict on revision (with an optional
        note), pass the turn to pass_to, set status, ack. Each part is optional, but give at least one;
        all inputs are checked before anything is written. Returns one line per
        action taken, then the messages other agents posted since since_id (or
        since your last read receipt when since_id is omitted), capped by
        max_chars. ack=true marks the thread HANDLED through your post when
        nothing unseen arrived, otherwise only through what you had seen. If a
        later step fails, the result starts 'partial:' and lists what was
        written; do not repeat those parts.
        """)]
    public static string Respond(
        [Description("Thread id.")]
        long thread_id,
        [Description("Message to post. Required for kind=review.")]
        string? body = null,
        [Description("reply keeps the status; review marks the thread answered.")]
        MessageKind kind = MessageKind.Reply,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? author = null,
        [Description("Newest message id you already hold; the result returns later ones. 0 = your last read receipt.")]
        long since_id = 0,
        [Description("Formal verdict to record. Needs revision.")]
        ReviewVerdict? verdict = null,
        [Description("Source-snapshot id the verdict applies to.")]
        string? revision = null,
        [Description("One line recorded with the verdict: justification, or the blockers.")]
        string? note = null,
        [Description("Pass the turn token to this agent.")]
        string? pass_to = null,
        [Description("What pass_to must do. Default: act on the posted message.")]
        string? instruction = null,
        [Description("Your role, recorded on the turn pass.")]
        TurnRole? from_role = null,
        [Description("Receiver's role, recorded on the turn pass.")]
        TurnRole? to_role = null,
        [Description("Workflow stage after the pass.")]
        TurnStage? stage = null,
        [Description("Set the thread status after posting, e.g. closed.")]
        ThreadStatus? status = null,
        [Description("Mark the thread handled for you. Default true.")]
        bool ack = true,
        [Description("Max characters of returned messages. Default 8000; 0 = no cap.")]
        int max_chars = 8000)
    {
        using var db = ServerState.Open();
        string me;
        ThreadDetail detail;
        try
        {
            me = CallerIdentity.Resolve(author);
            detail = ThreadStore.ReadThread(db, thread_id, ServerState.Cache);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }

        // Every refusal happens here, before the first write, so a bad call
        // never leaves half its effects on the board.
        var text = body?.Trim() ?? "";
        var problems = new List<string>();
        if (me.Length == 0)
            problems.Add($"respond needs an author: pass author, or set {CallerIdentity.AgentEnvVar} for this server");
        if (text.Length == 0 && verdict is null && string.IsNullOrWhiteSpace(pass_to) && status is null)
            problems.Add("nothing to do: give body, verdict, pass_to or status");
        if (kind == MessageKind.Review && text.Length == 0)
            problems.Add("kind=review needs a body");
        if (kind == MessageKind.Review && detail.Header.Status == ThreadStore.StatusClosed && text.Length > 0)
            problems.Add($"thread {thread_id} is closed — use kind=reply, or pass status=open first");
        if (verdict is not null && string.IsNullOrWhiteSpace(revision))
            problems.Add("verdict needs revision (the snapshot id you inspected)");
        if (!string.IsNullOrWhiteSpace(pass_to))
        {
            if (AgentIdentity.AnswersTo(me, pass_to))
                problems.Add($"pass_to '{pass_to.Trim()}' is you — the holder would not change");
            if (text.Length == 0 && string.IsNullOrWhiteSpace(instruction))
                problems.Add("pass_to needs an instruction, or a body the receiver can act on");
        }
        if (problems.Count > 0)
            return $"Error: respond: {string.Join("; ", problems)}. Nothing was written.";

        // The delta boundary is fixed BEFORE writing, so the caller's own post
        // never counts as news and a peer's message that lands mid-call does.
        var cursor = RecipientStore.ForThread(db, thread_id)
            .Where(c => AgentIdentity.AnswersTo(me, c.Agent))
            .Select(c => c.LastSeenMsgId)
            .DefaultIfEmpty(0)
            .Max();
        var since = since_id > 0 ? since_id : cursor;
        var unseen = since > 0
            ? detail.Messages.Where(m => m.Id > since && !AgentIdentity.AnswersTo(me, m.Author)).ToList()
            : [];

        var done = new List<string>();
        try
        {
            // Wrap multi-table writes (message + review + turn pass + status + ack)
            // in one SQLite transaction so concurrent tail/bridge readers never
            // observe a reply before its companion turn_events row commits.
            using var tx = db.BeginTransaction();
            long? posted = null;
            if (text.Length > 0)
            {
                var message = kind == MessageKind.Review
                    ? ThreadStore.SubmitReview(db, thread_id, text, me, ServerState.Cache, tx)
                    : ThreadStore.Reply(db, thread_id, text, me, ServerState.Cache, tx);
                posted = message.Id;
                done.Add($"posted {kind.Wire()} #{message.Id}" +
                         (kind == MessageKind.Review ? " (thread answered)" : ""));
            }

            if (verdict is { } v)
            {
                var ev = ReviewStore.Record(db, thread_id, me, v.Wire(), revision!, "", note?.Trim() ?? "");
                done.Add($"verdict {ev.Kind.ToUpperInvariant()} @ rev {ev.Revision}");
            }

            if (!string.IsNullOrWhiteSpace(pass_to))
            {
                var ask = string.IsNullOrWhiteSpace(instruction) ? $"act on message #{posted}" : instruction;
                var ev = TurnStore.Pass(db, thread_id, me, pass_to.Trim(), ask,
                    from_role.Wire(), to_role.Wire(), stage.Wire());
                done.Add($"turn → {ev.ToAgent}" + (ev.ToRole.Length > 0 ? $" [{ev.ToRole}]" : "") +
                         (ev.Stage.Length > 0 ? $" (stage {ev.Stage})" : "") +
                         ToolResponses.UnknownAgentNote(db, pass_to).Replace("\n", " "));
            }

            if (status is { } s)
            {
                var before = ThreadStore.SetStatus(db, thread_id, s.Wire());
                done.Add($"status {before} → {s.Wire()}");
            }

            if (ack)
            {
                // HANDLED means acted on. Through the caller's own post only when
                // nothing arrived that it has not seen; otherwise only through
                // what it had seen, so the unseen messages stay pending.
                var through = unseen.Count == 0 ? posted ?? detail.Messages.LastOrDefault()?.Id ?? 0 : since;
                var onThread = through > 0 && (through == posted || detail.Messages.Any(m => m.Id == through));
                if (onThread)
                {
                    RecipientStore.Acknowledge(db, thread_id, me, through);
                    done.Add(unseen.Count == 0
                        ? $"handled through #{through}"
                        : $"handled through #{through} only — {unseen.Count} unseen message(s) below stay pending");
                }
            }

            tx.Commit();
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());
        }
        catch (Exception e)
        {
            var reason = e is AgoraException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            return $"Error: respond: {reason}. Nothing was written.";
        }

        var lines = new List<string> { $"thread #{thread_id} by {me}: {string.Join("; ", done)}." };
        if (since == 0)
        {
            lines.Add($"(no read receipt for you on this thread, so no delta; pass since_id to get one — " +
                      $"{BoardHints.MessageRange(db, thread_id)})");
        }
        else if (unseen.Count == 0)
        {
            lines.Add($"no messages from others since #{since}.");
        }
        else
        {
            lines.Add($"new from others since #{since} ({unseen.Count}):");
            lines.AddRange(ToolResponses.Budgeted(unseen, max_chars, out var shown));
            if (shown > 0)
            {
                try
                {
                    RecipientStore.Observe(db, thread_id, me, unseen[shown - 1].Id);
                }
                catch (AgoraException)
                {
                    // A receipt is bookkeeping; the response already landed.
                }
            }
        }
        return string.Join('\n', lines);
    }

    [McpServerTool(Name = "search_threads", ReadOnly = true)]
    [Description("""
        Find threads by keyword across topics, context and message bodies,
        best match first. Read-only. Use 2–4 short keywords, not a sentence;
        terms are ANDed with an automatic any-term fallback. Each hit is a
        thread line plus a [highlighted] snippet; capped by max_chars, with
        overflow listed as thread ids.
        """)]
    public static string SearchThreads(
        [Description("A few keywords or an identifier.")]
        string query,
        [Description("Max hits. Default 10.")]
        int limit = 10,
        [Description("Max output characters. Default 6000; 0 = no cap.")]
        int max_chars = 6000)
    {
        using var db = ServerState.Open();
        try
        {
            return ToolResponses.RenderHits(ThreadStore.Search(db, query ?? "", limit), max_chars);
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
    }
}
