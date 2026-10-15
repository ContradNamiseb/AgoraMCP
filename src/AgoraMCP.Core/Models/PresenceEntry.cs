namespace AgoraMCP.Models;

/// <summary>
/// One agent's self-announcement: identity, specialty, and a one-line blurb of
/// what it is good at. The heartbeat IS the announcement — nothing else is
/// required for an agent to join the agora.
/// </summary>
public sealed record PresenceEntry
{
    public required string Agent { get; init; }
    public required string Specialty { get; init; }
    public required string Capabilities { get; init; }
    public required string LastHeartbeat { get; init; }
    public required string Status { get; init; }     // online | offline
}
