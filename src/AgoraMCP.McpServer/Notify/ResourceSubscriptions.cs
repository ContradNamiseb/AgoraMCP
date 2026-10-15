using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Which resource URIs the local MCP client subscribed to. One server process
/// serves exactly one client over stdio, so a process-wide set is the whole
/// truth; the subscribe/unsubscribe handlers in StdioServer feed it, and
/// ResourceWatcher consults it so notifications/resources/updated goes only
/// where the spec says it may — to a client that asked for that URI.
/// </summary>
internal static class ResourceSubscriptions
{
    private static readonly ConcurrentDictionary<string, byte> Subscribed =
        new(StringComparer.Ordinal);

    public static void Add(string uri) => Subscribed[uri] = 1;

    public static void Remove(string uri) => Subscribed.TryRemove(uri, out _);

    public static bool IsSubscribed(string uri) => Subscribed.ContainsKey(uri);

    /// <summary>Bind the SDK's subscribe/unsubscribe requests into the registry.</summary>
    public static void Wire(IMcpServerBuilder builder)
    {
        builder.WithSubscribeToResourcesHandler((context, _) =>
        {
            Add(context.Params.Uri);
            return new ValueTask<EmptyResult>(new EmptyResult());
        });
        builder.WithUnsubscribeFromResourcesHandler((context, _) =>
        {
            Remove(context.Params.Uri);
            return new ValueTask<EmptyResult>(new EmptyResult());
        });
    }
}
