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
    [property: JsonPropertyName("at")] string CreatedAt);

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
                        if (sub.Agent.Length > 0 && to.Length > 0 && to != sub.Agent) continue;
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

    private static long ReadMaxSeq(string dbPath)
    {
        using var db = AgoraDatabase.Open(dbPath);
        return db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM messages");
    }

    internal static List<(string Line, long Seq, string To, string Author, string Topic)> ReadSince(
        string dbPath, long since, string agent)
    {
        // Agent filter here is a bandwidth courtesy only (directed mail to
        // someone else is skipped for named subscribers); correctness never
        // depends on it because the pump re-checks per subscriber.
        using var db = AgoraDatabase.Open(dbPath);
        var rows = db.Prepare("""
            SELECT m.id, m.thread_id, m.author, m.kind, t.topic, t.directed_to, m.created_at
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
                Topic: r.GetString("topic"),
                DirectedTo: r.GetString("directed_to"),
                At: r.GetString("created_at")));
        var outList = new List<(string, long, string, string, string)>();
        foreach (var r in rows)
        {
            if (agent.Length > 0 && r.DirectedTo.Length > 0 && r.DirectedTo != agent) continue;
            var ev = new BoardEvent(r.Seq, r.Kind, r.ThreadId, r.Seq, r.Author, r.Topic, r.DirectedTo, r.At);
            outList.Add((JsonSerializer.Serialize(ev), r.Seq, r.DirectedTo, r.Author, r.Topic));
        }
        return outList;
    }
}
