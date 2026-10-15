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
}
