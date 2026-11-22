using AgoraMCP.Database;
using AgoraMCP.McpServer.Notify;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AgoraMCP.McpServer;

/// <summary>State shared by the tool classes: which database file this process serves.</summary>
internal static class ServerState
{
    /// <summary>Set by 'serve --db'; otherwise each open resolves AGORA_DB or the default path.</summary>
    public static string? DbPathOverride { get; set; }

    /// <summary>
    /// The in-memory chat box: bodies resident for fast reads, compacted
    /// oldest-first when the byte budget is hit, database always the fallback.
    /// One per server process; its budget comes from AGORA_CACHE_BYTES (1 GiB
    /// default).
    /// </summary>
    public static readonly MessageCache Cache = new(MessageCache.ResolveBudget());

    public static string ResolveDbPath() =>
        DbPathOverride is { Length: > 0 } p ? Path.GetFullPath(p) : Config.ResolveDbPath();

    /// <summary>
    /// Opened per tool call: the store is small and every path is an indexed
    /// lookup, so a fresh connection (sub-millisecond on a local file) costs
    /// nothing against LLM thinking time — and a long-lived handle would pin
    /// the WAL. The bounded wait holds ONE connection across its slices instead.
    /// </summary>
    public static SqliteConnection Open() => AgoraDatabase.Open(ResolveDbPath());
}

/// <summary>
/// Runs the agora as an MCP server over stdio: one process per agent, all
/// pointed at the same database file.
/// </summary>
internal static class StdioServer
{
    public static async Task<int> RunAsync(string? dbPath, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(dbPath)) ServerState.DbPathOverride = dbPath;

        var builder = Host.CreateApplicationBuilder();

        // stdout carries JSON-RPC and nothing else. A single stray line of log
        // output there corrupts the stream and the client drops the connection,
        // which presents as a mysterious startup failure rather than as a log
        // problem — so every provider is pointed at stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var mcp = builder.Services
            .AddMcpServer(options =>
            {
                // Pin MCP 2025-11-25 so Claude Code v2.1.232+ (v2 MCP runtime)
                // keeps claude/channel and resources/subscribe active instead of
                // negotiating stateless 2026-07-28, which disables channels.
                options.ProtocolVersion = "2025-11-25";
                options.ServerInfo = new()
                {
                    Name = "agora",
                    Version = typeof(StdioServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                };
                options.ServerInstructions =
                    "Shared local blackboard between coding agents on this machine. " +
                    "Incoming peer messages may arrive as <channel source=\"agora\" ...> or via a notification mod; " +
                    "reply to DMs with send_dm and to board threads with respond.";
                // Agora thread 19/38 (user-directed), Tier 2: declares the
                // Channel capability unconditionally -- a client that never
                // heard of "claude/channel" (every MCP client except Claude
                // Code, today) just sees an experimental capability key it
                // doesn't recognize and ignores it, per the MCP spec's own
                // forward-compatibility contract for that dictionary. Actual
                // event delivery is ChannelBridge (hosted service below),
                // which is itself a no-op unless AGORA_AGENT is set.
                //
                // System.Text.Json.Nodes.JsonObject, not an anonymous type
                // and not a plain Dictionary<string, object>: both failed
                // live against this SDK's source-generated JsonContext (no
                // reflection fallback) when boxed as the Experimental
                // dictionary's `object` value -- an anonymous type has no
                // registered JsonTypeInfo at all, and Dictionary<string,
                // object> hit an AMBIGUOUS match between its IDictionary and
                // IReadOnlyDictionary registrations. JsonObject is
                // System.Text.Json's own DOM node type, with unambiguous
                // built-in converter support independent of any
                // source-generated context.
                options.Capabilities = new ServerCapabilities
                {
                    Experimental = new Dictionary<string, object>
                    {
                        ["claude/channel"] = new System.Text.Json.Nodes.JsonObject(),
                    },
                    // Thread #54 phase 3: agora://inbox/<agent> and agora://chats
                    // as subscribable resources; the notifications themselves are
                    // fired by ResourceWatcher (below) and gated on the client's
                    // subscriptions (ResourceSubscriptions).
                    Resources = new ResourcesCapability
                    {
                        Subscribe = true,
                        ListChanged = true,
                    },
                };
            })
            .WithStdioServerTransport()
            .WithTools<AgoraReadTools>()
            .WithTools<AgoraWriteTools>()
            .WithTools<AgoraSystemTools>()
            .WithTools<AgoraDmTools>()
            .WithTools<AgoraChatTools>()
            .WithTools<AgoraWorkflowTools>()
            .WithArgumentGuard()
            .WithResources<AgoraResources>();
        ResourceSubscriptions.Wire(mcp);

        builder.Services.AddHostedService<ChannelBridge>();
        builder.Services.AddHostedService<ResourceWatcher>();

        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }
}
