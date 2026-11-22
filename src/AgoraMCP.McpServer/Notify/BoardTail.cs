using System.Text;
using System.Text.Json;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Hub-less, zero-CPU board event streamer (`agora tail`): waits on its own
/// ephemeral Windows <see cref="NamedBoardEvent"/> (falling back to a coarse
/// async delay on non-Windows or missed signals) and writes one UTF-8 JSON
/// <see cref="BoardEvent"/> line to stdout whenever peer mail arrives for the
/// target agent.
///
/// <para>Powers every client push adapter without requiring `agora hub`:
/// Antigravity Sidecars (`agentapi send-message`), Claude Code Mods
/// (`$.process.spawn` -> `$.prompt.submit`), Claude Code Plugin Monitors,
/// and one-shot reactive background waiters (`--once`).</para>
/// </summary>
public static class BoardTail
{
    public sealed record Options
    {
        public required string DbPath { get; init; }
        public required string Agent { get; init; }
        /// <summary>Starting message cursor; null = start at current MAX(id) (live only).</summary>
        public long? Since { get; init; }
        /// <summary>Include open broadcast threads in addition to directed mail and DMs.</summary>
        public bool IncludeAll { get; init; }
        /// <summary>Only emit direct messages (threads.kind = 'dm').</summary>
        public bool DmsOnly { get; init; }
        /// <summary>Exit 0 immediately after printing the first matching batch.</summary>
        public bool Once { get; init; }
        /// <summary>Optional maximum seconds to wait before exiting 0 cleanly.</summary>
        public double? TimeoutSeconds { get; init; }
        /// <summary>Coarse safety-net interval in seconds (default 5s Windows, 1s elsewhere).</summary>
        public double IntervalSeconds { get; init; } = NamedBoardEvent.IsSupported ? 5 : 1;
    }

    public static async Task<int> RunAsync(
        Options options, TextWriter? stdout = null, CancellationToken token = default)
    {
        var agent = options.Agent.Trim();
        if (agent.Length == 0)
        {
            Console.Error.WriteLine("tail: --agent is required (or set AGORA_AGENT).");
            return 2;
        }

        if (stdout is null)
        {
            try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
            catch (IOException) { }
        }
        var output = stdout ?? Console.Out;

        var dbPath = Path.GetFullPath(options.DbPath);
        var listenerId = $"tail-{agent}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        EventWaitHandle? ownEvent = null;

        // Register BEFORE sampling the initial cursor so a write landing
        // right after ReadMaxSeq cannot slip between snapshot and wait.
        try
        {
            ownEvent = NamedBoardEvent.OpenOwnEvent(dbPath, listenerId);
            if (ownEvent is not null)
                NamedBoardEvent.Register(dbPath, listenerId);
        }
        catch (Exception)
        {
            ownEvent?.Dispose();
            ownEvent = null;
        }

        try
        {
            var cursor = options.Since ?? NotifyHub.ReadMaxSeq(dbPath);
            var deadline = options.TimeoutSeconds is > 0
                ? DateTime.UtcNow.AddSeconds(options.TimeoutSeconds.Value)
                : (DateTime?)null;
            var fallback = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 0.1, 60));

            // If caller asked for historical replay (--since), drain immediately
            // before sleeping; if live-only, still check once in case a write
            // landed right after ReadMaxSeq.
            var firstPass = true;
            while (!token.IsCancellationRequested)
            {
                if (!firstPass)
                {
                    var wait = fallback;
                    if (deadline is { } dl)
                    {
                        var remaining = dl - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) return 0;
                        if (remaining < wait) wait = remaining;
                    }
                    try
                    {
                        await ChannelBridge.WaitForWakeAsync(ownEvent, wait, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
                firstPass = false;

                try
                {
                    var (events, maxSeq) = NotifyHub.ReadEventsSince(
                        dbPath,
                        cursor,
                        agent,
                        directedOnly: !options.IncludeAll,
                        dmsOnly: options.DmsOnly,
                        excludeSelf: true);

                    if (maxSeq > cursor) cursor = maxSeq;

                    if (events.Count > 0)
                    {
                        foreach (var ev in events)
                            output.WriteLine(JsonSerializer.Serialize(ev));
                        output.Flush();
                        if (options.Once) return 0;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"tail: read error ({e.Message}) — retrying.");
                }

                if (deadline is { } dlAfter && DateTime.UtcNow >= dlAfter)
                    return 0;
            }
            return 0;
        }
        finally
        {
            if (ownEvent is not null)
            {
                NamedBoardEvent.Unregister(dbPath, listenerId);
                ownEvent.Dispose();
            }
        }
    }
}
