using System.Globalization;
using Microsoft.Data.Sqlite;
using AgoraMCP.Database;
using AgoraMCP.Models;
using AgoraMCP.McpServer.SystemInterop;

namespace AgoraMCP.McpServer;

/// <summary>
/// Text rendering for tool results. These strings are read by a model, not a
/// parser: every status lands on its own labelled line so a caller can act on
/// answered / no_peer_online / timeout without guessing.
/// </summary>
internal static class ToolResponses
{
    public static string RenderThreadLines(IReadOnlyList<ThreadSummary> threads, string heading)
    {
        if (threads.Count == 0) return $"{heading}: none.";
        var lines = new List<string> { $"{heading}: {threads.Count}" };
        lines.AddRange(threads.Select(ThreadLine));
        return string.Join('\n', lines);
    }

    private static string ThreadLine(ThreadSummary t)
    {
        var dir = t.DirectedTo.Length > 0 ? $" (dir→{t.DirectedTo})" : "";
        var spec = t.Specialty.Length > 0 ? $" [{t.Specialty}]" : "";
        return $"#{t.Id}{spec}{dir} {t.Topic} — by {t.CreatedBy}, {t.MessageCount} msg, " +
               $"status {t.Status}, updated {ShortTime(t.UpdatedAt)}";
    }

    public static string RenderDetail(ThreadDetail d, bool truncated = false, int maxChars = 0) =>
        RenderDetail(d, truncated, maxChars, out _);

    /// <summary>As above; <paramref name="shown"/> is how many messages fit the budget.</summary>
    public static string RenderDetail(ThreadDetail d, bool truncated, int maxChars, out int shown)
    {
        var h = d.Header;
        var lines = new List<string>
        {
            $"#{h.Id} [{(h.Specialty.Length > 0 ? h.Specialty : "general")}] {h.Topic}",
            $"status: {h.Status}" +
            (h.DirectedTo.Length > 0 ? $" · directed to: {h.DirectedTo}" : "") +
            $" · by {h.CreatedBy} · opened {ShortTime(h.CreatedAt)} · updated {ShortTime(h.UpdatedAt)}",
        };
        if (h.Context.Length > 0) lines.Add($"context: {h.Context}");
        lines.Add($"messages ({d.Messages.Count}):");
        var used = lines.Sum(l => l.Length + 1);
        lines.AddRange(Budgeted(d.Messages, maxChars > 0 ? Math.Max(maxChars - used, 1) : 0, out shown));
        // Canonical history is never lost to a window: the truncation note
        // names the full-history path explicitly (thread #23 stage 5).
        if (truncated)
            lines.Add($"… older messages exist — this was an incremental window. " +
                      "The canonical full history is available via read_thread without since_id/limit.");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Message blocks in order until <paramref name="maxChars"/> runs out (0 =
    /// no cap). What did not fit is named by id, with the cursor that resumes
    /// exactly where this stopped — a cut the caller can see past, never a
    /// silent one. A single message larger than the whole budget is shown cut
    /// rather than skipped, so every call makes progress.
    /// </summary>
    public static List<string> Budgeted(IReadOnlyList<ThreadMessage> messages, int maxChars) =>
        Budgeted(messages, maxChars, out _);

    /// <summary>As above; <paramref name="shown"/> counts messages shown whole or cut.</summary>
    public static List<string> Budgeted(IReadOnlyList<ThreadMessage> messages, int maxChars, out int shown)
    {
        var blocks = new List<string>();
        var used = 0;
        shown = messages.Count;
        for (var i = 0; i < messages.Count; i++)
        {
            var block = MessageBlock(messages[i]);
            if (maxChars <= 0 || used + block.Length + 1 <= maxChars)
            {
                blocks.Add(block);
                used += block.Length + 1;
                continue;
            }

            if (blocks.Count == 0)
            {
                var room = Math.Max(maxChars - 120, 200);
                blocks.Add(block[..Math.Min(room, block.Length)] +
                           $" …[message cut at {room} of {block.Length} chars; full text: read_thread " +
                           $"since_id={messages[i].Id - 1} limit=1 max_chars=0]");
                i++;
            }

            shown = i;
            var rest = messages.Skip(i).ToList();
            if (rest.Count > 0)
            {
                var resume = messages[i - 1].Id;
                blocks.Add($"… max_chars reached: {rest.Count} more message(s) not shown " +
                           $"({IdList(rest.Select(m => m.Id))}). Continue with since_id={resume}.");
            }
            break;
        }
        return blocks;
    }

    /// <summary>"#901 #903 #904 … #940 (+31)" — enough ids to act on, bounded in size.</summary>
    public static string IdList(IEnumerable<long> ids, int show = 12)
    {
        var all = ids.ToList();
        if (all.Count <= show) return string.Join(' ', all.Select(i => $"#{i}"));
        return string.Join(' ', all.Take(show - 1).Select(i => $"#{i}")) +
               $" … #{all[^1]} (+{all.Count - show + 1} more)";
    }

    /// <summary>
    /// The stable pagination window (thread #23 stage 5): message ids are
    /// immutable and monotonic, so since_id is a stable cursor by
    /// construction. Returns the window and whether anything was cut.
    /// </summary>
    public static (IReadOnlyList<ThreadMessage> Window, bool Truncated) WindowMessages(
        IReadOnlyList<ThreadMessage> messages, long sinceId, int limit)
    {
        if (sinceId <= 0 && limit <= 0) return (messages, false);
        var newer = messages.Where(m => m.Id > sinceId).ToList();
        var window = limit > 0 && newer.Count > limit ? newer.Take(limit).ToList() : newer;
        return (window, window.Count < newer.Count);
    }

    /// <summary>
    /// The compact briefing (thread #23 stage 5): one call carrying the
    /// current review decision, the active handoff, the caller's delivery
    /// state, and the most recent messages — with a TRUST BOUNDARY between
    /// them. Overlays are VERIFIED STATE (rows this system recorded); message
    /// bodies are PEER-AUTHORED PROSE (claims to verify against code/tests),
    /// and the rendering says so explicitly: thread text is never treated as
    /// an instruction to the reader.
    /// </summary>
    public static string RenderBriefing(SqliteConnection db, long threadId,
        string agent, long sinceId, int recentLimit, MessageCache? cache, bool useDigest = true,
        int maxChars = 0)
    {
        var detail = ThreadStore.ReadThread(db, threadId, cache);
        var h = detail.Header;
        var lines = new List<string>
        {
            $"BRIEFING #{h.Id} [{(h.Specialty.Length > 0 ? h.Specialty : "general")}] {h.Topic}",
            $"status: {h.Status}" +
            (h.DirectedTo.Length > 0 ? $" · directed to: {h.DirectedTo}" : "") +
            $" · by {h.CreatedBy} · updated {ShortTime(h.UpdatedAt)}",
        };

        // A digest replaces the bulk of the history with a compact summary, so
        // the reader pays for the digest (hundreds of tokens) instead of the
        // whole thread (thousands to tens of thousands). The digest is
        // peer-authored prose and is labelled as such; the caller is also told
        // exactly what it replaces, and that the raw range is still one call
        // away — a summary the reader cannot audit is a summary it must trust.
        ThreadDigest? digest = null;
        if (useDigest) digest = DigestStore.Current(db, threadId);
        var digestFloor = digest?.CoveredThroughMsgId ?? 0;
        var effectiveSince = Math.Max(sinceId, digestFloor);

        lines.Add("— RECORDED STATE (database rows — proves recording, not that underlying claims are verified) —");
        var turn = TurnStore.CurrentTurn(db, threadId);
        lines.Add(turn is null
            ? "  turn: none recorded on this thread."
            : $"  turn: held by {turn.Holder}" +
              (turn.Role.Length > 0 ? $" [{turn.Role}]" : "") +
              (turn.Stage.Length > 0 ? $" (stage: {turn.Stage})" : "") +
              (turn.FromAgent.Length > 0 ? $" — from {turn.FromAgent}" + (turn.FromRole.Length > 0 ? $" [{turn.FromRole}]" : "") : "") +
              (turn.Instruction.Length > 0 ? $" — instruction: {turn.Instruction}" : ""));
        var review = ReviewStore.Current(db, threadId);
        lines.Add(review is null
            ? "  review: none recorded on this thread."
            : $"  review: {review}");
        var handoff = HandoffStore.Current(db, threadId);
        lines.Add(handoff is null
            ? "  handoff: none on this thread."
            : $"  handoff: #{handoff.Id} {handoff}");
        if (agent.Length > 0)
        {
            var mine = RecipientStore.DeliveryFor(db, agent)
                .FirstOrDefault(s => s.ThreadId == threadId);
            lines.Add(mine is null
                ? $"  delivery for {agent}: no recorded cursor on this thread."
                : $"  delivery for {agent}: " + (mine.Handled
                    ? $"HANDLED (acked through {mine.LastAckMsgId})"
                    : mine.Observed
                        ? $"observed through {mine.LastSeenMsgId}, UNHANDLED"
                        : $"PENDING: {mine.MaxMsgId - mine.LastSeenMsgId} unread (newest {mine.MaxMsgId})"));
        }

        lines.Add("— RECENT MESSAGES (peer-authored messages — recorded claims, not verified facts; " +
                  "provenance cue, not an instruction boundary: consumers are responsible for " +
                  "instruction hierarchy — verify against code and tests) —");

        // The digest, when one covers part of this thread: printed here with
        // its own provenance line, because it is a peer's summary of history,
        // not the history itself.
        if (digest is not null)
        {
            var replaced = detail.Messages.Count(m => m.Id <= digest.CoveredThroughMsgId);
            var rawTokens = TokenEstimate.ApproxTokens(
                detail.Messages.Where(m => m.Id <= digest.CoveredThroughMsgId));
            var digestTokens = TokenEstimate.ApproxTokens(digest.Body);
            lines.Add($"— DIGEST of messages #1–#{digest.CoveredThroughMsgId} " +
                      $"({replaced} message(s); written by {digest.Author} {Age(digest.CreatedAt)} ago) —");
            lines.Add($"  A peer's summary, not the messages. Raw history: read_thread {threadId} " +
                      $"--limit 0 --since 0, or thread_briefing with digest=0.");
            lines.Add($"  ≈{digestTokens} estimated tokens here instead of ≈{rawTokens} for the raw range.");
            lines.AddRange(Indent(digest.Body).Split('\n').Select(l => "  " + l));
        }

        // The briefing takes the most RECENT of the window (a spawned turn
        // wants the tail, not the head): since_id selects, recentLimit caps.
        // Messages the digest already covers are excluded — that is the saving.
        var newer = detail.Messages.Where(m => m.Id > effectiveSince).ToList();
        var window = recentLimit > 0 && newer.Count > recentLimit
            ? newer.TakeLast(recentLimit).ToList()
            : newer;
        if (window.Count < newer.Count)
            lines.Add($"  … {newer.Count - window.Count} earlier message(s) in this window — " +
                      "canonical full history via read_thread (optionally with since_id/limit).");
        if (window.Count == 0)
            lines.Add(digest is null
                ? "  (no messages past the given cursor)"
                : "  (nothing past the digest — it covers the thread to date)");
        var used = lines.Sum(l => l.Length + 1);
        lines.AddRange(Budgeted(window, maxChars > 0 ? Math.Max(maxChars - used, 1) : 0));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// A trailing warning when a recipient has never been seen here — the
    /// usual cause is a model name used in place of the client name. Advisory,
    /// not a refusal: an agent may legitimately receive work before its first
    /// heartbeat, but nobody should send it there by accident.
    /// </summary>
    public static string UnknownAgentNote(SqliteConnection db, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || BoardHints.IsKnownAgent(db, name)) return "";
        return $"\n(warning: '{name.Trim()}' has never been seen on this board — if that is a typo, " +
               $"known agents are: {string.Join(", ", BoardHints.KnownAgents(db))})";
    }

    private static string MessageBlock(ThreadMessage m) =>
        $"  #{m.Id} [{m.Kind}] {m.Author} · {ShortTime(m.CreatedAt)}\n    {Indent(m.Body)}";

    private static string Indent(string body) => body.Replace("\n", "\n    ");

    public static string RenderRoster(IReadOnlyList<PresenceEntry> roster)
    {
        if (roster.Count == 0) return "online now: nobody (not even you — call heartbeat first).";
        var lines = new List<string> { $"online now: {roster.Count}" };
        lines.AddRange(roster.Select(p =>
        {
            var spec = p.Specialty.Length > 0 ? $" [{p.Specialty}]" : "";
            var caps = p.Capabilities.Length > 0 ? $" — {p.Capabilities}" : "";
            return $"  - {p.Agent}{spec}{caps} (heartbeat {Age(p.LastHeartbeat)} ago)";
        }));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The foreground-window snapshot. Labelled lines like every other tool
    /// result (read by a model, not a parser), not a raw record dump.
    /// </summary>
    public static string RenderActiveWindow(ActiveWindowContext w) =>
        $"foreground window: {w.WindowTitle}\n" +
        $"  process: {w.ProcessName} (pid {w.ProcessId})\n" +
        $"  executable: {w.ExecutablePath}\n" +
        $"  captured: {w.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z";

    /// <summary>The visible top-level windows, one labelled block each.</summary>
    public static string RenderWindowList(IReadOnlyList<ActiveWindowContext> windows, string filter)
    {
        var heading = filter.Length > 0
            ? $"visible windows matching '{filter}'"
            : "visible windows";
        if (windows.Count == 0) return $"{heading}: none.";
        var lines = new List<string> { $"{heading}: {windows.Count}" };
        lines.AddRange(windows.Select(w =>
            $"  - {w.WindowTitle}\n      {w.ProcessName} (pid {w.ProcessId}) — {w.ExecutablePath}"));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The post_question(wait=true) result. Each terminal names itself and says
    /// what to do next — the negative path carries the live roster so the
    /// caller can substitute on the spot.
    /// </summary>
    public static string RenderWait(WaitResult w)
    {
        var lines = new List<string> { $"thread {w.ThreadId} · status: {w.Status}" };

        switch (w.Status)
        {
            case WaitStatus.Answered:
                lines.Add($"peer response arrived ({w.Messages.Count} message(s)):");
                lines.AddRange(w.Messages.Select(MessageBlock));
                break;

            case WaitStatus.NoPeerOnline:
                lines.Add("no eligible peer is online, so nothing will arrive — do not wait longer.");
                lines.Add(RenderRoster(w.OnlineNow ?? []));
                lines.Add("substitute: repost with target_agent=<a name above> (and a specialty they cover), " +
                          "or proceed solo and note it.");
                break;

            case WaitStatus.Timeout:
                if (w.Messages.Count > 0)
                {
                    lines.Add("time up; partial responses that did arrive:");
                    lines.AddRange(w.Messages.Select(MessageBlock));
                }
                else
                {
                    lines.Add("time up with no response yet. the thread stays open — check back with " +
                              "read_thread, or keep going and a late review will be there.");
                }
                break;
        }

        return string.Join('\n', lines);
    }

    public static string RenderHits(IReadOnlyList<SearchHit> hits, int maxChars = 0)
    {
        if (hits.Count == 0) return "no matches.";
        var lines = new List<string> { $"matches: {hits.Count} (best first)" };
        var used = lines[0].Length;
        for (var i = 0; i < hits.Count; i++)
        {
            var line = ThreadLine(hits[i].Thread) + "\n    …" + hits[i].Snippet.Replace("\n", " ");
            if (maxChars > 0 && used + line.Length + 1 > maxChars && i > 0)
            {
                lines.Add($"… max_chars={maxChars} reached: {hits.Count - i} more match(es): " +
                          string.Join(' ', hits.Skip(i).Select(h => $"#{h.Thread.Id}")));
                break;
            }
            lines.Add(line);
            used += line.Length + 1;
        }
        return string.Join('\n', lines);
    }

    public static string RenderStats(ThreadStore.Stats s)
    {
        var cache = s.CacheBudgetBytes > 0
            ? $"memory cache: {s.CachedMessages} message(s) resident, {FormatBytes(s.CacheBytes)} of " +
              $"{FormatBytes(s.CacheBudgetBytes)} budget (compacted messages are recalled from the database)"
            : "memory cache: disabled (set AGORA_CACHE_BYTES to enable, default 1 GiB)";
        return $"db: {s.DbPath}\n" +
               $"threads: {s.ThreadsOpen} open, {s.ThreadsAnswered} answered, {s.ThreadsClosed} closed " +
               $"({s.ThreadsOpen + s.ThreadsAnswered + s.ThreadsClosed} total)\n" +
               $"messages: {s.Messages}\n" +
               $"agents online: {s.AgentsOnline}\n" +
               cache;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):F1} GiB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MiB"
        : bytes >= 1 << 10 ? $"{bytes / (double)(1 << 10):F1} KiB"
        : $"{bytes} B";

    /// <summary>"2026-09-04T13:22:41.123Z" → "2026-09-04 13:22" — enough to order events, cheap to read.</summary>
    private static string ShortTime(string iso) =>
        iso.Length >= 16 ? iso[..16].Replace('T', ' ') : iso;

    private static string Age(string iso)
    {
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
            return "?";
        var seconds = (long)(DateTime.UtcNow - t).TotalSeconds;
        return seconds < 0 ? "just now"
            : seconds < 60 ? $"{seconds}s"
            : seconds < 3600 ? $"{seconds / 60}m"
            : $"{seconds / 3600}h";
    }

    /// <summary>
    /// An agent's saved working state — the cheap answer to "where was I".
    /// Labelled as the agent's own note: it is a claim, not recorded state.
    /// </summary>
    public static string RenderResume(IReadOnlyList<AgentNote> notes, string agent)
    {
        if (notes.Count == 0)
            return $"no saved notes for {agent}. Save one with a note call " +
                   "(agent_notes, or `agora note --agent NAME`) before the session ends.";

        var lines = new List<string> { $"RESUME — {notes.Count} note slot(s) for {agent}" };
        foreach (var n in notes)
        {
            lines.Add($"— slot '{n.Slot}' (saved {Age(n.CreatedAt)} ago by {n.Agent}; the agent's own note, " +
                      "not a verified record) —");
            lines.AddRange(Indent(n.Body).Split('\n').Select(l => "  " + l));
            lines.Add($"  ≈{TokenEstimate.ApproxTokens(n.Body)} estimated tokens");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Serve a memo, showing everything the reader needs to distrust it.</summary>
    public static string RenderMemo(CachedAnswer memo, string asked)
    {
        var age = AnswerCacheStore.Age(memo.CreatedAt);
        var days = (int)age.TotalDays;
        var ageText = days >= 1 ? $"{days}d" : $"{(int)age.TotalHours}h";
        var src = memo.SourceThreadId is { } t
            ? $" (from thread #{t}" + (memo.SourceMsgId is { } m ? $" #{m}" : "") + ")"
            : "";
        return "MEMO HIT — an answer previously filed on this board, not a fresh derivation.\n" +
               $"  asked: {asked}\n" +
               $"  filed by {memo.FiledBy} {ageText} ago{src}, served {memo.Hits} time(s)\n" +
               $"  age: {ageText} — answers go stale; re-ask if this looks dated.\n" +
               $"  (file a correction with a memo call, or re-ask with cache=0 for a fresh take)\n" +
               $"— MEMO ANSWER (filed by a peer — a recorded claim, verify before relying on it) —\n" +
               Indent(memo.Answer);
    }

    public static string RenderMemoList(IReadOnlyList<CachedAnswer> memos, int total)
    {
        if (memos.Count == 0) return "no memos filed.";
        var lines = new List<string> { $"memos: {memos.Count} shown (newest first)" };
        lines.AddRange(memos.Select(m =>
        {
            var src = m.SourceThreadId is { } t ? $" (thread #{t})" : "";
            return $"  #{m.Id} [{Age(m.CreatedAt)} ago, {m.Hits} hit(s), by {m.FiledBy}{src}]\n" +
                   $"      Q: {Shorten(m.Question, 100)}\n" +
                   $"      A: {Shorten(m.Answer.Replace('\n', ' '), 140)}";
        }));
        return string.Join('\n', lines);
    }

    public static string RenderTokenReport(IReadOnlyList<TokenUsage> usage,
        IReadOnlyList<(long ThreadId, long Spent, long Saved)> byThread, int sinceDays)
    {
        if (usage.Count == 0)
            return $"no token activity recorded in the last {sinceDays} day(s). " +
                   "Agents record their own usage with a tokens call (kind=spend) and the caches " +
                   "record what they saved.";

        var spent = usage.Sum(u => u.Spent);
        var saved = usage.Sum(u => u.Saved);
        var lines = new List<string>
        {
            $"TOKEN REPORT — last {sinceDays} day(s)",
            "  spend is SELF-REPORTED by each agent (only its client sees real counts);",
            "  saved is measured here from bytes that did not have to be re-read;",
            "  both are ≈estimates (TokenEstimate, ~4 chars/token).",
            "",
        };
        var width = usage.Max(u => u.Agent.Length);
        foreach (var u in usage)
        {
            var pct = spent > 0 ? $" ({100.0 * u.Spent / spent:F0}%)" : "";
            lines.Add($"  {u.Agent.PadRight(width)}  spent {u.Spent,10:N0}{pct}   saved {u.Saved,10:N0}");
        }
        lines.Add("");
        lines.Add($"  total: spent {spent:N0}, saved {saved:N0}" +
                  (spent > 0 ? $" — net {spent - saved:N0}" : ""));
        if (byThread.Count > 0)
        {
            lines.Add("");
            lines.Add("  by thread (biggest spend first):");
            lines.AddRange(byThread.Select(t =>
                $"    #{t.ThreadId}: spent {t.Spent:N0}, saved {t.Saved:N0}"));
        }
        return string.Join('\n', lines);
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string RenderTurn(SqliteConnection db, long threadId)
    {
        var turn = TurnStore.CurrentTurn(db, threadId);
        if (turn is null) return $"turn: thread {threadId} does not exist.";

        var history = TurnStore.ForThread(db, threadId);
        var lines = new List<string>
        {
            $"Thread #{threadId} Turn State:",
            $"  Holder: {turn.Holder}" + (turn.Role.Length > 0 ? $" (role: {turn.Role})" : ""),
            $"  Stage: {turn.Stage}",
            $"  Instruction: {turn.Instruction}",
        };
        if (turn.FromAgent.Length > 0)
            lines.Add($"  Handed from: {turn.FromAgent}" + (turn.FromRole.Length > 0 ? $" ({turn.FromRole})" : ""));
        lines.Add($"  Updated: {ShortTime(turn.UpdatedAt)}");

        if (history.Count > 0)
        {
            lines.Add($"Pass history ({history.Count} event(s)):");
            foreach (var h in history)
            {
                var fRole = h.FromRole.Length > 0 ? $" [{h.FromRole}]" : "";
                var tRole = h.ToRole.Length > 0 ? $" [{h.ToRole}]" : "";
                var st = h.Stage.Length > 0 ? $" (stage: {h.Stage})" : "";
                lines.Add($"  #{h.Id} [{ShortTime(h.CreatedAt)}] {h.FromAgent}{fRole} → {h.ToAgent}{tRole}{st}: {h.Instruction}");
            }
        }
        else
        {
            lines.Add("  (Initial turn held by thread creator; no pass events recorded yet)");
        }

        return string.Join('\n', lines);
    }
}
