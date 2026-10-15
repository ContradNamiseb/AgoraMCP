using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>
/// The valid keys an error message should carry. A refusal that says only
/// "thread 99 does not exist" costs the caller a list call before it can
/// retry; one that names the ids that do exist lets it correct itself on the
/// next turn. Every helper is a bounded indexed query, so a refusal stays as
/// cheap as the call it refuses.
/// </summary>
public static class BoardHints
{
    private const int MaxIds = 8;

    /// <summary>"open: #61 #58 …; newest: #62 #61 …" — the threads a caller most likely meant.</summary>
    public static string ThreadIds(SqliteConnection db)
    {
        var open = Ids(db, "SELECT id FROM threads WHERE status = 'open' AND kind != 'dm' ORDER BY id DESC LIMIT $n");
        var newest = Ids(db, "SELECT id FROM threads WHERE kind != 'dm' ORDER BY id DESC LIMIT $n");
        if (newest.Count == 0) return "the board has no threads yet";
        return $"open: {Join(open)}; newest: {Join(newest)}";
    }

    /// <summary>"thread 41 holds messages #900–#941 (41 messages)" — the valid cursor range.</summary>
    public static string MessageRange(SqliteConnection db, long threadId)
    {
        var (count, first, last) = db.Prepare("""
            SELECT COUNT(*) AS n, COALESCE(MIN(id), 0) AS lo, COALESCE(MAX(id), 0) AS hi
            FROM messages WHERE thread_id = $t
            """, "$t")
            .Set("$t", threadId)
            .Query(r => (r.GetInt64("n"), r.GetInt64("lo"), r.GetInt64("hi")))
            .Single();
        return count == 0
            ? $"thread {threadId} has no messages"
            : $"thread {threadId} holds messages #{first}–#{last} ({count} messages)";
    }

    /// <summary>"open: #12 #9; newest: #14 #13 …" for handoffs.</summary>
    public static string HandoffIds(SqliteConnection db)
    {
        var open = Ids(db, "SELECT id FROM handoffs WHERE status = 'open' ORDER BY id DESC LIMIT $n");
        var newest = Ids(db, "SELECT id FROM handoffs ORDER BY id DESC LIMIT $n");
        if (newest.Count == 0) return "no handoffs exist yet";
        return $"open: {Join(open)}; newest: {Join(newest)}";
    }

    /// <summary>
    /// Every agent name the board knows: the canonical clients plus anyone who
    /// has ever heartbeated or posted. A recipient outside this set is almost
    /// always a typo or a model name used in place of the client name.
    /// </summary>
    public static IReadOnlyList<string> KnownAgents(SqliteConnection db)
    {
        var names = new SortedSet<string>(AgentIdentity.Known, StringComparer.OrdinalIgnoreCase);
        foreach (var name in db.Prepare("SELECT agent FROM presence").Query(r => r.GetString("agent")))
            names.Add(name);
        return [.. names];
    }

    /// <summary>True when <paramref name="agent"/>, or a former name of it, has been seen on this board.</summary>
    public static bool IsKnownAgent(SqliteConnection db, string agent)
    {
        var names = AgentIdentity.NamesFor(agent);
        if (names.Count == 0) return false;
        if (names.Any(n => AgentIdentity.Known.Contains(n, StringComparer.OrdinalIgnoreCase))) return true;

        using var cmd = db.Prepare("SELECT 1 FROM presence WHERE agent IN {ME} UNION ALL " +
                                   "SELECT 1 FROM messages WHERE author IN {ME} LIMIT 1");
        cmd.CommandText = cmd.CommandText.Replace("{ME}", AgentIdentity.NameList(agent, cmd));
        return cmd.Query(_ => 1).Count > 0;
    }

    private static List<long> Ids(SqliteConnection db, string sql) =>
        db.Prepare(sql, "$n").Set("$n", MaxIds).Query(r => r.GetInt64("id"));

    private static string Join(List<long> ids) =>
        ids.Count == 0 ? "none" : string.Join(' ', ids.Select(i => $"#{i}"));
}
