using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>
/// Canonical role names in a collaborative agent system.
/// </summary>
public static class TurnRoles
{
    public const string Implementer = "implementer";
    public const string Reviewer = "reviewer";
    public const string Tester = "tester";
    public const string Researcher = "researcher";
}

/// <summary>
/// Standard stages in a role-driven turn workflow.
/// </summary>
public static class TurnStages
{
    public const string InProgress = "in_progress";
    public const string DevTested = "dev_tested";
    public const string Reviewing = "reviewing";
    public const string ReviewApproved = "review_approved";
    public const string ChangesRequested = "changes_requested";
    public const string Done = "done";
}

/// <summary>One immutable turn pass (thread #36): from_agent yields the floor to to_agent with role and stage.</summary>
public sealed record TurnEvent(
    long Id,
    long ThreadId,
    string FromAgent,
    string ToAgent,
    string FromRole,
    string ToRole,
    string Stage,
    string Instruction,
    string CreatedAt);

/// <summary>
/// Snapshot of a thread's current turn holder, role, and workflow stage.
/// </summary>
public sealed record TurnState(
    long ThreadId,
    string Holder,
    string Role,
    string Stage,
    string Instruction,
    string FromAgent,
    string FromRole,
    string UpdatedAt)
{
    public override string ToString()
    {
        var rolePart = Role.Length > 0 ? $" [{Role}]" : "";
        var stagePart = Stage.Length > 0 ? $" (stage: {Stage})" : "";
        var fromPart = FromAgent.Length > 0
            ? $" — handed off by {FromAgent}" + (FromRole.Length > 0 ? $" [{FromRole}]" : "")
            : "";
        return $"{Holder}{rolePart}{stagePart}{fromPart}: {Instruction}";
    }
}

/// <summary>
/// The strict turn-token protocol (thread #36, gemini's design as amended in
/// my #397): exactly one agent holds a thread's turn at a time. Events are
/// INSERT-ONLY — the current holder is the to_agent of the newest event,
/// defaulting to threads.created_by when no events exist (the asker starts
/// with the floor). History is immutable; corrections are new events.
/// Role-driven enhancements track roles (implementer, reviewer, tester, researcher)
/// and stages (dev_tested, reviewing, review_approved, etc.).
/// </summary>
public static class TurnStore
{
    /// <summary>Record a turn pass. Validated: existing thread, named actors, to_agent exists as a board agent is NOT required (an agent may hold a turn before its first heartbeat) — but to_agent must differ from from_agent (a self-pass is meaningless) and the instruction must be non-empty (a turn without a stated action is the deadlock this protocol exists to prevent).</summary>
    public static TurnEvent Pass(
        SqliteConnection db,
        long threadId,
        string fromAgent,
        string toAgent,
        string instruction,
        string fromRole = "",
        string toRole = "",
        string stage = "",
        long companionMsgId = 0)
    {
        if (threadId <= 0)
            throw new AgoraException($"turn: thread id {threadId} is not a thread.");
        RequireThread(db, threadId);
        if (fromAgent.Trim().Length == 0)
            throw new AgoraException("turn: every pass names the passing agent.");
        if (toAgent.Trim().Length == 0)
            throw new AgoraException("turn: every pass names the receiving agent.");
        fromAgent = AgentIdentity.ValidateName(fromAgent, "from_agent");
        toAgent = AgentIdentity.ValidateName(toAgent, "pass_to");
        if (toAgent == fromAgent)
            throw new AgoraException(
                $"turn: passing the turn to yourself ({fromAgent}) is meaningless — the holder is unchanged.");
        if (instruction.Trim().Length == 0)
            throw new AgoraException(
                "turn: instruction is required — a turn without a stated action is the deadlock this protocol exists to prevent.");

        var atMsgId = db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM messages");
        if (companionMsgId > atMsgId) atMsgId = companionMsgId;

        using var cmd = db.Prepare("""
            INSERT INTO turn_events(thread_id, from_agent, to_agent, from_role, to_role, stage, instruction, at_msg_id, msg_id)
            VALUES ($t, $f, $to, $fr, $tr, $s, $i, $at_msg, $msg_id)
            """, "$t", "$f", "$to", "$fr", "$tr", "$s", "$i", "$at_msg", "$msg_id");
        cmd.SetAll(
            threadId,
            fromAgent.Trim(),
            toAgent.Trim(),
            fromRole.Trim(),
            toRole.Trim(),
            stage.Trim(),
            instruction.Trim(),
            atMsgId,
            companionMsgId);
        cmd.ExecuteNonQuery();
        return ForThread(db, threadId).Last(e => e.ToAgent == toAgent.Trim());
    }

    /// <summary>The full immutable pass history, oldest first.</summary>
    public static IReadOnlyList<TurnEvent> ForThread(SqliteConnection db, long threadId) =>
        db.Prepare("""
            SELECT id, thread_id, from_agent, to_agent, from_role, to_role, stage, instruction, created_at
            FROM turn_events WHERE thread_id = $t ORDER BY id
            """, "$t")
           .Set("$t", threadId)
           .Query(r => new TurnEvent(
               r.GetInt64("id"),
               r.GetInt64("thread_id"),
               r.GetString("from_agent"),
               r.GetString("to_agent"),
               r.GetString("from_role"),
               r.GetString("to_role"),
               r.GetString("stage"),
               r.GetString("instruction"),
               r.GetString("created_at")));

    /// <summary>
    /// The thread's current turn state: holder, role, stage, instruction, and previous sender.
    /// Returns null only when the thread itself does not exist.
    /// </summary>
    public static TurnState? CurrentTurn(SqliteConnection db, long threadId)
    {
        var threadInfo = db.Prepare("SELECT id, created_by, created_at FROM threads WHERE id = $t", "$t")
            .Set("$t", threadId)
            .Query(r => (Id: r.GetInt64("id"), Creator: r.GetString("created_by"), CreatedAt: r.GetString("created_at")))
            .FirstOrDefault();
        if (threadInfo.Id == 0) return null;

        var lastEvent = db.Prepare("""
            SELECT to_agent, to_role, stage, instruction, from_agent, from_role, created_at
            FROM turn_events WHERE thread_id = $t ORDER BY id DESC LIMIT 1
            """, "$t")
           .Set("$t", threadId)
           .Query(r => (
               Holder: r.GetString("to_agent"),
               Role: r.GetString("to_role"),
               Stage: r.GetString("stage"),
               Instruction: r.GetString("instruction"),
               FromAgent: r.GetString("from_agent"),
               FromRole: r.GetString("from_role"),
               CreatedAt: r.GetString("created_at")))
           .FirstOrDefault();

        if (lastEvent.Holder is not null)
        {
            return new TurnState(
                threadId,
                lastEvent.Holder,
                lastEvent.Role,
                lastEvent.Stage,
                lastEvent.Instruction,
                lastEvent.FromAgent,
                lastEvent.FromRole,
                lastEvent.CreatedAt);
        }

        return new TurnState(
            threadId,
            threadInfo.Creator,
            "asker",
            TurnStages.InProgress,
            "Initial thread floor",
            "",
            "",
            threadInfo.CreatedAt);
    }

    /// <summary>
    /// The thread's current turn holder: the to_agent of the newest event, or
    /// threads.created_by when no events exist (the asker starts with the
    /// floor). Returns null only when the thread itself does not exist.
    /// </summary>
    public static string? CurrentHolder(SqliteConnection db, long threadId) =>
        CurrentTurn(db, threadId)?.Holder;

    /// <summary>
    /// All threads where the CURRENT holder is <paramref name="agent"/> — the
    /// "you hold the token" view behind the fetch_pending banner. Holder
    /// matching spans every name the agent has used: turn events are immutable,
    /// so a turn passed to an agent under its former name is still that
    /// agent's turn after a rename.
    /// </summary>
    public static IReadOnlyList<(long ThreadId, string Topic, string Instruction, string Role, string Stage, string FromAgent, string FromRole)> HeldBy(
        SqliteConnection db, string agent)
    {
        var held = new List<(long, string, string, string, string, string, string)>();
        var names = AgentIdentity.NamesFor(agent);
        var threads = db.Prepare("""
            SELECT t.id, t.topic, t.created_by,
                   (SELECT to_agent FROM turn_events WHERE thread_id = t.id ORDER BY id DESC LIMIT 1) AS holder
            FROM threads t
            """)
           .Query(r => (Id: r.GetInt64("id"), Topic: r.GetString("topic"),
                        Creator: r.GetString("created_by"), Holder: r.GetStringOrNull("holder")))
           .Where(t => names.Contains(t.Creator, StringComparer.OrdinalIgnoreCase)
                    || (t.Holder is not null && names.Contains(t.Holder, StringComparer.OrdinalIgnoreCase)));
        foreach (var t in threads)
        {
            var holder = t.Holder ?? t.Creator;
            if (!names.Contains(holder, StringComparer.OrdinalIgnoreCase)) continue;

            var last = db.Prepare("""
                SELECT instruction, to_role, stage, from_agent, from_role
                FROM turn_events WHERE thread_id = $t ORDER BY id DESC LIMIT 1
                """, "$t")
               .Set("$t", t.Id)
               .Query(r => (
                   Instruction: r.GetString("instruction"),
                   Role: r.GetString("to_role"),
                   Stage: r.GetString("stage"),
                   FromAgent: r.GetString("from_agent"),
                   FromRole: r.GetString("from_role")))
               .FirstOrDefault();

            held.Add((
                t.Id,
                t.Topic,
                last.Instruction ?? "",
                last.Role ?? (t.Creator == agent ? "asker" : ""),
                last.Stage ?? TurnStages.InProgress,
                last.FromAgent ?? "",
                last.FromRole ?? ""));
        }
        return held;
    }

    private static void RequireThread(SqliteConnection db, long threadId)
    {
        var exists = db.Prepare("SELECT 1 FROM threads WHERE id = $t", "$t")
            .Set("$t", threadId)
            .Query(_ => 1).Count > 0;
        if (!exists)
            throw ThreadStore.Missing(db, threadId, "turn: ");
    }
}
