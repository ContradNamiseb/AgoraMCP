using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>
/// One agent's delivery state on one thread: how far they have READ (observed)
/// and how far they have HANDLED (acknowledged). Durable and shared — every
/// process sees the same record, so "observed but not handled" is visible on
/// the board instead of buried in a per-agent state file (thread #23 stage 1).
/// The watcher state file stays the retry mechanism; this table is the truth
/// other agents and the user's chat view inspect.
/// </summary>
public sealed record RecipientCursor(
    long ThreadId, string Agent, long LastSeenMsgId, long LastAckMsgId, string UpdatedAt);

/// <summary>
/// Recording and querying per-recipient delivery state. Cursors are MONOTONIC:
/// an upsert can only advance them (MAX of old and new), so a delayed duplicate
/// read or an out-of-order ack can never regress what was already seen or
/// handled — the idempotency that makes re-delivery and re-ack safe.
/// </summary>
public static class RecipientStore
{
    /// <summary>Record that <paramref name="agent"/> has read this thread through <paramref name="upToMsgId"/> (OBSERVED — never counts as handled; thread #23 stage 1).</summary>
    public static void Observe(SqliteConnection db, long threadId, string agent, long upToMsgId) =>
        Upsert(db, threadId, agent, seen: upToMsgId, acked: 0);

    /// <summary>Record that <paramref name="agent"/> has handled this thread through <paramref name="upToMsgId"/> (ACKNOWLEDGED — implies nothing about reads; recording is the durable half of the ack, the watcher state file stays the retry mechanism).</summary>
    public static void Acknowledge(SqliteConnection db, long threadId, string agent, long upToMsgId) =>
        Upsert(db, threadId, agent, seen: 0, acked: upToMsgId);

    private static void Upsert(SqliteConnection db, long threadId, string agent, long seen, long acked)
    {
        // BOUNDARY VALIDATION (thread #23 review #176): a cursor naming a
        // message that does not exist on THIS thread — a cross-thread id, or a
        // future id — would be preserved forever by the monotonic MAX upsert,
        // permanently corrupting the delivery view. Zero is the valid baseline
        // (nothing read/handled); anything else must be a real message id on
        // this thread. Negative bounds are refused outright.
        if (threadId <= 0)
            throw new AgoraException($"recipient cursor: thread id {threadId} is not a thread.");
        if (agent.Trim().Length == 0)
            throw new AgoraException("recipient cursor: every receipt needs an agent name.");
        if (seen < 0 || acked < 0)
            throw new AgoraException("recipient cursor: message boundaries cannot be negative.");
        foreach (var (kind, boundary) in new[] { ("observed", seen), ("handled", acked) })
        {
            if (boundary == 0) continue;
            var exists = db.Prepare(
                "SELECT 1 FROM messages WHERE id = $id AND thread_id = $t", "$id", "$t")
                .SetAll(boundary, threadId)
                .Query(_ => 1).Count > 0;
            if (!exists)
                throw new AgoraException(
                    $"recipient cursor: the {kind} boundary {boundary} does not name a message " +
                    $"on thread {threadId} — refusing to record a cursor that would never be true; " +
                    $"{BoardHints.MessageRange(db, threadId)}.");
        }

        using var cmd = db.Prepare("""
            INSERT INTO recipient_cursors(thread_id, agent, last_seen_msg_id, last_ack_msg_id, updated_at)
            VALUES ($t, $a, $s, $k, $now)
            ON CONFLICT(thread_id, agent) DO UPDATE SET
                last_seen_msg_id = MAX(last_seen_msg_id, excluded.last_seen_msg_id),
                last_ack_msg_id  = MAX(last_ack_msg_id,  excluded.last_ack_msg_id),
                updated_at       = excluded.updated_at
            """, "$t", "$a", "$s", "$k", "$now");
        cmd.SetAll(threadId, agent, seen, acked, AgoraDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every recorded cursor for one thread — the receipts other agents and the chat view render.</summary>
    public static IReadOnlyList<RecipientCursor> ForThread(SqliteConnection db, long threadId) =>
        db.Prepare("""
            SELECT thread_id, agent, last_seen_msg_id, last_ack_msg_id, updated_at
            FROM recipient_cursors WHERE thread_id = $t ORDER BY agent
            """, "$t")
           .Set("$t", threadId)
           .Query(r => new RecipientCursor(
               r.GetInt64("thread_id"), r.GetString("agent"),
               r.GetInt64("last_seen_msg_id"), r.GetInt64("last_ack_msg_id"),
               r.GetString("updated_at")));

    /// <summary>
    /// Delivery classification for one agent across the RELEVANT threads.
    /// The relevance rule is INTENTIONAL and three-way (thread #23 review
    /// #176): a thread is yours if it is DIRECTED at you (all of them,
    /// including historical ones you have never read — that is your mail,
    /// first-arrival included), OR you have AUTHORED in it, OR you hold a
    /// recorded cursor on it (reading a thread makes it relevant). Plain
    /// broadcast/open threads stay invisible until you do one of those —
    /// first arrival lists nothing for a brand-new agent beyond its directed
    /// mail. Pending = newest message unread; Observed = read but unhandled;
    /// Handled = acked through the newest message.
    /// </summary>
    public static IReadOnlyList<DeliveryState> DeliveryFor(SqliteConnection db, string agent)
    {
        // Relevance and receipts both span every name this agent has used: a
        // cursor recorded under a former name is still this agent's receipt,
        // and mail directed at one is still its mail.
        using var cmd = db.Prepare("""
            SELECT t.id, t.topic, t.status, t.directed_to,
                   COALESCE(MAX(m.id), 0) AS max_id,
                   COALESCE(MAX(rc.last_seen_msg_id), 0) AS seen,
                   COALESCE(MAX(rc.last_ack_msg_id), 0) AS acked
            FROM threads t
            LEFT JOIN messages m ON m.thread_id = t.id
            LEFT JOIN recipient_cursors rc ON rc.thread_id = t.id AND rc.agent IN {ME}
            WHERE t.directed_to IN {ME}
               OR EXISTS (SELECT 1 FROM messages pm WHERE pm.thread_id = t.id AND pm.author IN {ME})
               OR EXISTS (SELECT 1 FROM recipient_cursors rc2
                          WHERE rc2.thread_id = t.id AND rc2.agent IN {ME})
            GROUP BY t.id
            ORDER BY t.id
            """);
        cmd.CommandText = cmd.CommandText.Replace("{ME}", AgentIdentity.NameList(agent, cmd));
        return cmd.Query(r => new DeliveryState(
               r.GetInt64("id"), r.GetString("topic"), r.GetString("status"),
               r.GetString("directed_to"), r.GetInt64("max_id"),
               r.GetInt64("seen"), r.GetInt64("acked")));
    }

    /// <summary>One thread's delivery state for one agent, per the classification on <see cref="DeliveryFor"/>.</summary>
    public sealed record DeliveryState(
        long ThreadId, string Topic, string Status, string DirectedTo,
        long MaxMsgId, long LastSeenMsgId, long LastAckMsgId)
    {
        public bool Handled => LastAckMsgId >= MaxMsgId;
        public bool Observed => !Handled && LastSeenMsgId >= MaxMsgId;
        public bool Pending => !Handled && !Observed;
    }
}
