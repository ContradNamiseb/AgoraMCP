namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// The agent side of in-binary push: a long-lived SSE subscription that
/// prints one event JSON object per line. Run it as the agent's sidecar
/// (any terminal, any OS — it is the agora binary itself, not a script)
/// and the hub pokes this terminal the moment matching board mail lands,
/// instead of the agent polling an inbox.
///
/// <para>Durability: the client tracks the last seen seq and reconnects
/// with ?since= after any drop, so the hub replays what was missed from
/// the SQLite log. A dead hub is a reconnect-with-backoff, never lost
/// mail — verified by the ?since= replay contract, not by connection
/// uptime.</para>
/// </summary>
public static class Subscribe
{
    public sealed record Options
    {
        public string Hub { get; init; } = $"127.0.0.1:{NotifyHub.DefaultPort}";
        public string Agent { get; init; } = "";
        public long Since { get; init; }
    }

    public static async Task<int> RunAsync(Options options, CancellationToken token)
    {
        var agent = options.Agent.Trim();
        var since = options.Since;
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backoff = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var url = $"http://{options.Hub}/events?agent={Uri.EscapeDataString(agent)}&since={since}";
                using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                res.EnsureSuccessStatusCode();
                backoff = TimeSpan.FromSeconds(1);
                using var stream = await res.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream);
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null) break; // hub closed the stream: reconnect below
                    if (!line.StartsWith("data: ")) continue; // :ping keep-alives and blanks
                    var payload = line["data: ".Length..];
                    Console.WriteLine(payload);
                    Console.Out.Flush();
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty("seq", out var seqEl) &&
                            seqEl.TryGetInt64(out var seq) && seq > since)
                        {
                            since = seq;
                        }
                    }
                    catch (System.Text.Json.JsonException) { /* print anyway, keep cursor */ }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Console.Error.WriteLine($"subscribe: hub unreachable ({e.Message}) — retry in {(int)backoff.TotalSeconds}s from seq {since}.");
                try { await Task.Delay(backoff, token); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
        return 0;
    }
}
