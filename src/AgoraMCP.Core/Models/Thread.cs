namespace AgoraMCP.Models;

/// <summary>
/// One collaboration thread: a question on the blackboard and everything said
/// about it. Threads are created open, marked answered by the first peer
/// review, and closed deliberately — never deleted, so the review trail
/// survives.
/// </summary>
public sealed record ThreadHeader
{
    public required long Id { get; init; }
    public required string Topic { get; init; }
    public required string Context { get; init; }
    public required string Specialty { get; init; }
    public required string Status { get; init; }      // open | answered | closed
    public required string DirectedTo { get; init; }  // empty = open to anyone
    public required string CreatedBy { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

/// <summary>Compact shape for polling and lists — no bodies, so a poll is cheap.</summary>
public sealed record ThreadSummary
{
    public required long Id { get; init; }
    public required string Topic { get; init; }
    public required string Specialty { get; init; }
    public required string Status { get; init; }
    public required string DirectedTo { get; init; }
    public required string CreatedBy { get; init; }
    public required int MessageCount { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

/// <summary>
/// One message on a thread. Immutable by rule: corrections arrive as new
/// messages, never edits, so a review's history reads exactly as it happened.
/// </summary>
public sealed record ThreadMessage
{
    public required long Id { get; init; }
    public required long ThreadId { get; init; }
    public required string Author { get; init; }
    public required string Kind { get; init; }        // question | review | reply | note
    public required string Body { get; init; }
    public required string CreatedAt { get; init; }
}

/// <summary>A thread with all of its messages, oldest first.</summary>
public sealed record ThreadDetail
{
    public required ThreadHeader Header { get; init; }
    public required IReadOnlyList<ThreadMessage> Messages { get; init; }
}

/// <summary>One FTS result: the thread it belongs to plus the matching snippet.</summary>
public sealed record SearchHit(ThreadSummary Thread, string Snippet);

/// <summary>Where a bounded wait ended. Every wait ends in one of these — never silence.</summary>
public static class WaitStatus
{
    public const string Answered = "answered";
    public const string NoPeerOnline = "no_peer_online";
    public const string Timeout = "timeout";
}

/// <summary>Outcome of post_question with wait=true.</summary>
public sealed record WaitResult
{
    public required long ThreadId { get; init; }
    /// <summary>answered | no_peer_online | timeout.</summary>
    public required string Status { get; init; }
    /// <summary>Reviews/replies that arrived before the wait ended (empty on no_peer_online).</summary>
    public required IReadOnlyList<ThreadMessage> Messages { get; init; }
    /// <summary>The full live roster, carried on the negative path so the caller can substitute on the spot.</summary>
    public IReadOnlyList<PresenceEntry>? OnlineNow { get; init; }
}
