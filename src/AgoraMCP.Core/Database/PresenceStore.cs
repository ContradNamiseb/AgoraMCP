using Microsoft.Data.Sqlite;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// Presence: who is around, what they are good at, and how fresh that claim is.
///
/// <para>The heartbeat is the self-announcement — identity, specialty, and a
/// one-line capabilities blurb, upserted in one indexed statement. There is no
/// background reaper: liveness is judged at read time against a TTL, so a
/// crashed peer disappears from the roster within one TTL without anything
/// having to clean up after it.</para>
/// </summary>
public static class PresenceStore
{
    /// <summary>
    /// 3 missed beats at the suggested 30s cadence. A peer that died <90s ago
    /// still counts as online (its wait then ends in timeout, not
    /// no_peer_online — correct and bounded, just slower).
    /// </summary>
    public const int DefaultTtlSeconds = 90;

    /// <summary>Announce or refresh presence. One indexed upsert — trivial for SQLite at any sane cadence.</summary>
    public static PresenceEntry Heartbeat(
        SqliteConnection db,
        string agent,
        string specialty = "",
        string capabilities = "",
        bool offline = false,
        DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
            throw new AgoraException("heartbeat needs an agent name — presence is per-agent identity.");

        agent = AgentIdentity.ValidateName(agent, "agent");
        specialty = specialty.Trim();
        capabilities = capabilities.Trim();
        var seen = (at ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var status = offline ? "offline" : "online";

        db.Prepare("""
            INSERT INTO presence(agent, specialty, capabilities, last_heartbeat, status)
            VALUES ($agent, $specialty, $capabilities, $seen, $status)
            ON CONFLICT(agent) DO UPDATE SET
              specialty = excluded.specialty,
              capabilities = excluded.capabilities,
              last_heartbeat = excluded.last_heartbeat,
              status = excluded.status
            """, "$agent", "$specialty", "$capabilities", "$seen", "$status")
           .SetAll(agent, specialty, capabilities, seen, status)
           .ExecuteNonQuery();

        return new PresenceEntry
        {
            Agent = agent,
            Specialty = specialty,
            Capabilities = capabilities,
            LastHeartbeat = seen,
            Status = status,
        };
    }

    /// <summary>
    /// The live roster: agents whose heartbeat is younger than the TTL, newest
    /// identity data included. Filtered by specialty only when one is given.
    /// </summary>
    public static IReadOnlyList<PresenceEntry> OnlineRoster(
        SqliteConnection db, string specialty = "", int ttlSeconds = DefaultTtlSeconds)
    {
        // julianday arithmetic in SQL keeps this an indexed range check; SQLite
        // parses the ISO-8601 'Z' timestamps the schema defaults write.
        return db.Prepare("""
            SELECT agent, specialty, capabilities, last_heartbeat, status
            FROM presence
            WHERE status = 'online'
              AND julianday('now') - julianday(last_heartbeat) <= $ttl_seconds / 86400.0
              AND ($specialty = '' OR specialty = $specialty)
            ORDER BY agent
            """, "$ttl_seconds", "$specialty")
           .SetAll(ttlSeconds, specialty)
           .Query(ReadEntry);
    }

    private static PresenceEntry ReadEntry(SqliteDataReader r) => new()
    {
        Agent = r.GetString("agent"),
        Specialty = r.GetString("specialty"),
        Capabilities = r.GetString("capabilities"),
        LastHeartbeat = r.GetString("last_heartbeat"),
        Status = r.GetString("status"),
    };
}
