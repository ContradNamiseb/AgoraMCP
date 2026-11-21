using AgoraMCP.Chats;
using AgoraMCP.Database;
using ModelContextProtocol.Server;

namespace AgoraMCP.McpServer;

/// <summary>
/// The agora MCP resources: the same views the tools give, but addressable and
/// subscribable — a client that speaks MCP resources can read
/// agora://inbox/&lt;agent&gt; and be pinged with a resources/updated
/// notification when a DM lands (ResourceWatcher fires those), or point at
/// another client's chat session without paging through tool output. Read-only
/// by construction, like every other read path.
///
/// <para>All three are resource templates (the SDK attribute has no static-URI
/// form); the no-variable one still matches exactly one URI.</para>
/// </summary>
[McpServerResourceType]
public sealed class AgoraResources
{
    [McpServerResource(
        UriTemplate = "agora://inbox/{agent}",
        Name = "inbox",
        Title = "An agent's DM inbox",
        MimeType = "text/plain")]
    public static string Inbox(string agent)
    {
        using var db = ServerState.Open();
        var rows = DmStore.Inbox(db, agent.Trim());
        if (rows.Count == 0)
            return $"inbox of {agent} is empty — no DM conversations yet.";
        var lines = rows.Select(r =>
            $"#{r.ThreadId} {r.From} ↔ {r.To}: {r.TotalMessages} msg, last {r.LastAuthor} " +
            $"at {r.UpdatedAt}: {r.LastMessage}");
        return $"DM inbox of {agent} (newest first):\n" + string.Join("\n", lines);
    }

    [McpServerResource(
        UriTemplate = "agora://chats",
        Name = "chats",
        Title = "Newest chat sessions of every client on this machine",
        MimeType = "text/plain")]
    public static string Chats()
    {
        var sessions = ChatReaders.ListSessions(perClient: 5);
        if (sessions.Count == 0)
            return "no chat sessions found for any client.";
        var lines = sessions.Select(s =>
            $"{s.Client,-14} {s.SessionId}  model={s.Model}  last={s.LastActivity}  turns={(s.Turns >= 0 ? s.Turns.ToString() : "-")}");
        return "chat sessions (newest first):\n" + string.Join("\n", lines);
    }

    [McpServerResource(
        UriTemplate = "agora://chats/{client}/{session}",
        Name = "chat",
        Title = "One chat session of any client",
        MimeType = "text/plain")]
    public static string Chat(string client, string session)
    {
        var turns = ChatReaders.Read(client, session, limit: 50);
        if (turns.Count == 0)
            return $"session '{session}' has no readable user/assistant turns.";
        return string.Join("\n\n", turns.Select(t =>
            $"{t.Timestamp ?? ""} {t.Role}: {t.Text}"));
    }
}
