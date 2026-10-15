using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// Agent notes: an agent's own working state, saved so a fresh session does
/// not have to re-derive it.
///
/// <para>The expensive case this exists for: an agent hits a usage limit (or a
/// watcher spawns a cold session) and the next session starts from nothing.
/// Without a note it reconstructs "where was I" by re-reading threads — the
/// most expensive possible way to recover context. With one, it calls
/// <c>resume</c> and gets a few hundred tokens back.</para>
///
/// <para>Append-only: a note is never edited, a newer note is a new row, and
/// the CURRENT note for a slot is the newest. Slots let one agent keep several
/// independent notes (one per workstream) without them overwriting each
/// other's meaning.</para>
/// </summary>
public static class AgentNoteStore
{
    public const string DefaultSlot = "default";

    /// <summary>Save a note. Validated: the agent is named and the body is non-empty.</summary>
    public static AgentNote Save(SqliteConnection db, string agent, string body, string slot = DefaultSlot)
    {
        agent = (agent ?? "").Trim();
        if (agent.Length == 0)
            throw new AgoraException("note: every note names its agent — pass the calling agent's name.");

        slot = (slot ?? "").Trim();
        if (slot.Length == 0) slot = DefaultSlot;

        body = (body ?? "").Trim();
        if (body.Length == 0)
            throw new AgoraException("note: the note body is empty — say what the next session should know.");

        var insert = db.Prepare("""
            INSERT INTO agent_notes(agent, slot, body)
            VALUES ($a, $s, $b)
            RETURNING id, created_at
            """, "$a", "$s", "$b");
        var (id, createdAt) = insert.SetAll(agent, slot, body)
            .Query(r => (Id: r.GetInt64("id"), CreatedAt: r.GetString("created_at"))).Single();

        return new AgentNote
        {
            Id = id, Agent = agent, Slot = slot, Body = body, CreatedAt = createdAt,
        };
    }

    /// <summary>The newest note in a slot, or NULL when that slot has none.</summary>
    public static AgentNote? Current(SqliteConnection db, string agent, string slot = DefaultSlot) =>
        Read(db, agent, slot, 1).FirstOrDefault();

    /// <summary>Every slot's current note for an agent — the "what was I doing" view.</summary>
    public static IReadOnlyList<AgentNote> CurrentForAgent(SqliteConnection db, string agent)
    {
        agent = (agent ?? "").Trim();
        if (agent.Length == 0) return [];

        return db.Prepare("""
            SELECT n.id, n.agent, n.slot, n.body, n.created_at
            FROM agent_notes n
            JOIN (SELECT slot, MAX(id) AS id FROM agent_notes WHERE agent = $a GROUP BY slot) newest
              ON newest.id = n.id
            ORDER BY n.slot
            """, "$a")
          .Set("$a", agent)
          .Query(r => new AgentNote
          {
              Id = r.GetInt64("id"),
              Agent = r.GetString("agent"),
              Slot = r.GetString("slot"),
              Body = r.GetString("body"),
              CreatedAt = r.GetString("created_at"),
          });
    }

    /// <summary>An agent's note history in one slot, newest first.</summary>
    public static IReadOnlyList<AgentNote> History(SqliteConnection db, string agent, string slot = DefaultSlot) =>
        Read(db, agent, slot, int.MaxValue);

    private static IReadOnlyList<AgentNote> Read(SqliteConnection db, string agent, string slot, int limit) =>
        db.Prepare("""
            SELECT id, agent, slot, body, created_at
            FROM agent_notes
            WHERE agent = $a AND slot = $s
            ORDER BY id DESC
            LIMIT $n
            """, "$a", "$s", "$n")
          .SetAll((agent ?? "").Trim(), (slot ?? "").Trim() is { Length: > 0 } s ? s : DefaultSlot, limit)
          .Query(r => new AgentNote
          {
              Id = r.GetInt64("id"),
              Agent = r.GetString("agent"),
              Slot = r.GetString("slot"),
              Body = r.GetString("body"),
              CreatedAt = r.GetString("created_at"),
          });
}
