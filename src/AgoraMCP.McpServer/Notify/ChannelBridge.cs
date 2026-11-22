using AgoraMCP.Database;
using AgoraMCP.McpServer.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Agora thread 19/38 ("Agora Signal Path"), Tier 2: pushes directed mail and
/// DMs into a LIVE Claude Code CLI session as a Channel event
/// (<c>notifications/claude/channel</c>). Inert unless AGORA_AGENT is set.
/// </summary>
internal sealed class ChannelBridge(ModelContextProtocol.Server.McpServer server, ILogger<ChannelBridge> logger)
    : BackgroundService
{
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agent = Environment.GetEnvironmentVariable("AGORA_AGENT")?.Trim();
        if (string.IsNullOrEmpty(agent))
            return;

        var dbPath = ServerState.ResolveDbPath();
        long cursor;
        try
        {
            cursor = NotifyHub.ReadMaxSeq(dbPath);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "channel bridge: could not read starting cursor; bridge disabled for this session.");
            return;
        }

        // Ephemeral per-process wake event so multiple sessions of the same
        // agent (and external tail/watch processes) never steal each other's
        // auto-reset kernel wake signals.
        var listenerId = $"channel-{agent}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        EventWaitHandle? ownEvent = null;
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

        Console.Error.WriteLine($"agora: channel bridge active, forwarding mail directed to '{agent}'.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WaitForWakeAsync(ownEvent, FallbackPollInterval, stoppingToken);
                    var (events, maxSeq) = NotifyHub.ReadEventsSince(
                        dbPath, cursor, agent, directedOnly: true, excludeSelf: true);
                    foreach (var ev in events)
                    {
                        await server.SendNotificationAsync(
                            "notifications/claude/channel",
                            new
                            {
                                content = FormatContent(ev),
                                meta = new Dictionary<string, string>
                                {
                                    ["author"] = ev.Author,
                                    ["thread_id"] = ev.ThreadId.ToString(),
                                    ["msg_id"] = ev.MsgId.ToString(),
                                    ["kind"] = ev.Kind,
                                },
                            },
                            cancellationToken: stoppingToken);
                    }
                    if (maxSeq > cursor) cursor = maxSeq;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    logger.LogWarning(e, "channel bridge: poll error; retrying.");
                    try { await Task.Delay(FallbackPollInterval, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
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

    internal static string FormatContent(BoardEvent ev)
    {
        var body = ev.Body.Trim();
        if (string.Equals(ev.Kind, DmStore.MessageKind, StringComparison.OrdinalIgnoreCase))
            return body.Length > 0 ? body : ev.Topic;
        return body.Length > 0 ? $"[#{ev.ThreadId} {ev.Topic}]\n{body}" : $"[#{ev.ThreadId}] {ev.Topic}";
    }

    internal static Task WaitForWakeAsync(
        EventWaitHandle? ownEvent, TimeSpan fallback, CancellationToken token)
    {
        if (ownEvent is null)
            return Task.Delay(fallback, token);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? reg = null;
        CancellationTokenRegistration ctr = default;

        reg = ThreadPool.RegisterWaitForSingleObject(
            ownEvent,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            tcs,
            fallback,
            executeOnlyOnce: true);

        ctr = token.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);

        return tcs.Task.ContinueWith(
            t =>
            {
                reg.Unregister(null);
                ctr.Dispose();
                t.GetAwaiter().GetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
