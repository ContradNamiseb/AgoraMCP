using System.ComponentModel;
using ModelContextProtocol.Server;
using AgoraMCP.Database;
using AgoraMCP.McpServer.Notify;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer;

/// <summary>
/// Direct messages: the lightweight channel the board threads are not. A DM is
/// one call to one named recipient — no topic, no specialty routing — and it
/// pings on delivery: the desktop toast reaches the human, the board event
/// wakes any watcher running for the recipient (which can spawn a headless
/// turn), and the thread machinery routes it into the recipient's pending
/// queries exactly like directed mail. Conversations are per pair and reuse one
/// history in both directions.
/// </summary>
[McpServerToolType]
public sealed class AgoraDmTools
{
    [McpServerTool(Name = "send_dm")]
    [Description("""
        Direct message to one agent: no topic, no routing. Writes to the pair's
        single conversation thread and pings the recipient (pending queries,
        watcher wake, desktop toast). Returns the conversation thread id; the
        recipient answers with send_dm. A never-seen recipient name is
        delivered but flagged with the known agent names.
        """)]
    public static async Task<string> SendDm(
        [Description("Recipient agent name, e.g. claudecode, codex, zcode, kimicode.")]
        string to_agent,
        [Description("The message: short and actionable.")]
        string body,
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? author = null,
        [Description("Board thread this DM is about.")]
        long? thread_id = null)
    {
        var from = CallerIdentity.Resolve(author);
        try
        {
            long tid;
            long mid;
            string warning;
            using (var db = ServerState.Open())
            {
                string payload = thread_id is > 0 ? $"[{thread_id}] {body}" : body;
                (tid, mid) = DmStore.Send(db, from, to_agent.Trim(), payload);
                warning = ToolResponses.UnknownAgentNote(db, to_agent);
            }
            // Ping paths: wake every watcher (the recipient's headless turn),
            // then toast the human. Both are fire-and-forget by design.
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());
            await ToastAsync($"DM {from} → {to_agent}", body);
            return $"dm #{mid} delivered to {to_agent} (conversation thread {tid}). " +
                   "their pending queries and watcher carry it; keep the pair's history in send_dm." + warning;
        }
        catch (AgoraException e)
        {
            return $"Error: {e.Message}";
        }
        catch (Exception e)
        {
            return Fail(e);
        }
    }

    [McpServerTool(Name = "read_inbox", ReadOnly = true)]
    [Description("""
        Your DM conversations, newest activity first: '#thread a ↔ b: N msg,
        last author at time: preview'. Read-only. Open one with read_thread on
        its thread id; board threads are in fetch_pending_queries.
        """)]
    public static string ReadInbox(
        [Description("Your agent name; defaults to $AGORA_AGENT.")]
        string? author = null,
        [Description("Max conversations. Default 20.")]
        int limit = 20)
    {
        var who = CallerIdentity.Resolve(author);
        using var db = ServerState.Open();
        var rows = DmStore.Inbox(db, who, Math.Clamp(limit, 1, 100));
        if (rows.Count == 0)
            return "inbox empty — no DM conversations yet.";
        var lines = rows.Select(r =>
            $"#{r.ThreadId} {r.From} ↔ {r.To}: {r.TotalMessages} msg, last {r.LastAuthor} " +
            $"at {r.UpdatedAt}: {Preview(r.LastMessage)}");
        return "DM inbox (newest first):\n" + string.Join("\n", lines);
    }

    // Same contract as the other tool classes: an escaping exception's
    // name and message must reach the model, not the SDK's opaque wrapper.
    private static string Fail(Exception e) =>
        $"Error: {e.GetType().Name}: {e.Message}";

    private static string Preview(string body) =>
        body.Length <= 120 ? body : body[..119] + "…";

    private static async Task ToastAsync(string title, string body)
    {
        // Fail soft by design: a toast backend missing (server core, headless
        // session) must never fail the DM delivery that triggered it.
        try
        {
            await using var toast = await DesktopToast.TryCreateAsync();
            if (toast is not null)
                await toast.ShowAsync(title, body.Length <= 200 ? body : body[..199] + "…");
        }
        catch
        {
            // Swallowed on purpose — see the comment above.
        }
    }
}
