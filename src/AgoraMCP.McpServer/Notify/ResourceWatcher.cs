using AgoraMCP.Chats;
using AgoraMCP.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// The MCP-notification layer of the DM ping (thread #54, phase 3): watches
/// this agent's inbox from inside their own 'serve' process and, when a DM
/// lands, fires notifications/resources/updated for agora://inbox/&lt;agent&gt;
/// — but only to a client that subscribed to that URI, per the MCP contract.
/// A client that never subscribes is still pinged by the other layers (desktop
/// toast, watcher event, pending queries).
///
/// <para>Also fires notifications/resources/list_changed when the set of chat
/// sessions changes, so a client that listed agora://chats knows to re-list.
/// Same shape as ChannelBridge: a hosted service, polling SQLite (and, every
/// 15s, the session directories), fail-soft — never worth taking the tool
/// server down.</para>
///
/// <para>Runs only when AGORA_AGENT is set (the same opt-in convention
/// ChannelBridge uses): the inbox is an agent's, and a server process without
/// an agent has nobody's mail to watch.</para>
/// </summary>
internal sealed class ResourceWatcher(
    ModelContextProtocol.Server.McpServer server, ILogger<ResourceWatcher> logger)
    : BackgroundService
{
    private static readonly TimeSpan InboxPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ChatsPollInterval = TimeSpan.FromSeconds(15);
    private DateTime lastChatsScan = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the host's startup thread before any synchronous work: the
        // stamps below open the database (which applies the schema), and a
        // slow or queued open here would delay every hosted service that
        // starts after this one. ChannelBridge reaches its first await just
        // as fast, for the same reason.
        await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);

        var agent = Environment.GetEnvironmentVariable("AGORA_AGENT")?.Trim();
        if (string.IsNullOrEmpty(agent)) return;

        var dbPath = ServerState.ResolveDbPath();
        var inboxUri = $"agora://inbox/{agent}";
        // Console.Error, not the DI logger: StdioServer pins the logger to
        // Warning-and-stderr anyway, and one startup line confirming the
        // watcher is live costs nothing — same reasoning as ChannelBridge's
        // banner. Printed BEFORE the stamps: it must not be lost to whatever
        // the stamps block on.
        Console.Error.WriteLine($"agora: resource watcher active for '{agent}'.");
        string inboxStamp = InboxStamp(dbPath, agent);
        string chatsStamp = ChatsStamp();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(InboxPollInterval, stoppingToken);

                var stamp = InboxStamp(dbPath, agent);
                if (stamp != inboxStamp)
                {
                    inboxStamp = stamp;
                    if (ResourceSubscriptions.IsSubscribed(inboxUri))
                    {
                        await server.SendNotificationAsync(
                            "notifications/resources/updated",
                            new { uri = inboxUri },
                            cancellationToken: stoppingToken);
                    }
                }

                // The chat scan walks session directories, so it runs on its
                // own slower clock, independent of the inbox's second.
                if (DateTime.UtcNow - lastChatsScan >= ChatsPollInterval)
                {
                    lastChatsScan = DateTime.UtcNow;
                    var chats = ChatsStamp();
                    if (chats != chatsStamp)
                    {
                        chatsStamp = chats;
                        await server.SendNotificationAsync(
                            "notifications/resources/list_changed",
                            new { },
                            cancellationToken: stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                logger.LogWarning(e, "resource watcher: poll error; retrying.");
            }
        }
    }

    /// <summary>Everything a re-render of the inbox depends on, in one string.</summary>
    private static string InboxStamp(string dbPath, string agent)
    {
        try
        {
            using var db = AgoraDatabase.Open(dbPath);
            var rows = DmStore.Inbox(db, agent, 100);
            return string.Join("|", rows.Select(r =>
                $"{r.ThreadId}:{r.TotalMessages}:{r.UpdatedAt}"));
        }
        catch (Exception)
        {
            // A DB that cannot open now may open next tick; keep the last
            // stamp so recovery does not fire a spurious notification.
            return "unreadable";
        }
    }

    private static string ChatsStamp() =>
        string.Join("|", ChatReaders.ListSessions(perClient: 5)
            .Select(s => $"{s.Client}/{s.SessionId}"));
}
