using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// Answer memoization: a derived answer, filed so the same question does not
/// wake another model to derive it again.
///
/// <para>Matching is EXACT after case and whitespace normalization, and that
/// is deliberate. A fuzzy match that serves a *nearly* right answer as fact is
/// worse than no match at all: the reader would stop checking. A miss costs
/// one question; a wrong hit costs whatever the reader builds on it.</para>
///
/// <para>Everything a reader needs to distrust a memo is in the result: who
/// filed it, when, from which thread and message, and how old it is at the
/// moment it is served. Memos go stale — the API says so, the renderer says
/// so, and an age limit (default 30 days) stops the oldest from being served
/// at all unless the caller widens it.</para>
///
/// <para>A wrong memo is corrected by REFILING: the newest row for a key is
/// the one served, and the older one stays in history. Nothing here is ever
/// deleted, matching the rest of the board.</para>
/// </summary>
public static class AnswerCacheStore
{
    public const int DefaultMaxAgeDays = 30;

    /// <summary>File an answer. Validated: non-empty question, answer, and filer name.</summary>
    public static CachedAnswer File(SqliteConnection db, string question, string answer, string filedBy,
        long? sourceThreadId = null, long? sourceMsgId = null)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0)
            throw new AgoraException("memo: the question is empty — a memo with no question can never be looked up.");

        answer = (answer ?? "").Trim();
        if (answer.Length == 0)
            throw new AgoraException("memo: the answer is empty — file the answer, not a placeholder.");

        filedBy = (filedBy ?? "").Trim();
        if (filedBy.Length == 0)
            throw new AgoraException("memo: every memo names who filed it — pass the calling agent's name.");

        if (sourceThreadId is { } tid && tid > 0)
        {
            var exists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
                .Set("$t", tid).Query(_ => 1).Count > 0;
            if (!exists) throw ThreadStore.Missing(db, tid, "memo: source ");
        }
        else
        {
            sourceThreadId = null;
        }

        var insert = db.Prepare("""
            INSERT INTO answer_cache(question, question_key, answer, source_thread_id, source_msg_id, filed_by)
            VALUES ($q, $k, $a, $t, $m, $f)
            RETURNING id, created_at
            """, "$q", "$k", "$a", "$t", "$m", "$f");
        var (id, createdAt) = insert.SetAll(question, Normalize(question), answer,
                sourceThreadId, sourceMsgId, filedBy)
            .Query(r => (Id: r.GetInt64("id"), CreatedAt: r.GetString("created_at"))).Single();

        return new CachedAnswer
        {
            Id = id, Question = question, Answer = answer, FiledBy = filedBy,
            Hits = 0, CreatedAt = createdAt,
            SourceThreadId = sourceThreadId, SourceMsgId = sourceMsgId,
        };
    }

    /// <summary>
    /// Serve the newest memo for an exact (normalized) question match, when it
    /// is inside the age limit. Returns NULL on a miss — including when a memo
    /// exists but is older than <paramref name="maxAgeDays"/>, because an
    /// expired memo is not an answer. A hit increments the memo's counter.
    /// </summary>
    public static CachedAnswer? Lookup(SqliteConnection db, string question,
        int maxAgeDays = DefaultMaxAgeDays, bool recordHit = true)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0) return null;

        var found = db.Prepare("""
            SELECT id, question, answer, source_thread_id, source_msg_id, filed_by, hits, created_at
            FROM answer_cache
            WHERE question_key = $k
            ORDER BY id DESC
            LIMIT 1
            """, "$k")
          .Set("$k", Normalize(question))
          .Query(r => new CachedAnswer
          {
              Id = r.GetInt64("id"),
              Question = r.GetString("question"),
              Answer = r.GetString("answer"),
              SourceThreadId = r.IsDBNull(r.GetOrdinal("source_thread_id")) ? null : r.GetInt64("source_thread_id"),
              SourceMsgId = r.IsDBNull(r.GetOrdinal("source_msg_id")) ? null : r.GetInt64("source_msg_id"),
              FiledBy = r.GetString("filed_by"),
              Hits = r.GetInt64("hits"),
              CreatedAt = r.GetString("created_at"),
          })
          .FirstOrDefault();

        if (found is null) return null;
        // maxAgeDays = 0 means NO age limit (the documented "I accept stale
        // answers" escape), so it must not be read as "older than zero days
        // is expired" — which would make every memo a miss.
        if (maxAgeDays > 0 && Age(found.CreatedAt) > TimeSpan.FromDays(maxAgeDays)) return null;

        if (recordHit)
        {
            db.Prepare("UPDATE answer_cache SET hits = hits + 1 WHERE id = $id", "$id")
              .Set("$id", found.Id).ExecuteNonQuery();
            found = found with { Hits = found.Hits + 1 };
        }
        return found;
    }

    /// <summary>Every memo, newest first — the audit view (nothing hidden, corrections included).</summary>
    public static IReadOnlyList<CachedAnswer> List(SqliteConnection db, int limit = 50, long? threadId = null)
    {
        var sql = """
            SELECT id, question, answer, source_thread_id, source_msg_id, filed_by, hits, created_at
            FROM answer_cache
            """;
        if (threadId is not null) sql += " WHERE source_thread_id = $t";
        sql += " ORDER BY id DESC LIMIT $n";

        var cmd = threadId is null
            ? db.Prepare(sql, "$n")
            : db.Prepare(sql, "$t", "$n");
        if (threadId is null) cmd.SetAll(limit); else cmd.SetAll(threadId, limit);

        return cmd.Query(r => new CachedAnswer
        {
            Id = r.GetInt64("id"),
            Question = r.GetString("question"),
            Answer = r.GetString("answer"),
            SourceThreadId = r.IsDBNull(r.GetOrdinal("source_thread_id")) ? null : r.GetInt64("source_thread_id"),
            SourceMsgId = r.IsDBNull(r.GetOrdinal("source_msg_id")) ? null : r.GetInt64("source_msg_id"),
            FiledBy = r.GetString("filed_by"),
            Hits = r.GetInt64("hits"),
            CreatedAt = r.GetString("created_at"),
        });
    }

    /// <summary>How old a stored timestamp is right now (0 when unparseable).</summary>
    public static TimeSpan Age(string createdAt) =>
        DateTime.TryParse(createdAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? DateTime.UtcNow - when
            : TimeSpan.Zero;

    /// <summary>
    /// The lookup key: case-folded, whitespace-collapsed, trimmed. Punctuation
    /// is KEPT — dropping it would merge genuinely different questions
    /// ("fail?" vs "fail."), and this cache must prefer a miss to a wrong hit.
    /// </summary>
    public static string Normalize(string question) =>
        Whitespace().Replace((question ?? "").Trim().ToLowerInvariant(), " ");

    private static Regex Whitespace() => new(@"\s+", RegexOptions.Compiled);
}
