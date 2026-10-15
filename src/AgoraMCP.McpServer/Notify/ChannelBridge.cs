using AgoraMCP.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Agora thread 19/38 (user-directed, "Agora Signal Path" plan), Tier 2:
/// pushes directed mail into a LIVE, already-running Claude Code session as
/// a Channel event (<c>notifications/claude/channel</c>), instead of
/// waiting for the next tool call. Research preview on Claude Code's side
/// (needs --dangerously-load-development-channels or an org allowlist) --
/// see the plan doc for that tradeoff; this class only does its part of the
/// protocol and is inert (no-op background loop) for any client that never
/// asked for it.
///
/// <para>Runs inside the SAME 'serve' process as the tool server (registered
/// as a hosted service in StdioServer.cs), reusing NotifyHub's own
/// <see cref="NotifyHub.ReadSince"/> query rather than duplicating it --
/// same durable-log-plus-cursor contract Tier 1's toast and every
/// subscriber already rely on. Independent of whether NotifyHub's own `hub`
/// process happens to be running: this reads the SQLite log directly, the
/// same as ReadSince always has, not through the hub's HTTP endpoint.</para>
///
/// <para>Which agent's mail to forward is NOT knowable from the MCP
/// handshake -- every existing tool call in this codebase takes an explicit
/// `agent` argument per call, and Claude Code spawns this server with no
/// agent identity of its own. AGORA_AGENT (env var) is required for this
/// specifically, reusing the exact convention Program.cs's 'chat' command
/// already established -- each agent's own .mcp.json entry for this server
/// sets its own AGORA_AGENT once. No AGORA_AGENT means this loop never
/// starts: it is not enough to opt this feature on by accident.</para>
/// </summary>
// This project's own root namespace is AgoraMCP.McpServer, which shadows the
// SDK's ModelContextProtocol.Server.McpServer type when referenced
// unqualified -- confirmed live (CS0118, twice: a plain `using` and even a
// `using McpServer = ...` alias both lost to the enclosing namespace
// segment), so the SDK type is spelled out in full below rather than fought
// over with an alias.
internal sealed class ChannelBridge(ModelContextProtocol.Server.McpServer server, ILogger<ChannelBridge> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agent = Environment.GetEnvironmentVariable("AGORA_AGENT")?.Trim();
        if (string.IsNullOrEmpty(agent))
        {
            // Silent, not logged: this is the ordinary state for every
            // client that isn't opted into the channel plan at all, and a
            // stderr line per stdio-spawned MCP server on every session
            // start would be noise for the overwhelming majority of them.
            return;
        }

        var dbPath = ServerState.ResolveDbPath();
        // Same principle as Tier 1's toast cursor: start at "now" so this
        // doesn't replay the entire historical backlog as a wall of
        // <channel> tags the moment a session first loads this server --
        // only mail that lands after the session is live to receive it.
        long cursor;
        try
        {
            using var db = AgoraDatabase.Open(dbPath);
            cursor = db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM messages");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "channel bridge: could not read starting cursor; bridge disabled for this session.");
            return;
        }

        // Console.Error, not logger.LogInformation: StdioServer sets the DI
        // logger's minimum level to Warning (confirmed live -- an
        // LogInformation call here was silently dropped, not a bug in this
        // class), and this one-time confirmation that the bridge activated
        // at all is worth more than the usual Info-level noise concern --
        // same reasoning NotifyHub's own startup banner already applies.
        Console.Error.WriteLine($"agora: channel bridge active, forwarding mail directed to '{agent}'.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
                var rows = NotifyHub.ReadSince(dbPath, cursor, agent);
                foreach (var (_, seq, to, author, topic) in rows)
                {
                    if (to.Length > 0 && to == agent)
                    {
                        // content/meta shape and field names match Claude Code's
                        // own channel contract (channels-reference.md): content
                        // becomes the <channel> tag body, each meta entry an
                        // attribute. Claude Code drops this silently if the
                        // client never registered as a channel -- no error surfaces
                        // here either way, by protocol design, not a gap in this code.
                        await server.SendNotificationAsync(
                            "notifications/claude/channel",
                            new
                            {
                                content = $"{author}: {topic}",
                                meta = new Dictionary<string, string> { ["author"] = author },
                            },
                            cancellationToken: stoppingToken);
                    }
                    if (seq > cursor) cursor = seq;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // Fail soft, same invariant as Tier 1: a transient DB or
                // transport hiccup must never take the tool server down
                // with it, since it is far more important than this bridge.
                logger.LogWarning(e, "channel bridge: poll error; retrying.");
            }
        }
    }
}
