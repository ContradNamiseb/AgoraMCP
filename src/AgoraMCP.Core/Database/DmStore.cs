using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>One conversation row of the inbox.</summary>
public sealed record DmSummary(
    long ThreadId,
    string From,
    string To,
    string LastAuthor,
    string LastMessage,
    int TotalMessages,
    string UpdatedAt);

public static class DmStore
{
    public const string ThreadKind = "dm";
    public const string MessageKind = "dm";

    /// <summary>
    /// Delivers a direct message. A DM conversation is one thread shared by the
    /// pair (either direction reuses it), so a back-and-forth reads as one
    /// history. Falls back to the board thread machinery underneath, so
    /// watchers, directed routing and receipts treat a DM like any other
    /// directed message.
    /// </summary>
    public static (long ThreadId, long MessageId) Send(
        SqliteConnection db, string from, string to, string body)
    {
        if (string.IsNullOrWhiteSpace(from))
            throw new AgoraException("send_dm needs the calling agent's name");
        if (string.IsNullOrWhiteSpace(to))
            throw new AgoraException("send_dm needs a recipient agent name");
        if (string.IsNullOrWhiteSpace(body))
            throw new AgoraException("send_dm needs a body");

        from = AgentIdentity.ValidateName(from, "author");
        to = AgentIdentity.ValidateName(to, "to_agent");

        long threadId = FindPairThread(db, from, to);
        if (threadId == 0)
        {
            using var cmd = db.Prepare(
                "INSERT INTO threads(topic, context, specialty, directed_to, created_by, kind) " +
                "VALUES($topic, '', '', $to, $from, 'dm')",
                "$topic", "$to", "$from");
            cmd.Set("$topic", $"DM {from} ↔ {to}");
            cmd.Set("$to", to);
            cmd.Set("$from", from);
            cmd.ExecuteNonQuery();
            threadId = db.Scalar<long>("SELECT last_insert_rowid()");
        }

        using var msg = db.Prepare(
            "INSERT INTO messages(thread_id, author, kind, body) " +
            "VALUES($thread, $author, 'dm', $body)",
            "$thread", "$author", "$body");
        msg.Set("$thread", threadId);
        msg.Set("$author", from);
        msg.Set("$body", body);
        msg.ExecuteNonQuery();
        long messageId = db.Scalar<long>("SELECT last_insert_rowid()");

        using var bump = db.Prepare(
            "UPDATE threads SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = $thread",
            "$thread");
        bump.Set("$thread", threadId);
        bump.ExecuteNonQuery();
        return (threadId, messageId);
    }

    /// <summary>
    /// The DM conversations an agent takes part in, newest activity first.
    /// Membership follows AgentIdentity names, so mail sent to a former name
    /// still shows up after a rename.
    /// </summary>
    public static IReadOnlyList<DmSummary> Inbox(SqliteConnection db, string agent, int limit = 20)
    {
        var names = AgentIdentity.NamesFor(agent);
        if (names.Count == 0) return [];

        var list = string.Join(", ", Enumerable.Range(0, names.Count).Select(i => $"$n{i}"));
        var paramNames = Enumerable.Range(0, names.Count).Select(i => $"$n{i}")
            .Concat(["$limit"]).ToArray();
        using var cmd = db.Prepare(
            "SELECT t.id, t.created_by, t.directed_to, m.author, m.body, m.created_at, " +
            "       (SELECT COUNT(*) FROM messages x WHERE x.thread_id = t.id) AS total " +
            "FROM threads t JOIN messages m ON m.id = " +
            "  (SELECT MAX(id) FROM messages WHERE thread_id = t.id) " +
            $"WHERE t.kind = 'dm' AND (t.created_by IN ({list}) OR t.directed_to IN ({list})) " +
            "ORDER BY m.created_at DESC LIMIT $limit",
            paramNames);
        for (var i = 0; i < names.Count; i++) cmd.Set($"$n{i}", names[i]);
        cmd.Set("$limit", limit);
        return cmd.Query(r => new DmSummary(
            r.GetInt64("id"),
            r.GetString("created_by"),
            r.GetString("directed_to"),
            r.GetString("author"),
            r.GetString("body"),
            r.GetInt32("total"),
            r.GetString("created_at")));
    }

    /// <summary>The pair's shared conversation thread, or 0 when none exists.</summary>
    private static long FindPairThread(SqliteConnection db, string a, string b)
    {
        var aNames = AgentIdentity.NamesFor(a);
        var bNames = AgentIdentity.NamesFor(b);
        using var cmd = db.Prepare(
            "SELECT id FROM threads WHERE kind = 'dm' AND " +
            "((created_by IN ($a0, $a1, $a2, $a3) AND directed_to IN ($b0, $b1, $b2, $b3)) OR " +
            " (created_by IN ($b0, $b1, $b2, $b3) AND directed_to IN ($a0, $a1, $a2, $a3))) " +
            "ORDER BY id DESC LIMIT 1",
            "$a0", "$a1", "$a2", "$a3", "$b0", "$b1", "$b2", "$b3");
        for (var i = 0; i < 4; i++)
        {
            cmd.Set($"$a{i}", i < aNames.Count ? aNames[i] : "");
            cmd.Set($"$b{i}", i < bNames.Count ? bNames[i] : "");
        }
        return cmd.ExecuteScalar() is long id ? id : 0;
    }
}
