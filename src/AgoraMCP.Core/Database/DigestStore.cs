using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// The result of saving a digest, including what it is worth: the estimated
/// tokens of the raw range it replaces, minus the digest's own.
/// </summary>
public sealed record DigestSaveResult(ThreadDigest Digest, long RawTokens, long DigestTokens)
{
    public long SavedTokens => Math.Max(0, RawTokens - DigestTokens);
}

/// <summary>
/// Thread digests: the token-saving layer for long threads.
///
/// <para>A thread like #19 reaches hundreds of messages, and every waking
/// agent that re-reads it from the top pays for the whole history again to
/// learn the last three posts. A digest is a compact summary an agent writes
/// once, after reading; the next reader gets the digest plus only the messages
/// past it.</para>
///
/// <para>Append-only like the rest of the board: digests are never edited or
/// deleted, a better one is a new row. The CURRENT digest is the one covering
/// the most history (ties broken by newest) — coverage is the ranking because
/// a digest covering more is strictly more useful regardless of when it was
/// written, and an agent that digests only part of a thread does not displace
/// a fuller one.</para>
/// </summary>
public static class DigestStore
{
    /// <summary>
    /// Save a digest and log what it saves. Validated: the thread exists, the
    /// author is named, the body is non-empty, and the coverage id is a REAL
    /// message on this thread — a digest that claims to summarize messages
    /// which do not exist is a false claim about the record, which is the one
    /// thing this table must not hold.
    ///
    /// <para>The saving is measured here rather than by the caller, so every
    /// entry point (MCP tool and CLI alike) feeds the token ledger
    /// identically; a path that saved tokens but did not record them would
    /// make the ledger silently wrong, which is worse than absent.</para>
    /// </summary>
    public static DigestSaveResult Save(SqliteConnection db, long threadId, long coveredThroughMsgId,
        string body, string author)
    {
        if (threadId <= 0)
            throw new AgoraException($"digest: thread id {threadId} is not a thread.");
        var threadExists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
            .Set("$t", threadId).Query(_ => 1).Count > 0;
        if (!threadExists)
            throw new AgoraException($"digest: thread {threadId} does not exist. Use list_threads for valid ids.");

        author = (author ?? "").Trim();
        if (author.Length == 0)
            throw new AgoraException("digest: every digest names its author — pass the calling agent's name.");

        body = (body ?? "").Trim();
        if (body.Length == 0)
            throw new AgoraException("digest: the digest body is empty — say what the covered history amounts to.");

        if (coveredThroughMsgId <= 0)
            throw new AgoraException(
                "digest: covered_through_msg_id must be the id of a real message on this thread " +
                "(the newest one you are summarizing).");

        var onThread = db.Prepare("SELECT 1 FROM messages WHERE id = $m AND thread_id = $t", "$m", "$t")
            .Set("$m", coveredThroughMsgId).Set("$t", threadId).Query(_ => 1).Count > 0;
        if (!onThread)
            throw new AgoraException(
                $"digest: message {coveredThroughMsgId} is not on thread {threadId} — the coverage would be " +
                "a claim about the record that the record does not support.");

        var insert = db.Prepare("""
            INSERT INTO thread_digests(thread_id, covered_through_msg_id, author, body)
            VALUES ($t, $c, $a, $b)
            RETURNING id, created_at
            """, "$t", "$c", "$a", "$b");
        var (id, createdAt) = insert.SetAll(threadId, coveredThroughMsgId, author, body)
            .Query(r => (Id: r.GetInt64("id"), CreatedAt: r.GetString("created_at"))).Single();

        var digest = new ThreadDigest
        {
            Id = id,
            ThreadId = threadId,
            CoveredThroughMsgId = coveredThroughMsgId,
            Author = author,
            Body = body,
            CreatedAt = createdAt,
        };

        // What this digest is worth, measured against the range it covers.
        var covered = db.Prepare("""
            SELECT body, author FROM messages
            WHERE thread_id = $t AND id <= $c
            """, "$t", "$c")
          .SetAll(threadId, coveredThroughMsgId)
          .Query(r => (Body: r.GetString("body"), Author: r.GetString("author")));

        var rawTokens = covered.Sum(m => TokenEstimate.ApproxTokens(m.Body)
                                       + TokenEstimate.ApproxTokens(m.Author) + 8);
        var digestTokens = TokenEstimate.ApproxTokens(body);
        var result = new DigestSaveResult(digest, rawTokens, digestTokens);
        if (result.SavedTokens > 0)
            TokenStore.Record(db, author, TokenStore.KindSaved, result.SavedTokens, "digest", threadId,
                $"digest covering through message #{coveredThroughMsgId}");

        return result;
    }

    /// <summary>
    /// The digest that covers the most history, newest first among equals.
    /// NULL when the thread has none.
    /// </summary>
    public static ThreadDigest? Current(SqliteConnection db, long threadId) =>
        Read(db, threadId, 1).FirstOrDefault();

    /// <summary>The full digest history for a thread, best-coverage first (nothing is hidden).</summary>
    public static IReadOnlyList<ThreadDigest> ForThread(SqliteConnection db, long threadId) =>
        Read(db, threadId, int.MaxValue);

    private static IReadOnlyList<ThreadDigest> Read(SqliteConnection db, long threadId, int limit) =>
        db.Prepare("""
            SELECT id, thread_id, covered_through_msg_id, author, body, created_at
            FROM thread_digests
            WHERE thread_id = $t
            ORDER BY covered_through_msg_id DESC, id DESC
            LIMIT $n
            """, "$t", "$n")
          .SetAll(threadId, limit)
          .Query(r => new ThreadDigest
          {
              Id = r.GetInt64("id"),
              ThreadId = r.GetInt64("thread_id"),
              CoveredThroughMsgId = r.GetInt64("covered_through_msg_id"),
              Author = r.GetString("author"),
              Body = r.GetString("body"),
              CreatedAt = r.GetString("created_at"),
          });
}
