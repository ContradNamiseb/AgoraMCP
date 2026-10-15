using AgoraMCP.Database;
using AgoraMCP.Models;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The in-memory chat box: bodies resident for fast reads, write-through to
/// SQLite always, oldest-first compaction when the byte budget is hit, and
/// database recall for anything compacted out.
/// </summary>
public sealed class MessageCacheTests
{
    private static ThreadMessage Message(long id, string body) => new()
    {
        Id = id,
        ThreadId = 1,
        Author = "a",
        Kind = "question",
        Body = body,
        CreatedAt = "2026-09-04T00:00:00.000Z",
    };

    [Fact]
    public void Remember_then_get_is_the_fast_path()
    {
        var cache = new MessageCache();
        cache.Remember(Message(1, "resident body"));

        Assert.True(cache.TryGet(1, out var hit));
        Assert.Equal("resident body", hit!.Body);
        Assert.False(cache.TryGet(2, out _));
    }

    [Fact]
    public void Budget_hit_compacts_oldest_first()
    {
        // Each entry ~50 body bytes + 96 overhead ≈ 146; budget fits exactly two.
        var cache = new MessageCache(budgetBytes: 300);

        cache.Remember(Message(1, new string('a', 50)));
        cache.Remember(Message(2, new string('b', 50)));
        Assert.Equal(2, cache.Usage.Messages);

        cache.Remember(Message(3, new string('c', 50))); // over budget → evict #1

        Assert.Equal(2, cache.Usage.Messages);
        Assert.True(cache.Usage.Bytes <= 300);
        Assert.False(cache.TryGet(1, out _), "oldest message should have been compacted out");
        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void Oversized_message_is_not_cached_but_still_served_by_the_database()
    {
        var cache = new MessageCache(budgetBytes: 100);
        cache.Remember(Message(1, new string('x', 500)));

        Assert.Equal(0, cache.Usage.Messages);
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void Zero_budget_disables_the_cache()
    {
        var cache = new MessageCache(budgetBytes: 0);
        cache.Remember(Message(1, "anything"));
        Assert.Equal(0, cache.Usage.Messages);
    }

    [Fact]
    public void Writes_go_through_and_compacted_messages_are_recalled_from_the_database()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        var cache = new MessageCache();
        var id = ThreadStore.PostQuestion(db, "cache question", "context", "poster", cache: cache);
        var review = ThreadStore.SubmitReview(db, id, "review body", "peer", cache);
        ThreadStore.Reply(db, id, "reply body", "poster", cache);

        // Write-through: every appended message is resident immediately.
        Assert.True(cache.TryGet(review.Id, out _));
        var (count, _) = cache.Usage;
        Assert.Equal(3, count);

        // Recall: a cold cache (the compacted case) rehydrates from SQLite and
        // reads exactly what the warm one served.
        var warm = ThreadStore.ReadThread(db, id, cache);
        var cold = ThreadStore.ReadThread(db, id, new MessageCache());
        Assert.Equal(warm.Messages.Select(m => (m.Id, m.Body)),
                     cold.Messages.Select(m => (m.Id, m.Body)));
        Assert.Equal(3, cold.Messages.Count);
    }

    [Fact]
    public void Budget_resolves_from_environment()
    {
        Assert.Equal(1234, MessageCache.ResolveBudget(1234));
        Assert.Equal(MessageCache.DefaultBudgetBytes, MessageCache.ResolveBudget());
        Environment.SetEnvironmentVariable(MessageCache.BudgetEnvVar, "42");
        try { Assert.Equal(42, MessageCache.ResolveBudget()); }
        finally { Environment.SetEnvironmentVariable(MessageCache.BudgetEnvVar, null); }
        Environment.SetEnvironmentVariable(MessageCache.BudgetEnvVar, "not-a-number");
        try { Assert.Equal(MessageCache.DefaultBudgetBytes, MessageCache.ResolveBudget()); }
        finally { Environment.SetEnvironmentVariable(MessageCache.BudgetEnvVar, null); }
    }
}
