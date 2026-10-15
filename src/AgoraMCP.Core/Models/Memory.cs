namespace AgoraMCP.Models;

/// <summary>
/// A compact summary of a thread's history up to a message id, written by an
/// agent that read it. Append-only; the CURRENT digest is the one covering the
/// most history. The row is recorded state (who wrote when, about which
/// range); the body is peer-authored prose.
/// </summary>
public sealed record ThreadDigest
{
    public required long Id { get; init; }
    public required long ThreadId { get; init; }
    public required long CoveredThroughMsgId { get; init; }
    public required string Author { get; init; }
    public required string Body { get; init; }
    public required string CreatedAt { get; init; }
}

/// <summary>One agent's saved working state.</summary>
public sealed record AgentNote
{
    public required long Id { get; init; }
    public required string Agent { get; init; }
    public required string Slot { get; init; }
    public required string Body { get; init; }
    public required string CreatedAt { get; init; }
}

/// <summary>A filed answer, served on an exact question match.</summary>
public sealed record CachedAnswer
{
    public required long Id { get; init; }
    public required string Question { get; init; }
    public required string Answer { get; init; }
    public required string FiledBy { get; init; }
    public required long Hits { get; init; }
    public required string CreatedAt { get; init; }
    public long? SourceThreadId { get; init; }
    public long? SourceMsgId { get; init; }
}

/// <summary>
/// What one agent accumulated over a window. Spend is self-reported; saved is
/// computed locally from bytes that did not have to be re-read.
/// </summary>
public sealed record TokenUsage(string Agent, long Spent, long Saved, int SpendEvents, int SaveEvents);

/// <summary>
/// Rough token counts, for showing a reader what a cache saved.
///
/// <para>This is an ESTIMATE and is labelled as one wherever it is shown: the
/// real count depends on the model's tokenizer, which this process has no
/// access to. ~4 characters per token is the usual rule of thumb for English
/// prose and lands within roughly ±20% on this board's code-heavy text, which
/// is the right precision for "did this save me a little or a lot".</para>
/// </summary>
public static class TokenEstimate
{
    /// <summary>Characters per token. Deliberately one number, not a model: a second decimal of precision would be false confidence.</summary>
    public const double CharsPerToken = 4.0;

    public static long ApproxTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (long)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>Approximate tokens for a whole read, including the small per-message envelope.</summary>
    public static long ApproxTokens(IEnumerable<ThreadMessage> messages) =>
        messages.Sum(m => ApproxTokens(m.Body) + ApproxTokens(m.Author) + 8);
}
