using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// The blackboard: questions, peer reviews, and the conversation around them.
///
/// <para>Rules ported from the memory-bank philosophy: messages are immutable
/// (corrections are new messages, so the review trail reads exactly as it
/// happened); threads are closed, never deleted; every write names its author.</para>
///
/// <para>Hot path = indexed point lookups only. The wait loop polls
/// messages(thread_id), presence looks up presence(last_heartbeat); FTS is for
/// history search and never runs inside a wait.</para>
/// </summary>
public static class ThreadStore
{
    public const string StatusOpen = "open";
    public const string StatusAnswered = "answered";
    public const string StatusClosed = "closed";

    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 600;
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Post a question: a new open thread plus its first message. Directed
    /// questions carry a target agent name; they surface in that agent's
    /// fetch_pending_queries by exact name match and are merely marked for
    /// everyone else.
    /// </summary>
    public static long PostQuestion(
        SqliteConnection db,
        string topic,
        string context,
        string author,
        string specialty = "",
        string directedTo = "",
        MessageCache? cache = null)
    {
        // Null-coalesced before Trim: a missing argument can arrive as null
        // (MCP optional parameters, or a client that sent the field as null),
        // and the refusal below must be an AgoraException naming the fix —
        // not a NullReferenceException the caller sees as an opaque tool
        // error (thread #23, 2026-09-18).
        topic = (topic ?? "").Trim();
        context = (context ?? "").Trim();
        author = (author ?? "").Trim();
        specialty = (specialty ?? "").Trim();
        directedTo = (directedTo ?? "").Trim();

        if (topic.Length == 0) throw new AgoraException("post_question needs a non-empty topic.");
        if (author.Length == 0) throw new AgoraException("every write needs an author — pass the calling agent's name (or set AGORA_AGENT so it is filled in automatically).");
        if (directedTo.Length > 0 && directedTo == author)
            throw new AgoraException($"cannot direct a question at yourself ('{author}'). Leave target_agent empty to ask the agora.");

        using var tx = db.BeginTransaction();

        var insertThread = db.Prepare("""
            INSERT INTO threads(topic, context, specialty, directed_to, created_by)
            VALUES ($topic, $context, $specialty, $directed_to, $author)
            RETURNING id
            """, "$topic", "$context", "$specialty", "$directed_to", "$author");
        insertThread.Transaction = tx;
        var threadId = insertThread.SetAll(topic, context, specialty, directedTo, author)
            .Query(r => r.GetInt64("id")).Single();

        var insertMessage = db.Prepare("""
            INSERT INTO messages(thread_id, author, kind, body)
            VALUES ($thread_id, $author, 'question', $body)
            RETURNING id, created_at
            """, "$thread_id", "$author", "$body");
        insertMessage.Transaction = tx;
        var (messageId, createdAt) = insertMessage.SetAll(threadId, author, topic)
            .Query(r => (Id: r.GetInt64("id"), CreatedAt: r.GetString("created_at"))).Single();

        AppendFts(db, tx, threadId, topic, context, topic);
        tx.Commit();

        cache?.Remember(new ThreadMessage
        {
            Id = messageId,
            ThreadId = threadId,
            Author = author,
            Kind = "question",
            Body = topic,
            CreatedAt = createdAt,
        });

        return threadId;
    }

    /// <summary>
    /// Append a peer review: the answer that marks a thread answered. Rejected
    /// on missing or closed threads (closed threads take reply, not review).
    /// </summary>
    public static ThreadMessage SubmitReview(
        SqliteConnection db, long threadId, string body, string author, MessageCache? cache = null) =>
        AddMessage(db, threadId, "review", body, author, cache);

    /// <summary>
    /// Append a follow-up: discussion on any live thread without moving its
    /// status. Allowed even on closed threads — closure ends the review, not
    /// the record.
    /// </summary>
    public static ThreadMessage Reply(
        SqliteConnection db, long threadId, string body, string author, MessageCache? cache = null) =>
        AddMessage(db, threadId, "reply", body, author, cache);

    /// <summary>
    /// Pending questions: open, past since_id, cheap (no bodies). Directed
    /// routing per PostQuestion, matched against every name the caller has
    /// used — mail addressed to a former name still reaches them, so renaming
    /// an agent does not silently silence its inbox.
    /// </summary>
    public static IReadOnlyList<ThreadSummary> FetchPending(
        SqliteConnection db, string agent = "", string specialty = "", long sinceId = 0, int limit = 20)
    {
        agent = agent.Trim();
        specialty = specialty.Trim();
        if (limit < 1) limit = 1;

        // The directed-routing clauses are built to match the ORIGINAL
        // semantics exactly, now spanning every name the caller has used:
        //   - no agent name: the caller is nobody, so "directed at me" is never
        //     true and "someone else's mail" is any directed thread — which is
        //     what makes an unaddressed call see the whole open board.
        //   - with a name: both clauses bind the caller's name list.
        // (A naive `NOT IN (NULL)` for the empty case would evaluate to NULL,
        // not TRUE, and silently drop every directed thread from the
        // unaddressed view — found by ThreadStoreTests.Fetch_pagination.)
        using var cmd = db.Prepare("""
            SELECT t.id, t.topic, t.specialty, t.status, t.directed_to, t.created_by, t.created_at, t.updated_at,
                   (SELECT COUNT(*) FROM messages m WHERE m.thread_id = t.id) AS message_count
            FROM threads t
            WHERE t.status = 'open'
              AND t.id > $since_id
              AND (
                    (t.directed_to = '' AND ($specialty = '' OR t.specialty = $specialty))
                    OR {DIRECTED_AT_ME}
                    OR (t.directed_to != '' AND {NOT_AT_ME} AND $specialty = '')
                  )
            ORDER BY t.id
            LIMIT $limit
            """, "$since_id", "$specialty", "$limit");
        cmd.Set("$since_id", sinceId).Set("$specialty", specialty).Set("$limit", limit);

        var hasAgent = agent.Length > 0;
        var names = hasAgent ? AgentIdentity.NameList(agent, cmd) : "";
        cmd.CommandText = cmd.CommandText
            .Replace("{DIRECTED_AT_ME}", hasAgent ? $"t.directed_to IN {names}" : "0")
            .Replace("{NOT_AT_ME}", hasAgent ? $"t.directed_to NOT IN {names}" : "1");

        return cmd.Query(r => ReadSummary(r));
    }

    /// <summary>One thread in full: header plus every message, oldest first.
    /// Message bodies come from the in-memory chat box when resident and are
    /// re-read from SQLite (re-warming the cache) when compacted out; the id
    /// list itself always comes from SQLite, so answers written by other agent
    /// processes are never missed.</summary>
    public static ThreadDetail ReadThread(SqliteConnection db, long threadId, MessageCache? cache = null)
    {
        var header = db.Prepare("""
            SELECT id, topic, context, specialty, status, directed_to, created_by, created_at, updated_at
            FROM threads WHERE id = $id
            """, "$id").Set("$id", threadId)
            .Query(ReadHeader)
            .FirstOrDefault();

        if (header is null)
            throw new AgoraException($"thread {threadId} does not exist. Use fetch_pending_queries or list_threads for valid ids.");

        // Ids only — the payload decision (memory vs database) is per message.
        var ids = db.Prepare("SELECT id FROM messages WHERE thread_id = $id ORDER BY id", "$id")
            .Set("$id", threadId)
            .Query(r => r.GetInt64("id"));

        var messages = new List<ThreadMessage>(ids.Count);
        foreach (var id in ids)
        {
            var message = HydrateMessage(db, cache, id);
            if (message is not null) messages.Add(message);
        }

        return new ThreadDetail { Header = header, Messages = messages };
    }

    /// <summary>Compact thread list for dashboards and history review.</summary>
    public static IReadOnlyList<ThreadSummary> ListThreads(
        SqliteConnection db, string status = "", string specialty = "", int limit = 50)
    {
        status = status.Trim();
        specialty = specialty.Trim();
        if (limit < 1) limit = 1;

        return db.Prepare("""
            SELECT t.id, t.topic, t.specialty, t.status, t.directed_to, t.created_by, t.created_at, t.updated_at,
                   (SELECT COUNT(*) FROM messages m WHERE m.thread_id = t.id) AS message_count
            FROM threads t
            WHERE ($status = '' OR t.status = $status) AND ($specialty = '' OR t.specialty = $specialty)
              AND (t.kind = 'thread' OR t.kind = '')
            ORDER BY t.id DESC
            LIMIT $limit
            """, "$status", "$specialty", "$limit")
           .SetAll(status, specialty, limit)
           .Query(r => ReadSummary(r));
    }

    /// <summary>
    /// Full-text search over topics, context, and bodies. Short keyword queries
    /// work best; terms are ANDed as phrases, with an automatic OR fallback so a
    /// miss degrades to ranked partial matches instead of silence.
    /// </summary>
    public static IReadOnlyList<SearchHit> Search(SqliteConnection db, string query, int limit = 10)
    {
        query = query.Trim();
        if (query.Length == 0) throw new AgoraException("search needs a query — a few keywords, not a sentence.");

        limit = Math.Clamp(limit, 1, 50);
        var anded = FtsQuery(query, " AND ");
        var hits = SearchOnce(db, anded, limit);
        if (hits.Count == 0)
        {
            var ored = FtsQuery(query, " OR ");
            if (ored != anded) hits = SearchOnce(db, ored, limit);
        }
        return hits;
    }

    private static List<SearchHit> SearchOnce(SqliteConnection db, string match, int limit) =>
        db.Prepare("""
            SELECT t.id, t.topic, t.specialty, t.status, t.directed_to, t.created_by, t.created_at, t.updated_at,
                   (SELECT COUNT(*) FROM messages m WHERE m.thread_id = t.id) AS message_count,
                   snippet(thread_fts, 2, '[', ']', ' … ', 16) AS snip
            FROM thread_fts f
            JOIN threads t ON t.id = f.thread_id
            WHERE thread_fts MATCH $match
            ORDER BY bm25(thread_fts)
            LIMIT $limit
            """, "$match", "$limit")
           .SetAll(match, limit)
           .Query(r => new SearchHit(ReadSummary(r), r.GetString("snip")));

    /// <summary>Every term becomes a quoted phrase (identifiers match exactly); joined by the given operator.</summary>
    private static string FtsQuery(string query, string op) => string.Join(op,
        query.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\""));

    // ---- bounded wait (post_question with wait=true) ----

    /// <summary>Every wait ends in a terminal answer — answered, no_peer_online, or timeout. Never silence.</summary>
    public static int ClampTimeoutSeconds(int seconds) => Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>Knobs for the server-side wait. Tests shrink the interval and TTL; production uses these defaults.</summary>
    public sealed record WaitOptions
    {
        /// <summary>The poster — excluded from the presence check, so waiting for yourself never happens.</summary>
        public string Author { get; init; } = "";
        /// <summary>The specialty a peer must cover for the wait to be worth starting.</summary>
        public string Specialty { get; init; } = "";
        /// <summary>When set, only this exact agent satisfies the presence check.</summary>
        public string TargetAgent { get; init; } = "";
        public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
        public int TtlSeconds { get; init; } = PresenceStore.DefaultTtlSeconds;
        /// <summary>Poll slices are indexed point lookups; ~1s keeps cancellation responsive at negligible cost.</summary>
        public double PollIntervalSeconds { get; init; } = 1.0;
    }

    /// <summary>
    /// Wait for a peer to answer a just-posted question.
    ///
    /// <para>First a presence check: with no eligible peer online the wait ends
    /// immediately with no_peer_online plus the full live roster, so the caller
    /// can substitute on the spot instead of hanging. Otherwise the loop sleeps
    /// in ~1s slices and polls for NEW answer ids only — an index-only scan that
    /// touches no payloads while nothing has arrived. Each poll slice costs one
    /// O(log n) lookup; when an id appears, its body is fetched (memory first,
    /// then SQLite) and returned.</para>
    /// </summary>
    public static async Task<WaitResult> WaitForAnswerAsync(
        SqliteConnection db, long threadId, WaitOptions options, CancellationToken cancellationToken = default,
        MessageCache? cache = null)
    {
        var timeout = ClampTimeoutSeconds(options.TimeoutSeconds);

        // Presence check excluding self. A directed question needs that exact
        // agent; a specialty question needs any peer covering the specialty;
        // an open question needs any peer at all.
        var roster = PresenceStore.OnlineRoster(db, ttlSeconds: options.TtlSeconds);
        var peers = roster.Where(p => p.Agent != options.Author).ToList();
        var peerAvailable = options.TargetAgent.Length > 0
            ? peers.Any(p => p.Agent == options.TargetAgent)
            : options.Specialty.Length > 0
                ? peers.Any(p => p.Specialty == options.Specialty)
                : peers.Count > 0;

        if (!peerAvailable)
            return new WaitResult
            {
                ThreadId = threadId,
                Status = WaitStatus.NoPeerOnline,
                Messages = [],
                OnlineNow = roster,
            };

        // Index-only id scan, one prepared statement rebound per slice — the
        // hot path stays payload-free until an answer actually lands.
        using var poll = db.Prepare("""
            SELECT id FROM messages
            WHERE thread_id = $thread_id AND kind IN ('review', 'reply') AND id > $after_id
            ORDER BY id
            """, "$thread_id", "$after_id");

        var collected = new List<ThreadMessage>();
        var afterId = 0L;
        var deadline = DateTime.UtcNow.AddSeconds(timeout);

        while (true)
        {
            var newIds = poll.Set("$thread_id", threadId).Set("$after_id", afterId).Query(r => r.GetInt64("id"));
            if (newIds.Count > 0)
            {
                foreach (var id in newIds)
                {
                    var message = HydrateMessage(db, cache, id);
                    if (message is not null) collected.Add(message);
                    if (id > afterId) afterId = id;
                }
                return new WaitResult { ThreadId = threadId, Status = WaitStatus.Answered, Messages = collected };
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new WaitResult { ThreadId = threadId, Status = WaitStatus.Timeout, Messages = collected };

            await Task.Delay(remaining < TimeSpan.FromSeconds(options.PollIntervalSeconds)
                ? remaining
                : TimeSpan.FromSeconds(options.PollIntervalSeconds), cancellationToken);
        }
    }

    /// <summary>What the blackboard holds right now, and how much of the chat is resident in memory.</summary>
    public sealed record Stats(
        int ThreadsOpen, int ThreadsAnswered, int ThreadsClosed, int Messages, int AgentsOnline, string DbPath,
        int CachedMessages = 0, long CacheBytes = 0, long CacheBudgetBytes = 0);

    public static Stats GetStats(SqliteConnection db, string dbPath, int ttlSeconds = PresenceStore.DefaultTtlSeconds,
        MessageCache? cache = null)
    {
        var (cached, bytes) = cache?.Usage ?? (0, 0L);
        return new Stats(
            ThreadsOpen: CountThreads(db, StatusOpen),
            ThreadsAnswered: CountThreads(db, StatusAnswered),
            ThreadsClosed: CountThreads(db, StatusClosed),
            Messages: (int)db.Scalar<long>("SELECT COUNT(*) FROM messages"),
            AgentsOnline: PresenceStore.OnlineRoster(db, ttlSeconds: ttlSeconds).Count,
            DbPath: dbPath,
            CachedMessages: cached,
            CacheBytes: bytes,
            CacheBudgetBytes: cache?.BudgetBytes ?? 0);
    }

    private static int CountThreads(SqliteConnection db, string status) =>
        db.Prepare("SELECT COUNT(*) FROM threads WHERE status = $status", "$status")
          .Set("$status", status)
          .Query(r => r.GetInt32("COUNT(*)")).Single();

    // ---- internals ----

    private static ThreadMessage AddMessage(
        SqliteConnection db, long threadId, string kind, string? body, string? author, MessageCache? cache = null)
    {
        // Same boundary rule as PostQuestion: coalesce before Trim so the
        // author check below is reachable — author.Trim() on a null used to
        // crash one line early and the friendly message never ran (thread #23,
        // 2026-09-18: every claude-opus write failed this way after the
        // republish).
        body = (body ?? "").Trim();
        author = (author ?? "").Trim();
        if (body.Length == 0) throw new AgoraException($"a {kind} needs a non-empty body.");
        if (author.Length == 0) throw new AgoraException("every write needs an author — pass the calling agent's name (or set AGORA_AGENT so it is filled in automatically).");

        var threadStatus = db.Prepare("SELECT status FROM threads WHERE id = $id", "$id")
            .Set("$id", threadId)
            .Query(r => r.GetString("status"))
            .FirstOrDefault();

        if (threadStatus is null)
            throw new AgoraException($"thread {threadId} does not exist. Use fetch_pending_queries or list_threads for valid ids.");
        if (kind == "review" && threadStatus == StatusClosed)
            throw new AgoraException($"thread {threadId} is closed — closed threads take reply, not review.");

        using var tx = db.BeginTransaction();

        var insert = db.Prepare("""
            INSERT INTO messages(thread_id, author, kind, body)
            VALUES ($thread_id, $author, $kind, $body)
            RETURNING id, created_at
            """, "$thread_id", "$author", "$kind", "$body");
        insert.Transaction = tx;
        var (messageId, createdAt) = insert.SetAll(threadId, author, kind, body)
            .Query(r => (Id: r.GetInt64("id"), CreatedAt: r.GetString("created_at"))).Single();

        // A review moves the thread to answered; a reply leaves status alone.
        // Either way the thread's updated_at moves — that is its heartbeat.
        var newStatus = kind == "review" ? StatusAnswered : threadStatus;
        var update = db.Prepare("UPDATE threads SET status = $status, updated_at = $now WHERE id = $id",
            "$status", "$now", "$id");
        update.Transaction = tx;
        update.SetAll(newStatus, AgoraDatabase.Now(), threadId).ExecuteNonQuery();

        AppendFts(db, tx, threadId, "", "", body);
        tx.Commit();

        var message = new ThreadMessage
        {
            Id = messageId,
            ThreadId = threadId,
            Author = author,
            Kind = kind,
            Body = body,
            CreatedAt = createdAt,
        };
        cache?.Remember(message);
        return message;
    }

    /// <summary>
    /// The per-message read path: memory first, database on a miss, and the
    /// miss re-warms the cache. Correct because bodies are immutable; fresh
    /// because the id list always comes from SQLite.
    /// </summary>
    private static ThreadMessage? HydrateMessage(SqliteConnection db, MessageCache? cache, long id)
    {
        if (cache is not null && cache.TryGet(id, out var cached)) return cached;

        var message = db.Prepare("""
            SELECT id, thread_id, author, kind, body, created_at
            FROM messages WHERE id = $id
            """, "$id").Set("$id", id)
            .Query(ReadMessage)
            .FirstOrDefault();

        if (message is not null) cache?.Remember(message);
        return message;
    }

    /// <summary>Index a posted body for history search. Append-only: FTS never sits on the wait path.</summary>
    private static void AppendFts(
        SqliteConnection db, SqliteTransaction tx, long threadId, string topic, string context, string body)
    {
        var cmd = db.Prepare("INSERT INTO thread_fts(topic, context, body, thread_id) VALUES ($t, $c, $b, $id)",
            "$t", "$c", "$b", "$id");
        cmd.Transaction = tx;
        cmd.SetAll(topic, context, body, threadId).ExecuteNonQuery();
    }

    private static ThreadHeader ReadHeader(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        Topic = r.GetString("topic"),
        Context = r.GetString("context"),
        Specialty = r.GetString("specialty"),
        Status = r.GetString("status"),
        DirectedTo = r.GetString("directed_to"),
        CreatedBy = r.GetString("created_by"),
        CreatedAt = r.GetString("created_at"),
        UpdatedAt = r.GetString("updated_at"),
    };

    private static ThreadSummary ReadSummary(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        Topic = r.GetString("topic"),
        Specialty = r.GetString("specialty"),
        Status = r.GetString("status"),
        DirectedTo = r.GetString("directed_to"),
        CreatedBy = r.GetString("created_by"),
        MessageCount = r.GetInt32("message_count"),
        CreatedAt = r.GetString("created_at"),
        UpdatedAt = r.GetString("updated_at"),
    };

    private static ThreadMessage ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetInt64("id"),
        ThreadId = r.GetInt64("thread_id"),
        Author = r.GetString("author"),
        Kind = r.GetString("kind"),
        Body = r.GetString("body"),
        CreatedAt = r.GetString("created_at"),
    };
}
