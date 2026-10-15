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
        Send a direct message to one named agent — the ping channel for
        reviewer↔implementer coordination. Unlike post_question there is no
        topic or routing to think about: one call, one recipient, and the
        recipient's next fetch_pending_queries (or watcher wake, or the human's
        toast) surfaces it. A pair keeps one shared conversation history, so
        reply with send_dm rather than reply_thread.

        Ping on delivery: desktop toast to the human, watcher event for the
        recipient's agent sessions, and the message lands in their pending
        queries. The recipient answers with send_dm back or reply on the thread
        id this returns.
        """)]
    public static async Task<string> SendDm(
        [Description("Recipient agent name, e.g. 'kimicode' or 'claudecode'. Routing understands former names.")]
        string to_agent,
        [Description("The message. Short and actionable — a DM is a tap on the shoulder, not a thread post.")]
        string body,
        [Description("The calling agent's name (you). Falls back to $AGORA_AGENT when omitted.")]
        string? author = null,
        [Description("Optional: link this DM to a board thread id so context travels (recorded in the conversation).")]
        long? thread_id = null)
    {
        var from = CallerIdentity.Resolve(author);
        try
        {
            long tid;
            long mid;
            using (var db = ServerState.Open())
            {
                string payload = thread_id is > 0 ? $"[{thread_id}] {body}" : body;
                (tid, mid) = DmStore.Send(db, from, to_agent.Trim(), payload);
            }
            // Ping paths: wake every watcher (the recipient's headless turn),
            // then toast the human. Both are fire-and-forget by design.
            NamedBoardEvent.Broadcast(ServerState.ResolveDbPath());
            await ToastAsync($"DM {from} → {to_agent}", body);
            return $"dm #{mid} delivered to {to_agent} (conversation thread {tid}). " +
                   "their pending queries and watcher carry it; keep the pair's history in send_dm.";
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

    [McpServerTool(Name = "read_inbox")]
    [Description("""
        Your direct-message conversations, newest activity first: who, the last
        message, its author and when. Use fetch_pending_queries for board
        threads; this is the DM view. Read a full conversation with read_thread
        on the thread id shown here.
        """)]
    public static string ReadInbox(
        [Description("The calling agent's name (you). Falls back to $AGORA_AGENT when omitted.")]
        string? author = null,
        [Description("Max conversations to return (default 20).")]
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
