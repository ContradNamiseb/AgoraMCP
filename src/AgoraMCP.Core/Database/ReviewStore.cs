using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>One immutable review-workflow event (thread #23 stage 2).</summary>
public sealed record ReviewEvent(
    long Id, long ThreadId, string Actor, string Kind,
    string Revision, string Snapshot, string Note, string CreatedAt);

/// <summary>The derived current review state of one thread: newest event wins, with the label the compact views show.</summary>
public sealed record ReviewCurrent(
    long ThreadId, string State, string Actor, string Revision, string Note)
{
    public override string ToString()
    {
        var baseLine = State switch
        {
            "awaiting_review" => $"AWAITING REVIEW (rev {Revision}, submitted by {Actor})",
            "approved" => $"APPROVED by {Actor} @ rev {Revision}",
            "changes_required" => $"CHANGES REQUIRED by {Actor} @ rev {Revision}",
            "rejected" => $"REJECTED by {Actor} @ rev {Revision}",
            _ => $"revision submitted by {Actor} @ rev {Revision}",
        };
        return Note.Length > 0 ? $"{baseLine} — {Note}" : baseLine;
    }
}

/// <summary>
/// The implementation-review overlay: verdicts and revision submissions as
/// immutable events. Nothing here is ever updated or deleted — the CURRENT
/// state is derived (newest event wins), so a new revision submission after
/// an approval makes the thread AWAITING REVIEW again without erasing the
/// approval from history: no silent inheritance (thread #23 stage 2, from
/// codex-sol's plan #167). This overlay is separate from the Q&A thread
/// status (open/answered/closed), which keeps its existing meaning.
/// </summary>
public static class ReviewStore
{
    public static readonly string[] Kinds =
        ["revision_submitted", "awaiting_review", "changes_required", "approved", "rejected"];

    /// <summary>Record one event. Validated: kind from the fixed vocabulary, actor and revision required (an approval attached to no snapshot is the exact silent-inheritance hazard this table exists to kill).</summary>
    public static ReviewEvent Record(SqliteConnection db, long threadId, string actor,
        string kind, string revision, string snapshot = "", string note = "")
    {
        if (threadId <= 0)
            throw new AgoraException($"review event: thread id {threadId} is not a thread.");
        // Thread EXISTENCE check (thread #23 review #202, finding 2): a
        // positive-but-stale id would otherwise die as a raw SqliteException
        // on the FK constraint — unhandled by the MCP tools, which only catch
        // AgoraException. Refuse as our own loud error instead.
        var threadExists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
            .Set("$t", threadId)
            .Query(_ => 1).Count > 0;
        if (!threadExists)
            throw new AgoraException($"review event: thread {threadId} does not exist.");
        if (actor.Trim().Length == 0)
            throw new AgoraException("review event: every event names its actor.");
        if (!Kinds.Contains(kind))
            throw new AgoraException(
                $"review event: unknown kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.");
        if (revision.Trim().Length == 0)
            throw new AgoraException(
                "review event: a revision (source-snapshot identifier) is required — an event " +
                "attached to no snapshot is the silent-inheritance hazard this record exists to prevent.");

        using var cmd = db.Prepare("""
            INSERT INTO review_events(thread_id, actor, kind, revision, snapshot, note)
            VALUES ($t, $a, $k, $r, $s, $n)
            """, "$t", "$a", "$k", "$r", "$s", "$n");
        cmd.SetAll(threadId, actor.Trim(), kind, revision.Trim(), snapshot, note.Trim());
        cmd.ExecuteNonQuery();

        return ForThread(db, threadId).Last(e => e.ThreadId == threadId);
    }

    /// <summary>The full immutable history, oldest first.</summary>
    public static IReadOnlyList<ReviewEvent> ForThread(SqliteConnection db, long threadId) =>
        db.Prepare("""
            SELECT id, thread_id, actor, kind, revision, snapshot, note, created_at
            FROM review_events WHERE thread_id = $t ORDER BY id
            """, "$t")
           .Set("$t", threadId)
           .Query(r => new ReviewEvent(
               r.GetInt64("id"), r.GetInt64("thread_id"), r.GetString("actor"),
               r.GetString("kind"), r.GetString("revision"), r.GetString("snapshot"),
               r.GetString("note"), r.GetString("created_at")));

    /// <summary>The current state: the newest event, labeled. No events = no overlay (the thread simply has no review workflow yet).</summary>
    public static ReviewCurrent? Current(SqliteConnection db, long threadId)
    {
        var last = ForThread(db, threadId).LastOrDefault();
        if (last is null) return null;
        var state = last.Kind switch
        {
            "revision_submitted" or "awaiting_review" => "awaiting_review",
            "approved" => "approved",
            "changes_required" => "changes_required",
            "rejected" => "rejected",
            var k => k,
        };
        return new ReviewCurrent(threadId, state, last.Actor, last.Revision, last.Note);
    }
}
