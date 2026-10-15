using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// The token ledger: what agents spent and what the caches saved.
///
/// <para>Half of this is self-reported and the store says so. This process
/// never sees a model's token counters — only the client does — so a 'spend'
/// row is an agent's own statement about its usage. A 'saved' row is
/// different: it is computed locally from bytes that did not have to be
/// re-read, which this process can measure. The two are kept in one table
/// because they answer the same question ("where do the tokens go?") and
/// separated by <c>kind</c>, never by sign — a signed column invites exactly
/// the misreading that a kind column cannot.</para>
/// </summary>
public static class TokenStore
{
    public const string KindSpend = "spend";
    public const string KindSaved = "saved";

    public static readonly string[] Sources =
        ["digest", "note", "memo", "search", "read", "spawn", "manual"];

    /// <summary>Record one event. Validated: named agent, known kind, non-negative count.</summary>
    public static void Record(SqliteConnection db, string agent, string kind, long tokens,
        string source = "manual", long? threadId = null, string note = "")
    {
        agent = (agent ?? "").Trim();
        if (agent.Length == 0)
            throw new AgoraException("tokens: every entry names its agent — pass the calling agent's name.");

        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind != KindSpend && kind != KindSaved)
            throw new AgoraException($"tokens: unknown kind '{kind}'. Use '{KindSpend}' or '{KindSaved}'.");

        if (tokens < 0)
            throw new AgoraException(
                "tokens: the count must be positive — the DIRECTION comes from kind ('spend' or 'saved'), " +
                "so a negative number would double-sign it.");

        if (threadId is { } tid && tid > 0)
        {
            var exists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
                .Set("$t", tid).Query(_ => 1).Count > 0;
            if (!exists) throw ThreadStore.Missing(db, tid, "tokens: ");
        }
        else
        {
            threadId = null;
        }

        db.Prepare("""
            INSERT INTO token_events(agent, kind, tokens, thread_id, source, note)
            VALUES ($a, $k, $n, $t, $s, $o)
            """, "$a", "$k", "$n", "$t", "$s", "$o")
          .SetAll(agent, kind, tokens, threadId,
              (source ?? "").Trim() is { Length: > 0 } src ? src : "manual", note ?? "")
          .ExecuteNonQuery();
    }

    /// <summary>Per-agent totals over the last <paramref name="sinceDays"/> days, biggest spend first.</summary>
    public static IReadOnlyList<TokenUsage> Report(SqliteConnection db, int sinceDays = 7, string? agent = null)
    {
        var sql = """
            SELECT agent,
                   SUM(CASE WHEN kind = 'spend' THEN tokens ELSE 0 END) AS spent,
                   SUM(CASE WHEN kind = 'saved' THEN tokens ELSE 0 END) AS saved,
                   SUM(CASE WHEN kind = 'spend' THEN 1 ELSE 0 END) AS spend_events,
                   SUM(CASE WHEN kind = 'saved' THEN 1 ELSE 0 END) AS save_events
            FROM token_events
            WHERE created_at >= $cutoff
            """;
        if (!string.IsNullOrWhiteSpace(agent)) sql += " AND agent = $agent";
        sql += """
             GROUP BY agent
             ORDER BY spent DESC, saved DESC, agent
            """;

        var cmd = string.IsNullOrWhiteSpace(agent)
            ? db.Prepare(sql, "$cutoff")
            : db.Prepare(sql, "$cutoff", "$agent");
        var cutoff = CutoffIso(sinceDays);
        if (string.IsNullOrWhiteSpace(agent)) cmd.SetAll(cutoff); else cmd.SetAll(cutoff, agent.Trim());

        return cmd.Query(r => new TokenUsage(
            r.GetString("agent"),
            r.GetInt64("spent"),
            r.GetInt64("saved"),
            r.GetInt32("spend_events"),
            r.GetInt32("save_events")));
    }

    /// <summary>Per-thread totals (both kinds summed separately), biggest first — where the tokens actually went.</summary>
    public static IReadOnlyList<(long ThreadId, long Spent, long Saved)> ByThread(SqliteConnection db, int sinceDays = 7)
    {
        return db.Prepare("""
            SELECT thread_id,
                   SUM(CASE WHEN kind = 'spend' THEN tokens ELSE 0 END) AS spent,
                   SUM(CASE WHEN kind = 'saved' THEN tokens ELSE 0 END) AS saved
            FROM token_events
            WHERE created_at >= $cutoff AND thread_id IS NOT NULL
            GROUP BY thread_id
            ORDER BY spent DESC, saved DESC
            LIMIT 10
            """, "$cutoff")
          .Set("$cutoff", CutoffIso(sinceDays))
          .Query(r => (r.GetInt64("thread_id"), r.GetInt64("spent"), r.GetInt64("saved")));
    }

    /// <summary>Recent individual events, newest first.</summary>
    public static IReadOnlyList<(string Agent, string Kind, long Tokens, string Source, string CreatedAt)>
        Recent(SqliteConnection db, int limit = 20)
    {
        return db.Prepare("""
            SELECT agent, kind, tokens, source, created_at
            FROM token_events
            ORDER BY id DESC
            LIMIT $n
            """, "$n")
          .Set("$n", limit)
          .Query(r => (r.GetString("agent"), r.GetString("kind"), r.GetInt64("tokens"),
              r.GetString("source"), r.GetString("created_at")));
    }

    /// <summary>ISO cutoff for "the last N days" in the same shape the schema's defaults produce.</summary>
    public static string CutoffIso(int sinceDays) =>
        DateTime.UtcNow.AddDays(-Math.Max(0, sinceDays)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
