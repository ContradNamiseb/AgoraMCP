using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgoraMCP.Database;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// One board event pushed to subscribers. The durable truth stays the
/// messages table (append-only, id-ordered); this envelope is the push
/// HINT over it: a subscriber that misses events replays from SQLite with
/// ?since=, so a dropped connection is a latency event, never a loss.
/// </summary>
public sealed record BoardEvent(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("thread")] long ThreadId,
    [property: JsonPropertyName("msg")] long MsgId,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("directed_to")] string DirectedTo,
    [property: JsonPropertyName("at")] string CreatedAt,
    [property: JsonPropertyName("body")] string Body = "",
    [property: JsonPropertyName("turn_seq"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TurnSeq = null);

/// <summary>
/// In-binary push fan-out: a resident loopback SSE hub. No scripts, no
/// broker, no new store — the messages table is the durable log, the hub
/// only delivers hints over it.
///
/// <para>Why this shape: MCP-over-stdio (how every agent reaches the hub)
/// cannot server-push without a client-held stream, and SQLite has no
/// cross-process notify channel — so the hub holds the client connections
/// itself (GET /events, text/event-stream) and polls the append-only log
/// at 1s. Poll interval bounds LATENCY ONLY: correctness comes from the
/// ?since= replay cursor, which re-reads SQLite on every (re)connect.
/// Presence-gating is implicit — a connected subscriber is online by
/// definition; offline agents catch up from the log when they subscribe.
/// </para>
/// </summary>
public static class NotifyHub
{
    public const int DefaultPort = 48721;

    public sealed record Options
    {
        public required string DbPath { get; init; }
        public int Port { get; init; } = DefaultPort;
        public int PollMs { get; init; } = 1000;
    }

    private sealed class Subscriber
    {
        public required string Agent { get; init; }
        public long Cursor;
        public BlockingCollection<string> Queue { get; } = new(new ConcurrentQueue<string>());
    }

    public static async Task<int> RunAsync(Options options, CancellationToken token)
    {
        // Raw TCP loopback, NOT HttpListener: HttpListener rides HTTP.SYS on
        // Windows and refuses non-admin prefixes without a netsh urlacl
        // reservation. A minimal hand-rolled SSE response over TcpListener
        // needs no privileges, no packages, and behaves identically on
        // Linux/macOS. Clients are equally simple (curl, HttpClient).
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"hub: cannot listen on 127.0.0.1:{options.Port} ({e.Message}).");
            return 1;
        }
        Console.WriteLine($"hub: serving SSE on http://127.0.0.1:{options.Port}/events (db {options.DbPath})");
        var subscribers = new ConcurrentDictionary<Guid, Subscriber>();

        // Agora thread 19/38 (user-directed), Tier 1: native desktop toast,
        // independent of whether any SSE subscriber is connected -- see
        // DesktopToast's own doc comment for why this must fail soft.
        var toast = await DesktopToast.TryCreateAsync();
        var pump = PumpLoopAsync(options, subscribers, toast, token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = HandleAsync(client, options, subscribers, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            foreach (var sub in subscribers.Values) sub.Queue.CompleteAdding();
        }
        await pump;
        if (toast is not null) await toast.DisposeAsync();
        return 0;
    }

    private static async Task HandleAsync(TcpClient client, Options options,
        ConcurrentDictionary<Guid, Subscriber> subscribers, CancellationToken token)
    {
        var id = Guid.NewGuid();
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(token) ?? "";
                string? line;
                var query = "";
                var parts = requestLine.Split(' ');
                if (parts.Length >= 2)
                {
                    var qi = parts[1].IndexOf('?');
                    var path = qi >= 0 ? parts[1][..qi] : parts[1];
                    query = qi >= 0 ? parts[1][(qi + 1)..] : "";
                    if (parts[0] != "GET" || path != "/events")
                    {
                        await WriteRawAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token);
                        return;
                    }
                }
                else
                {
                    return;
                }
                // Drain request headers.
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token))) { }
                var q = ParseQuery(query);
                var agent = q.GetValueOrDefault("agent", "");
                var since = long.TryParse(q.GetValueOrDefault("since", "0"), out var s) ? s : 0;
                var sub = new Subscriber { Agent = agent.Trim(), Cursor = since };
                subscribers[id] = sub;
                // Immediate catch-up replay from the durable log. The cursor
                // MUST advance past replayed rows here: the pump delivers
                // everything above the cursor, so replaying without advancing
                // double-delivers the whole window (observed live: one post,
                // two deliveries with the same seq). Replay sets the floor;
                // the pump owns everything above it from here on.
                foreach (var (replayLine, replaySeq, _, _, _) in ReadSince(options.DbPath, since, agent))
                {
                    sub.Queue.Add(replayLine);
                    if (replaySeq > sub.Cursor) sub.Cursor = replaySeq;
                }
                await WriteRawAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nX-Accel-Buffering: no\r\nConnection: keep-alive\r\n\r\n",
                    token);
                await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = false };
                var lastPing = DateTime.UtcNow;
                while (!token.IsCancellationRequested)
                {
                    string? item = null;
                    try
                    {
                        if (!sub.Queue.TryTake(out item, 15000, token)) item = null;
                    }
                    catch (OperationCanceledException) { break; }
                    try
                    {
                        if (item is not null)
                        {
                            await writer.WriteAsync($"data: {item}\n\n");
                        }
                        else if (DateTime.UtcNow - lastPing > TimeSpan.FromSeconds(15))
                        {
                            await writer.WriteAsync(":ping\n\n");
                            lastPing = DateTime.UtcNow;
                        }
                        else
                        {
                            continue;
                        }
                        await writer.FlushAsync();
                    }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                    {
                        break; // subscriber gone; resumes via ?since=
                    }
                }
                subscribers.TryRemove(id, out _);
                sub.Queue.CompleteAdding();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Accept aborted, client vanished mid-handshake, or shutdown:
            // drop it; catch-up reads cover any gap.
        }
        finally
        {
            subscribers.TryRemove(id, out _);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) { map[Uri.UnescapeDataString(pair)] = ""; continue; }
            map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return map;
    }

    private static Task WriteRawAsync(NetworkStream stream, string text, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length, token);
    }

    private static async Task PumpLoopAsync(Options options,
        ConcurrentDictionary<Guid, Subscriber> subscribers, DesktopToast? toast, CancellationToken token)
    {
        // Agora thread 19/38: the toast cursor is independent of every
        // subscriber's cursor and of subscriber presence -- the old
        // `if (subscribers.IsEmpty) continue` skipped this whole tick,
        // including any DB read, whenever nobody happened to be tailing
        // the SSE stream, which is exactly the case a toast exists to
        // cover. It starts at "now" (current max id), not 0, so a freshly
        // started hub doesn't toast the entire historical backlog on its
        // first tick -- only mail that lands after the hub comes up.
        var toastCursor = ReadMaxSeq(options.DbPath);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.PollMs, token);
                var floor = subscribers.IsEmpty
                    ? toastCursor
                    : Math.Min(toastCursor, subscribers.Values.Min(s => s.Cursor));
                var rows = ReadSince(options.DbPath, floor, "");
                foreach (var (line, seq, to, author, topic) in rows)
                {
                    // One toast per genuinely new DIRECTED row, regardless of
                    // whether any subscriber is connected -- gated on
                    // toastCursor specifically, not any subscriber's cursor,
                    // so it never double-fires per subscriber and never
                    // depends on one being present.
                    if (toast is not null && seq > toastCursor && to.Length > 0)
                    {
                        _ = toast.ShowAsync($"Agora → {to}", $"{author}: {topic}");
                    }
                    foreach (var sub in subscribers.Values)
                    {
                        if (seq <= sub.Cursor) continue;
                        if (sub.Agent.Length > 0 && to.Length > 0 && !AgentIdentity.AnswersTo(sub.Agent, to)) continue;
                        sub.Queue.Add(line);
                        sub.Cursor = seq;
                    }
                }
                if (rows.Count > 0) toastCursor = rows[^1].Seq;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Console.Error.WriteLine($"hub: pump error ({e.Message}) — retrying; catch-up reads cover the gap.");
            }
        }
    }

    internal static long ReadMaxSeq(string dbPath)
    {
        using var db = AgoraDatabase.Open(dbPath);
        return db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM messages");
    }

    internal static long ReadMaxTurnSeq(string dbPath)
    {
        using var db = AgoraDatabase.Open(dbPath);
        return db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM turn_events");
    }

    /// <summary>
    /// Computes the true recipient of a message:
    /// - On DM threads (one shared thread per pair, where threads.directed_to
    ///   stays frozen as the initial recipient), the recipient is whichever party
    ///   (created_by or directed_to) is NOT the message author.
    /// - On board threads, an explicit turn pass to another agent wins (even on
    ///   a directed thread handed off to a third reviewer).
    /// - Otherwise, on directed board threads: the asker's posts go to
    ///   directed_to, and directed_to's replies route back to created_by.
    /// </summary>
    internal static string EffectiveRecipient(
        string threadKind, string createdBy, string directedTo, string turnHolder, string author)
    {
        if (string.Equals(threadKind, DmStore.ThreadKind, StringComparison.OrdinalIgnoreCase))
        {
            if (AgentIdentity.AnswersTo(createdBy, author)) return directedTo;
            if (AgentIdentity.AnswersTo(directedTo, author)) return createdBy;
            return directedTo;
        }
        if (turnHolder.Length > 0 && !AgentIdentity.AnswersTo(turnHolder, author))
            return turnHolder;
        if (directedTo.Length > 0)
        {
            if (AgentIdentity.AnswersTo(directedTo, author)) return createdBy;
            return directedTo;
        }
        return "";
    }

    internal static (List<BoardEvent> Events, long MaxTurnSeq) ReadTurnEventsSince(
        string dbPath,
        long sinceTurnId,
        string agent,
        bool excludeSelf = true)
    {
        using var db = AgoraDatabase.Open(dbPath);
        var rows = db.Prepare("""
            SELECT te.id, te.thread_id, te.from_agent, te.to_agent,
                   te.from_role, te.to_role, te.stage, te.instruction, te.created_at,
                   t.topic
            FROM turn_events te
            JOIN threads t ON t.id = te.thread_id
            WHERE te.id > $since
            ORDER BY te.id
            LIMIT 500
            """, "$since")
            .Set("$since", sinceTurnId)
            .Query(r => (
                Id: r.GetInt64("id"),
                ThreadId: r.GetInt64("thread_id"),
                FromAgent: r.GetString("from_agent"),
                ToAgent: r.GetString("to_agent"),
                FromRole: r.GetString("from_role"),
                ToRole: r.GetString("to_role"),
                Stage: r.GetString("stage"),
                Instruction: r.GetString("instruction"),
                At: r.GetString("created_at"),
                Topic: r.GetString("topic")));

        var maxTurn = sinceTurnId;
        var events = new List<BoardEvent>();
        var target = agent.Trim();
        foreach (var r in rows)
        {
            if (r.Id > maxTurn) maxTurn = r.Id;
            if (target.Length > 0)
            {
                if (excludeSelf && AgentIdentity.AnswersTo(target, r.FromAgent)) continue;
                if (!AgentIdentity.AnswersTo(target, r.ToAgent)) continue;
            }

            var meta = new List<string>();
            if (r.ToRole.Length > 0) meta.Add($"role={r.ToRole}");
            if (r.Stage.Length > 0) meta.Add($"stage={r.Stage}");
            var prefix = meta.Count > 0 ? $"[turn passed ({string.Join(", ", meta)})] " : "[turn passed] ";
            var body = prefix + r.Instruction;

            events.Add(new BoardEvent(
                Seq: 0,
                Kind: "turn_pass",
                ThreadId: r.ThreadId,
                MsgId: 0,
                Author: r.FromAgent,
                Topic: r.Topic,
                DirectedTo: r.ToAgent,
                CreatedAt: r.At,
                Body: body,
                TurnSeq: r.Id));
        }
        return (events, maxTurn);
    }

    /// <summary>
    /// Reads both new <c>messages</c> and (when not <paramref name="dmsOnly"/>)
    /// new <c>turn_events</c> since their respective cursors. Deduplicates a
    /// turn pass that landed in the same batch as a message already emitted for
    /// the same (thread, author, recipient) so <c>respond(body + pass_to)</c>
    /// never double-wakes, while body-less turn passes and delayed turn passes
    /// always emit a live <c>turn_pass</c> event.
    /// </summary>
    internal static (List<BoardEvent> Events, long MaxMsgSeq, long MaxTurnSeq) ReadCombinedSince(
        string dbPath,
        long sinceMsgId,
        long sinceTurnId,
        string agent,
        bool directedOnly = false,
        bool dmsOnly = false,
        bool excludeSelf = false)
    {
        var (msgEvents, maxMsg) = ReadEventsSince(
            dbPath, sinceMsgId, agent, directedOnly, dmsOnly, excludeSelf);

        if (dmsOnly)
            return (msgEvents, maxMsg, sinceTurnId);

        var (turnEvents, maxTurn) = ReadTurnEventsSince(
            dbPath, sinceTurnId, agent, excludeSelf);

        if (turnEvents.Count == 0)
            return (msgEvents, maxMsg, maxTurn);

        foreach (var te in turnEvents)
        {
            var coveredByMessage = msgEvents.Any(m =>
                m.ThreadId == te.ThreadId &&
                AgentIdentity.AnswersTo(m.Author, te.Author) &&
                AgentIdentity.AnswersTo(m.DirectedTo, te.DirectedTo));
            if (!coveredByMessage)
                msgEvents.Add(te with { Seq = maxMsg });
        }

        return (msgEvents, maxMsg, maxTurn);
    }

    internal static (List<BoardEvent> Events, long MaxSeq) ReadEventsSince(
        string dbPath,
        long since,
        string agent,
        bool directedOnly = false,
        bool dmsOnly = false,
        bool excludeSelf = false)
    {
        using var db = AgoraDatabase.Open(dbPath);
        var rows = db.Prepare("""
            SELECT m.id, m.thread_id, m.author, m.kind, m.body,
                   t.topic, t.created_by, t.directed_to, t.kind AS thread_kind,
                   COALESCE((SELECT te.to_agent FROM turn_events te WHERE te.thread_id = t.id ORDER BY te.id DESC LIMIT 1), '') AS turn_holder,
                   m.created_at
            FROM messages m
            JOIN threads t ON t.id = m.thread_id
            WHERE m.id > $since
            ORDER BY m.id
            LIMIT 500
            """, "$since")
            .Set("$since", since)
            .Query(r => (
                Seq: r.GetInt64("id"),
                ThreadId: r.GetInt64("thread_id"),
                Author: r.GetString("author"),
                Kind: r.GetString("kind"),
                Body: r.GetString("body"),
                Topic: r.GetString("topic"),
                CreatedBy: r.GetString("created_by"),
                DirectedTo: r.GetString("directed_to"),
                ThreadKind: r.GetString("thread_kind"),
                TurnHolder: r.GetString("turn_holder"),
                At: r.GetString("created_at")));

        var maxSeq = since;
        var events = new List<BoardEvent>();
        var target = agent.Trim();
        foreach (var r in rows)
        {
            if (r.Seq > maxSeq) maxSeq = r.Seq;
            var isDm = string.Equals(r.ThreadKind, DmStore.ThreadKind, StringComparison.OrdinalIgnoreCase);
            if (dmsOnly && !isDm) continue;

            var effectiveTo = EffectiveRecipient(r.ThreadKind, r.CreatedBy, r.DirectedTo, r.TurnHolder, r.Author);
            if (target.Length > 0)
            {
                if (excludeSelf && AgentIdentity.AnswersTo(target, r.Author)) continue;
                if (directedOnly)
                {
                    if (effectiveTo.Length == 0 || !AgentIdentity.AnswersTo(target, effectiveTo)) continue;
                }
                else
                {
                    if (effectiveTo.Length > 0 && !AgentIdentity.AnswersTo(target, effectiveTo)) continue;
                }
            }

            var kind = isDm ? DmStore.MessageKind : r.Kind;
            events.Add(new BoardEvent(r.Seq, kind, r.ThreadId, r.Seq, r.Author, r.Topic, effectiveTo, r.At, r.Body));
        }
        return (events, maxSeq);
    }

    internal static List<(string Line, long Seq, string To, string Author, string Topic)> ReadSince(
        string dbPath, long since, string agent)
    {
        var (events, _) = ReadEventsSince(dbPath, since, agent);
        var outList = new List<(string, long, string, string, string)>(events.Count);
        foreach (var ev in events)
            outList.Add((JsonSerializer.Serialize(ev), ev.Seq, ev.DirectedTo, ev.Author, ev.Topic));
        return outList;
    }
}
