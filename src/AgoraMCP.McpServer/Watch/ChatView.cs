using Microsoft.Data.Sqlite;
using AgoraMCP.Database;

namespace AgoraMCP.McpServer.Watch;

/// <summary>
/// `agora chat`: the user's live terminal view of the board — every thread and
/// message, refreshed the moment anything happens (the same per-agent named
/// event the watch uses drives the refresh) or every coarse interval, whichever
/// comes first. Entirely local: a file read and a kernel event, no ports, no
/// server. Q or Ctrl+C quits.
/// The viewer's identity is NOT hardcoded (thread #21 review #124, finding 2):
/// `--agent` wins, then $AGORA_AGENT, and when neither is set the view belongs
/// to no agent — directed threads render a neutral "DIRECTED TO name" label
/// instead of "TO YOU".
/// </summary>
internal static class ChatView
{
    public static int Run(string dbPath, double refreshSeconds, string viewerAgent = "")
    {
        using var ownEvent = NamedBoardEvent.OpenOwnEvent(dbPath, "chat-ui");
        NamedBoardEvent.Register(dbPath, "chat-ui");

        Console.WriteLine("agora chat — live board view. Q to quit.");
        while (true)
        {
            try
            {
                Render(dbPath, viewerAgent);
            }
            catch (Exception e)
            {
                Console.WriteLine($"chat: render error: {e.Message}");
            }

            QuitIfRequested();

            // Slice the coarse wait so the quit key stays responsive even
            // while nothing is being signaled.
            var deadline = DateTime.UtcNow.AddSeconds(refreshSeconds);
            while (DateTime.UtcNow < deadline)
            {
                QuitIfRequested();
                if (ownEvent?.WaitOne(200) == true) break;
            }
        }
    }

    private static void QuitIfRequested()
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.Q)
                Environment.Exit(0);
        }
    }

    /// <summary>
    /// Render the whole board. Internal for the smoke tests; writes to `writer`
    /// (default Console.Out) rather than Console.SetOut, so capturing tests in
    /// different xUnit classes cannot race each other's console (thread #21
    /// review #124). Does not touch Console.Clear/WindowWidth, so it runs
    /// headless. viewerAgent empty = no-agent view: every directed thread gets
    /// a neutral "DIRECTED TO name" label; otherwise only threads addressed to
    /// viewerAgent get the "DIRECTED TO YOU" marker.
    /// </summary>
    internal static void Render(string dbPath, string viewerAgent = "", TextWriter? writer = null)
    {
        using var db = AgoraDatabase.Open(dbPath);
        var w = writer ?? Console.Out;
        // Colors are console-global state, so they are only safe when writing
        // to the real console; an injected writer (capturing tests) gets plain
        // text, which keeps concurrent test classes isolated.
        var colorize = writer is null;

        var threads = ThreadStore.ListThreads(db, limit: 50);
        var presence = PresenceStore.OnlineRoster(db);

        var online = presence.Count > 0 ? string.Join(", ", presence.Select(p => p.Agent)) : "nobody";
        w.WriteLine($"AGORA — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — online: {online}");

        foreach (var t in threads.OrderBy(t => t.Id))
        {
            if (colorize) Console.ForegroundColor = ConsoleColor.Cyan;
            w.Write($"#{t.Id} ");
            if (colorize) Console.ForegroundColor = ConsoleColor.Yellow;
            w.Write($"[{(t.Specialty.Length > 0 ? t.Specialty : "general")}] ");
            if (colorize) Console.ResetColor();
            w.WriteLine(t.Topic);
            if (colorize) Console.ForegroundColor = ConsoleColor.DarkGray;
            w.WriteLine($"   by {t.CreatedBy} · {t.Status} · dir→{t.DirectedTo} · updated {t.UpdatedAt}");
            if (colorize) Console.ResetColor();
            if (viewerAgent.Length > 0 && t.DirectedTo == viewerAgent)
            {
                if (colorize) Console.ForegroundColor = ConsoleColor.Magenta;
                w.WriteLine($"   ⚑ DIRECTED TO YOU by {t.CreatedBy}");
                if (colorize) Console.ResetColor();
            }
            else if (viewerAgent.Length == 0 && t.DirectedTo.Length > 0)
            {
                if (colorize) Console.ForegroundColor = ConsoleColor.Magenta;
                w.WriteLine($"   ⚑ DIRECTED TO {t.DirectedTo} by {t.CreatedBy}");
                if (colorize) Console.ResetColor();
            }

            // Delivery receipts (thread #23 stage 1): who has read this thread
            // and who has handled it, from the shared recipient_cursors table.
            // Silent on threads nobody has a recorded cursor for.
            var receipts = RecipientStore.ForThread(db, t.Id);
            if (receipts.Count > 0)
            {
                var parts = receipts.Select(rc => rc.LastAckMsgId > 0
                    ? $"{rc.Agent} acked#{rc.LastAckMsgId}"
                    : $"{rc.Agent} seen#{rc.LastSeenMsgId}");
                w.WriteLine($"   ✉ " + string.Join(" · ", parts));
            }

            // Review workflow overlay (thread #23 stage 2): the current
            // verdict + reviewed revision + reviewer, when the thread has any.
            var review = ReviewStore.Current(db, t.Id);
            if (review is not null)
            {
                if (colorize) Console.ForegroundColor = ConsoleColor.DarkYellow;
                w.WriteLine($"   ⚖ review: {review}");
                if (colorize) Console.ResetColor();
            }

            // Turn-token overlay (thread #36): the thread's current turn holder.
            var turn = TurnStore.CurrentTurn(db, t.Id);
            if (turn is not null)
            {
                if (colorize) Console.ForegroundColor = ConsoleColor.DarkRed;
                var rTag = turn.Role.Length > 0 ? $" as {turn.Role}" : "";
                var sTag = turn.Stage.Length > 0 ? $" ({turn.Stage})" : "";
                w.WriteLine($"   ⏳ [TURN: {turn.Holder}{rTag}{sTag}]");
                if (colorize) Console.ResetColor();
            }

            // Handoff overlay (thread #23 stage 3): current handoff, if any.
            var handoff = HandoffStore.Current(db, t.Id);
            if (handoff is not null)
            {
                if (colorize) Console.ForegroundColor = ConsoleColor.DarkCyan;
                w.WriteLine($"   ⇄ handoff #{handoff.Id}: {handoff}");
                if (colorize) Console.ResetColor();
            }

            foreach (var m in ThreadStore.ReadThread(db, t.Id).Messages)
            {
                if (colorize && m.Author == "zcode-glm") Console.ForegroundColor = ConsoleColor.Green;
                w.WriteLine($"   #{m.Id} [{m.Kind}] {m.Author} · {m.CreatedAt}");
                if (colorize) Console.ResetColor();
                foreach (var line in m.Body.Split('\n'))
                    w.WriteLine($"     {line.TrimEnd()}");
                w.WriteLine();
            }
        }
    }
}
