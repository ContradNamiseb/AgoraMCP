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
    [McpServerTool(Name = "list_chats")]
    [Description("""
        List the newest chat sessions of every client on this machine —
        claudecode, zcode, kimicode, codex — newest activity first. Use this to
        see what the other agents are working on in their own sessions, then
        read_chat for the conversation. Turn counts: claudecode counts user +
        assistant turns; others show '-' until read.
        """)]
    public static string ListChats(
        [Description("Only one client's sessions (claudecode, zcode, kimicode, codex). Omit for all.")]
        string? client = null,
        [Description("Max sessions per client (default 5).")]
        int per_client = 5)
    {
        var sessions = ChatReaders.ListSessions(perClient: Math.Clamp(per_client, 1, 20));
        if (!string.IsNullOrWhiteSpace(client))
        {
            var wanted = client.Trim().ToLowerInvariant();
            sessions = sessions.Where(s => s.Client.Contains(wanted, StringComparison.Ordinal)).ToList();
        }
        if (sessions.Count == 0)
            return "no chat sessions found for any client.";
        var lines = sessions.Select(s =>
            $"{s.Client,-10} {s.SessionId}  model={s.Model}  last={s.LastActivity}  turns={s.Turns}");
        return "chat sessions (newest first):\n" + string.Join("\n", lines);
    }

    [McpServerTool(Name = "read_chat")]
    [Description("""
        Read one chat session of another client (or your own) — the user and
        assistant text turns, tool traffic skipped. Get the session id from
        list_chats. Roles are as each client logged them. Read-only.
        """)]
    public static string ReadChat(
        [Description("Client that owns the session: claudecode, zcode, kimicode or codex.")]
        string client,
        [Description("Session id from list_chats.")]
        string session_id,
        [Description("Max turns to return, from the end of the conversation (default 30).")]
        int last = 30)
    {
        try
        {
            var turns = ChatReaders.Read(client, session_id);
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
            return $"Error: {e.Message}";
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
