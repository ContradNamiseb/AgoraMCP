using AgoraMCP.Database;
using System.ComponentModel;
using AgoraMCP.Chats;
using ModelContextProtocol.Server;

namespace AgoraMCP.McpServer;

/// <summary>
/// Cross-agent chat visibility: every client on this box logs its own
/// conversations to disk; these tools list and read them, so any agent can see
/// what the others have been working on in their own sessions — the same way
/// multiple sessions of one client already see each other through the board.
/// Read-only by construction.
/// </summary>
[McpServerToolType]
public sealed class AgoraChatTools
{
    [McpServerTool(Name = "list_chats", ReadOnly = true)]
    [Description("""
        Newest chat sessions of each client on this machine, to see what other
        agents are working on: 'client session_id model last turns'. Read-only.
        Open one with read_chat. Turns show '-' for non-claudecode clients.
        """)]
    public static string ListChats(
        [Description("Only this client's sessions.")]
        ChatClient? client = null,
        [Description("Max sessions per client. Default 5.")]
        int per_client = 5)
    {
        var sessions = ChatReaders.ListSessions(perClient: Math.Clamp(per_client, 1, 20));
        if (client is { } c)
        {
            var wanted = c.Wire();
            sessions = sessions.Where(s => s.Client.Contains(wanted, StringComparison.Ordinal)).ToList();
        }
        if (sessions.Count == 0)
            return "no chat sessions found for any client.";
        var lines = sessions.Select(s =>
            $"{s.Client,-10} {s.SessionId}  model={s.Model}  last={s.LastActivity}  turns={s.Turns}");
        return "chat sessions (newest first):\n" + string.Join("\n", lines);
    }

    [McpServerTool(Name = "read_chat", ReadOnly = true)]
    [Description("""
        The user and assistant text turns of one chat session (tool traffic
        skipped), newest last, each cut to 160 chars: 'time role text'.
        Read-only. Unknown session id → error listing that client's newest ids.
        """)]
    public static string ReadChat(
        [Description("Client that owns the session.")]
        ChatClient client,
        [Description("Session id from list_chats.")]
        string session_id,
        [Description("Max turns, from the end. Default 30.")]
        int last = 30)
    {
        try
        {
            var turns = ChatReaders.Read(client.Wire(), session_id);
            if (turns.Count == 0)
                return $"session '{session_id}' has no readable user/assistant turns.";
            var take = Math.Clamp(last, 1, 200);
            var shown = turns.Count > take ? turns.Skip(turns.Count - take).ToList() : turns.ToList();
            var skipped = turns.Count - shown.Count;
            var lines = shown.Select(t =>
                $"{t.Timestamp ?? "          "} {t.Role,-9} {Preview(t.Text)}");
            return (skipped > 0 ? $"… {skipped} earlier turns skipped\n" : "") +
                   string.Join("\n", lines);
        }
        catch (AgoraException e)
        {
            var known = ChatReaders.ListSessions(perClient: 5)
                .Where(s => s.Client.Contains(client.Wire(), StringComparison.Ordinal))
                .Select(s => s.SessionId)
                .ToList();
            return $"Error: {e.Message}. " + (known.Count == 0
                ? $"No {client.Wire()} sessions exist on this machine."
                : $"Newest {client.Wire()} session ids: {string.Join(", ", known)}.");
        }
        catch (Exception e)
        {
            return $"Error: {e.GetType().Name}: {e.Message}";
        }
    }

    private static string Preview(string body)
    {
        var single = body.Replace("\r", "").Replace('\n', ' ');
        return single.Length <= 160 ? single : single[..159] + "…";
    }
}
