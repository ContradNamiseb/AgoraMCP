using System.Net.Http;
using System.Text.Json;
using AgoraMCP.Database;
using AgoraMCP.McpServer.Notify;
using Xunit;

namespace AgoraMCP.Tests;

// In-binary push (no scripts, no broker): the hub replays the SQLite log
// over SSE and pushes new rows live; ?since= resumes after a drop.
public sealed class NotifyHubTests
{
    private const int TestPort = 48799;

    private static async Task<List<string>> CollectAsync(
        string query, int want, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var res = await client.GetAsync(
            $"http://127.0.0.1:{TestPort}/events{query}",
            HttpCompletionOption.ResponseHeadersRead, token);
        res.EnsureSuccessStatusCode();
        using var stream = await res.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (lines.Count < want)
        {
            var line = await reader.ReadLineAsync(token) ??
                throw new TimeoutException("stream closed before want lines");
            if (line.StartsWith("data: ")) lines.Add(line["data: ".Length..]);
        }
        return lines;
    }

    [Fact]
    public async Task Hub_replays_log_then_pushes_live()
    {
        using var temp = new TempDb();
        ThreadStore.PostQuestion(temp.Open(), "first", "", "alice");
        ThreadStore.PostQuestion(temp.Open(), "second", "", "bob");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hub = NotifyHub.RunAsync(
            new NotifyHub.Options { DbPath = temp.DbPath, Port = TestPort },
            cts.Token);
        // Give the listener a moment to bind (CI-safe margin, not timing).
        await Task.Delay(500, cts.Token);

        // Catch-up replay of the two seeded rows, in id order.
        var replay = await CollectAsync("?since=0", 2, cts.Token);
        Assert.Equal(2, replay.Count);
        var first = JsonDocument.Parse(replay[0]).RootElement;
        var second = JsonDocument.Parse(replay[1]).RootElement;
        Assert.True(first.GetProperty("seq").GetInt64() <
                    second.GetProperty("seq").GetInt64());
        Assert.Equal("alice", first.GetProperty("author").GetString());

        // Live push: a row posted after subscribing arrives on the stream.
        ThreadStore.PostQuestion(temp.Open(), "third", "", "carol");
        var live = await CollectAsync(
            $"?since={second.GetProperty("seq").GetInt64()}", 1, cts.Token);
        Assert.Contains("third", live[0]);

        cts.Cancel();
        await hub;
    }

    [Fact]
    public async Task Directed_filter_skips_other_agents_mail()
    {
        using var temp = new TempDb();
        ThreadStore.PostQuestion(temp.Open(), "open-q", "", "alice");
        ThreadStore.PostQuestion(temp.Open(), "for-bob", "", "alice",
            directedTo: "bob");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hub = NotifyHub.RunAsync(
            new NotifyHub.Options { DbPath = temp.DbPath, Port = TestPort },
            cts.Token);
        await Task.Delay(500, cts.Token);

        // carol's filter: the open question arrives, bob's directed mail does not.
        var got = await CollectAsync("?agent=carol&since=0", 1, cts.Token);
        Assert.Contains("open-q", got[0]);

        cts.Cancel();
        await hub;
    }

    [Fact]
    public void ReadEventsSince_routes_bidirectional_dms_aliases_turn_passes_and_suppresses_self()
    {
        using var temp = new TempDb();
        long dmThread;
        using (var db = temp.Open())
        {
            // claudecode initiates a DM to antigravity: threads.directed_to stays 'antigravity'.
            (dmThread, _) = DmStore.Send(db, "claudecode", "antigravity", "ping from claude");
            // antigravity replies on the same thread (under a former alias too!).
            DmStore.Send(db, "gemini-antigravity", "claudecode", "pong from antigravity");
            // Open board thread with a turn pass to claudecode.
            var boardThread = ThreadStore.PostQuestion(db, "open-work", "initial context", "codex");
            ThreadStore.Reply(db, boardThread, "please check this PR", "codex");
            TurnStore.Pass(db, boardThread, "codex", "claude-opus", "review the diff");
        }

        // For claudecode (directedOnly + excludeSelf):
        // - Must NOT see its own initial DM ("ping from claude")
        // - MUST see antigravity's reverse reply ("pong from antigravity") with body
        // - MUST see codex's open-thread posts after turn pass to claude-opus alias
        var (forClaude, maxSeq) = NotifyHub.ReadEventsSince(
            temp.DbPath, 0, "claudecode", directedOnly: true, excludeSelf: true);
        Assert.True(maxSeq >= 4);
        Assert.DoesNotContain(forClaude, e => e.Body.Contains("ping from claude"));
        var reply = Assert.Single(forClaude, e => e.ThreadId == dmThread);
        Assert.Equal("dm", reply.Kind);
        Assert.Equal("claudecode", reply.DirectedTo);
        Assert.Equal("pong from antigravity", reply.Body);
        Assert.Contains(forClaude, e => e.Body.Contains("please check this PR"));

        // For antigravity (dmsOnly + directedOnly + excludeSelf):
        // - MUST see claudecode's initial DM ("ping from claude")
        // - Must NOT see its own reply ("pong from antigravity") or board threads
        var (forAgy, _) = NotifyHub.ReadEventsSince(
            temp.DbPath, 0, "antigravity", directedOnly: true, dmsOnly: true, excludeSelf: true);
        var only = Assert.Single(forAgy);
        Assert.Equal("claudecode", only.Author);
        Assert.Equal("antigravity", only.DirectedTo);
        Assert.Equal("ping from claude", only.Body);
    }

    [Fact]
    public async Task BoardTail_wakes_live_on_broadcast_and_cleans_up_event_file()
    {
        using var temp = new TempDb();
        using (var db = temp.Open())
            DmStore.Send(db, "antigravity", "claudecode", "old history message");

        using var writer = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tailTask = BoardTail.RunAsync(
            new BoardTail.Options
            {
                DbPath = temp.DbPath,
                Agent = "claudecode",
                Once = true,
                DmsOnly = true,
                IntervalSeconds = 10, // proves NamedBoardEvent woke it, not the 10s fallback
            },
            stdout: writer,
            token: cts.Token);

        await Task.Delay(250, cts.Token);
        using (var db = temp.Open())
        {
            // Self-message should be ignored by tail.
            DmStore.Send(db, "claudecode", "antigravity", "self outgoing");
            // Reverse DM back to claudecode should wake tail immediately.
            DmStore.Send(db, "antigravity", "claudecode", "live wake payload");
        }
        AgoraMCP.McpServer.Watch.NamedBoardEvent.Broadcast(temp.DbPath);

        var code = await tailTask;
        Assert.Equal(0, code);

        var lines = writer.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.Equal("dm", root.GetProperty("kind").GetString());
        Assert.Equal("antigravity", root.GetProperty("author").GetString());
        Assert.Equal("claudecode", root.GetProperty("directed_to").GetString());
        Assert.Equal("live wake payload", root.GetProperty("body").GetString());

        // Ephemeral tail-*.event file must be removed on clean exit.
        var evDir = AgoraMCP.McpServer.Watch.NamedBoardEvent.EventsDirectoryFor(temp.DbPath);
        if (Directory.Exists(evDir))
            Assert.Empty(Directory.GetFiles(evDir, "tail-*.event"));
    }

    [Fact]
    public void Live_server_negotiates_2025_11_25_and_pushes_bidirectional_channel_dms()
    {
        using var temp = new TempDb();
        // Seed a DM thread initiated BY claudecode (so threads.directed_to = 'antigravity').
        using (var db = temp.Open())
            DmStore.Send(db, "claudecode", "antigravity", "initial hello");

        using var client = new McpStdioClient(temp.DbPath, agentEnv: "claudecode");

        // Must pin 2025-11-25 + advertise claude/channel + ServerInstructions.
        Assert.Equal("2025-11-25", client.InitializeResult.GetProperty("protocolVersion").GetString());
        Assert.True(client.InitializeResult
            .GetProperty("capabilities")
            .GetProperty("experimental")
            .TryGetProperty("claude/channel", out _));
        Assert.Contains("send_dm", client.InitializeResult.GetProperty("instructions").GetString() ?? "");

        // Wait briefly for ChannelBridge to sample its initial cursor & register event.
        Thread.Sleep(400);

        // Send a self-message (must NOT echo) and then a reverse-direction reply from antigravity.
        using (var db = temp.Open())
        {
            DmStore.Send(db, "claudecode", "antigravity", "should not echo");
            DmStore.Send(db, "antigravity", "claudecode", "hello back over channel");
        }
        AgoraMCP.McpServer.Watch.NamedBoardEvent.Broadcast(temp.DbPath);

        var notif = client.WaitForNotification("notifications/claude/channel", seconds: 5);
        var p = notif.GetProperty("params");
        Assert.Equal("hello back over channel", p.GetProperty("content").GetString());
        var meta = p.GetProperty("meta");
        Assert.Equal("antigravity", meta.GetProperty("author").GetString());
        Assert.Equal("dm", meta.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(meta.GetProperty("thread_id").GetString()));
        Assert.False(string.IsNullOrEmpty(meta.GetProperty("msg_id").GetString()));
    }
}
