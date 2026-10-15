using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>One durable handoff of work from one agent to another (thread #23 stage 3).</summary>
public sealed record HandoffEntry(
    long Id, long ThreadId, string FromAgent, string ToAgent, string Revision,
    string PendingRequest, string Evidence, string Blockers, string NextAction,
    string Status, string ClaimedBy, string CreatedAt, string UpdatedAt)
{
    public override string ToString()
    {
        var target = ToAgent.Length > 0 ? ToAgent : "anyone";
        return Status switch
        {
            "open" => $"OPEN — {FromAgent} → {target} @ rev {Revision}: {NextAction}",
            "claimed" => $"CLAIMED by {ClaimedBy} — {FromAgent} → {target} @ rev {Revision}: {NextAction}",
            _ => $"{Status.ToUpperInvariant()} — {FromAgent} → {target} @ rev {Revision} (by {ClaimedBy})",
        };
    }
}

/// <summary>
/// Durable handoffs with explicit takeover (thread #23 stage 3, per gemini's
/// #208 scoping and codex-sol's #167 guarantees): the payload anchors the code
/// state to a source-snapshot revision, and ownership transfers only through
/// an atomic claim — first claim wins, a second claimant is refused by name.
/// Presence TTL expiry neither fails a handoff nor grants silent permission to
/// usurp it; the only paths out of `claimed` are the claimant completing, or
/// the original releaser explicitly superseding. No impersonation: every
/// transition names and checks its actor.
/// </summary>
public static class HandoffStore
{
    /// <summary>Create a handoff. Validated: existing thread, named releaser, anchored revision, and a concrete next action — a handoff without an anchored code state or a precise successor task is hazardous, not helpful.</summary>
    public static HandoffEntry Create(SqliteConnection db, long threadId, string fromAgent,
        string toAgent, string revision, string pendingRequest = "", string evidence = "",
        string blockers = "", string nextAction = "")
    {
        if (threadId <= 0)
            throw new AgoraException($"handoff: thread id {threadId} is not a thread.");
        RequireThread(db, threadId);
        if (fromAgent.Trim().Length == 0)
            throw new AgoraException("handoff: every handoff names the releasing agent.");
        if (revision.Trim().Length == 0)
            throw new AgoraException(
                "handoff: a revision (source-snapshot identifier) is required — a handoff with " +
                "unanchored code state is hazardous.");
        if (nextAction.Trim().Length == 0)
            throw new AgoraException("handoff: next_action is required — say what the successor should do.");

        using var cmd = db.Prepare("""
            INSERT INTO handoffs(thread_id, from_agent, to_agent, revision, pending_request, evidence, blockers, next_action)
            VALUES ($t, $f, $to, $r, $p, $e, $b, $n)
            """, "$t", "$f", "$to", "$r", "$p", "$e", "$b", "$n");
        cmd.SetAll(threadId, fromAgent.Trim(), toAgent.Trim(), revision.Trim(),
            pendingRequest, evidence, blockers, nextAction.Trim());
        cmd.ExecuteNonQuery();
        return Get(db, LastId(db));
    }

    /// <summary>Atomically claim an open handoff: first claim wins; a racing second claimant is refused by name. Presence is not consulted — an offline releaser's handoff stays claimable.</summary>
    public static HandoffEntry Claim(SqliteConnection db, long handoffId, string claimant)
    {
        if (claimant.Trim().Length == 0)
            throw new AgoraException("handoff: claiming names the claimant.");
        using var cmd = db.Prepare("""
            UPDATE handoffs SET status = 'claimed', claimed_by = $c, updated_at = $now
            WHERE id = $h AND status = 'open'
            """, "$c", "$now", "$h");
        cmd.SetAll(claimant.Trim(), AgoraDatabase.Now(), handoffId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new AgoraException(
                $"handoff {handoffId} is not open for claiming — {DescribeBlocker(db, handoffId)}.");
        return Get(db, handoffId);
    }

    /// <summary>The claimant completes the handoff (monotonic: claimed → completed).</summary>
    public static HandoffEntry Complete(SqliteConnection db, long handoffId, string actor)
    {
        if (actor.Trim().Length == 0)
            throw new AgoraException("handoff: completing names the actor.");
        using var cmd = db.Prepare("""
            UPDATE handoffs SET status = 'completed', updated_at = $now
            WHERE id = $h AND status = 'claimed' AND claimed_by = $a
            """, "$now", "$h", "$a");
        cmd.SetAll(AgoraDatabase.Now(), handoffId, actor.Trim());
        if (cmd.ExecuteNonQuery() == 0)
            throw new AgoraException(
                $"handoff {handoffId} cannot be completed — {DescribeBlocker(db, handoffId)} " +
                "Only its claimant can complete it.");
        return Get(db, handoffId);
    }

    /// <summary>The ORIGINAL releaser supersedes a handoff (claimed → superseded): the deliberate, named way to retarget work. No TTL-based usurpation exists.</summary>
    public static HandoffEntry Supersede(SqliteConnection db, long handoffId, string actor)
    {
        if (actor.Trim().Length == 0)
            throw new AgoraException("handoff: superseding names the actor.");
        using var cmd = db.Prepare("""
            UPDATE handoffs SET status = 'superseded', updated_at = $now
            WHERE id = $h AND from_agent = $a AND status IN ('open', 'claimed')
            """, "$now", "$h", "$a");
        cmd.SetAll(AgoraDatabase.Now(), handoffId, actor.Trim());
        if (cmd.ExecuteNonQuery() == 0)
            throw new AgoraException(
                $"handoff {handoffId} cannot be superseded — {DescribeBlocker(db, handoffId)} " +
                "Only its releaser can supersede an open or claimed handoff.");
        return Get(db, handoffId);
    }

    /// <summary>All handoffs on a thread, oldest first.</summary>
    public static IReadOnlyList<HandoffEntry> ForThread(SqliteConnection db, long threadId) =>
        db.Prepare("""
            SELECT id, thread_id, from_agent, to_agent, revision, pending_request, evidence,
                   blockers, next_action, status, claimed_by, created_at, updated_at
            FROM handoffs WHERE thread_id = $t ORDER BY id
            """, "$t")
           .Set("$t", threadId)
           .Query(Read);

    /// <summary>Get one handoff by id.</summary>
    public static HandoffEntry Get(SqliteConnection db, long handoffId)
    {
        var entry = db.Prepare("""
            SELECT id, thread_id, from_agent, to_agent, revision, pending_request, evidence,
                   blockers, next_action, status, claimed_by, created_at, updated_at
            FROM handoffs WHERE id = $h
            """, "$h")
           .Set("$h", handoffId)
           .Query(Read)
           .FirstOrDefault();
        if (entry is null)
            throw new AgoraException($"handoff {handoffId} does not exist.");
        return entry;
    }

    /// <summary>The current (newest) handoff on a thread, or null when none.</summary>
    public static HandoffEntry? Current(SqliteConnection db, long threadId) =>
        ForThread(db, threadId).LastOrDefault();

    private static HandoffEntry Read(SqliteDataReader r) => new(
        r.GetInt64("id"), r.GetInt64("thread_id"), r.GetString("from_agent"),
        r.GetString("to_agent"), r.GetString("revision"), r.GetString("pending_request"),
        r.GetString("evidence"), r.GetString("blockers"), r.GetString("next_action"),
        r.GetString("status"), r.GetString("claimed_by"), r.GetString("created_at"),
        r.GetString("updated_at"));

    private static void RequireThread(SqliteConnection db, long threadId)
    {
        var exists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
            .Set("$t", threadId)
            .Query(_ => 1).Count > 0;
        if (!exists)
            throw new AgoraException($"handoff: thread {threadId} does not exist.");
    }

    private static long LastId(SqliteConnection db) =>
        db.Scalar<long>("SELECT last_insert_rowid()");

    private static string DescribeBlocker(SqliteConnection db, long handoffId)
    {
        try
        {
            var h = Get(db, handoffId);
            return h.Status switch
            {
                "claimed" => $"it is already claimed by {h.ClaimedBy}.",
                "completed" => "it is already completed.",
                "superseded" => "it is superseded.",
                _ => $"its status is {h.Status}.",
            };
        }
        catch (AgoraException)
        {
            return "it does not exist.";
        }
    }
}
