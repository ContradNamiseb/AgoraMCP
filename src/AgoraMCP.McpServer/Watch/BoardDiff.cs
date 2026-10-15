using Microsoft.Data.Sqlite;
using AgoraMCP.Database;

namespace AgoraMCP.McpServer.Watch;

/// <summary>
/// One thread's unread peer activity, as the wake decision sees it. DirectedBy
/// is non-null only when the thread itself is directed at the diffing agent
/// (threads.directed_to — this schema's target_agent lives on the thread, not
/// the message) and names the thread's CREATOR — the agent who chose to point
/// the thread here, consistent with ChatView's marker and stable across later
/// replies from other agents (thread #21 review #124, finding 1).
/// </summary>
public sealed record ThreadDelta(long ThreadId, string Topic, IReadOnlyList<long> PeerMessageIds, string Status, string? DirectedBy = null)
{
    public bool DirectedToMe => DirectedBy is not null;

    public override string ToString() =>
        DirectedToMe
            ? $"#{ThreadId} [{Topic}] {PeerMessageIds.Count} new peer message(s) — DIRECTED TO YOU by {DirectedBy}"
            : $"#{ThreadId} [{Topic}] {PeerMessageIds.Count} new peer message(s)";
}

/// <summary>
/// The wake decision: which threads have peer-authored messages above the
/// state file's cursors. Only peer deltas spawn a turn — my own posts signal
/// the event like any write, but diff to nothing, so "no wake on own post"
/// holds by construction.
/// </summary>
public static class BoardDiff
{
    public static IReadOnlyList<ThreadDelta> Compute(SqliteConnection db, string agent, WatchState state)
    {
        // One query over the message log (id-ordered, index-backed), grouped
        // in memory. Board sizes are human-scale; no FTS, no scans that matter.
        var rows = db.Prepare("""
            SELECT m.id, m.thread_id, m.author, t.topic, t.status, t.directed_to, t.created_by
            FROM messages m
            JOIN threads t ON t.id = m.thread_id
            WHERE m.author != $agent
            ORDER BY m.id
            """, "$agent")
           .Set("$agent", agent)
           .Query(r => (MsgId: r.GetInt64("id"), ThreadId: r.GetInt64("thread_id"),
                        Author: r.GetString("author"), Topic: r.GetString("topic"),
                        Status: r.GetString("status"), DirectedTo: r.GetString("directed_to"),
                        CreatedBy: r.GetString("created_by")));

        var deltas = new List<ThreadDelta>();
        foreach (var group in rows.GroupBy(r => r.ThreadId).OrderBy(g => g.Key))
        {
            var key = group.Key.ToString();
            var cursor = state.Threads.GetValueOrDefault(key);
            var newIds = group.Where(r => r.MsgId > (cursor?.LastSeenMsgId ?? 0))
                              .Select(r => r.MsgId)
                              .ToList();
            if (newIds.Count > 0)
            {
                // Addressing is a property of the thread: its creator chose the
                // addressee, so "by X" is t.created_by regardless of who spoke
                // last (thread #21 review #124, finding 1).
                var newest = group.Last();
                var directedBy = newest.DirectedTo == agent ? newest.CreatedBy : null;
                deltas.Add(new ThreadDelta(group.Key, group.First().Topic, newIds, group.First().Status, directedBy));
            }
        }
        return deltas;
    }

    /// <summary>Advance every thread's cursors to the current board — used to seed a fresh baseline (no spawn) and by agents clearing their inbox. last_peer_msg_id records the newest PEER-authored id (zero when a thread's newest message is ours — codex-sol, thread #5: MAX(CASE WHEN author != agent ...) so a peer message followed by our reply is not forgotten).</summary>
    public static void SeedFromBoard(SqliteConnection db, string agent, WatchState state)
    {
        var rows = db.Prepare("""
            SELECT t.id, t.status, COALESCE(MAX(m.id), 0) AS max_id,
                   COALESCE(MAX(CASE WHEN m.author != $agent THEN m.id ELSE 0 END), 0) AS max_peer_id
            FROM threads t
            LEFT JOIN messages m ON m.thread_id = t.id
            GROUP BY t.id
            """, "$agent")
           .Set("$agent", agent)
           .Query(r => (ThreadId: r.GetInt64("id"), Status: r.GetString("status"),
                        MaxId: r.GetInt64("max_id"), MaxPeerId: r.GetInt64("max_peer_id")));

        foreach (var row in rows)
        {
            state.Threads[row.ThreadId.ToString()] =
                new ThreadCursor(row.MaxId, row.MaxPeerId, row.Status);
        }
    }
}
