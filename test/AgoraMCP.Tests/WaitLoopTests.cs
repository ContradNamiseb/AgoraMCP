using AgoraMCP.Database;
using AgoraMCP.Models;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The UCI 'isready' rule: every bounded wait ends in a terminal answer —
/// answered, no_peer_online, or timeout. Never silence, never a hang.
/// </summary>
public sealed class WaitLoopTests
{
    private static readonly ThreadStore.WaitOptions Fast =
        new() { PollIntervalSeconds = 0.2, TimeoutSeconds = 10 };

    [Fact]
    public async Task No_peer_online_fails_fast_with_roster()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var id = ThreadStore.PostQuestion(db, "anyone?", "", "poster");

        var start = DateTime.UtcNow;
        var result = await ThreadStore.WaitForAnswerAsync(db, id, Fast with { Author = "poster" });
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(WaitStatus.NoPeerOnline, result.Status);
        Assert.Empty(result.Messages);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"fast-fail took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task Specialty_miss_returns_roster_for_substitution()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        // Someone IS online — but under a different specialty. The negative
        // path must still say so, carrying the roster to substitute against.
        PresenceStore.Heartbeat(db, "gemini-research", "research", "Gemini, deep retrieval");
        var id = ThreadStore.PostQuestion(db, "kernel question", "", "poster", specialty: "kernel");

        var result = await ThreadStore.WaitForAnswerAsync(db, id,
            Fast with { Author = "poster", Specialty = "kernel" });

        Assert.Equal(WaitStatus.NoPeerOnline, result.Status);
        Assert.NotNull(result.OnlineNow);
        Assert.Contains(result.OnlineNow!, p => p.Agent == "gemini-research");
    }

    [Fact]
    public async Task Directed_wait_needs_that_exact_agent()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        PresenceStore.Heartbeat(db, "other-peer", "kernel");
        var id = ThreadStore.PostQuestion(db, "for you specifically", "", "poster", directedTo: "gemini");

        var miss = await ThreadStore.WaitForAnswerAsync(db, id,
            Fast with { Author = "poster", TargetAgent = "gemini" });
        Assert.Equal(WaitStatus.NoPeerOnline, miss.Status);

        PresenceStore.Heartbeat(db, "gemini", "research");
        // A fresh thread for the hit case: directed at gemini, gemini online.
        var id2 = ThreadStore.PostQuestion(db, "for you, again", "", "poster", directedTo: "gemini");
        var reviewTask = Task.Run(async () =>
        {
            await Task.Delay(600);
            using var peer = temp.Open();
            ThreadStore.SubmitReview(peer, id2, "here", "gemini");
        });
        var hit = await ThreadStore.WaitForAnswerAsync(db, id2,
            Fast with { Author = "poster", TargetAgent = "gemini" });
        await reviewTask;

        Assert.Equal(WaitStatus.Answered, hit.Status);
        Assert.Equal("gemini", hit.Messages.Single().Author);
    }

    [Fact]
    public async Task Review_midwait_returns_answered_and_warms_cache()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        PresenceStore.Heartbeat(db, "reviewer", "kernel");
        var cache = new MessageCache();
        var id = ThreadStore.PostQuestion(db, "waiting question", "", "poster", specialty: "kernel", cache: cache);

        var reviewer = Task.Run(async () =>
        {
            await Task.Delay(700);
            using var peer = temp.Open();
            ThreadStore.SubmitReview(peer, id, "the answer body", "reviewer");
        });

        var result = await ThreadStore.WaitForAnswerAsync(db, id,
            Fast with { Author = "poster", Specialty = "kernel" }, cache: cache);
        await reviewer;

        Assert.Equal(WaitStatus.Answered, result.Status);
        var message = Assert.Single(result.Messages);
        Assert.Equal("the answer body", message.Body);

        // The wait hydrated through the cache, so the body is resident now.
        Assert.True(cache.TryGet(message.Id, out var cached));
        Assert.Equal("the answer body", cached!.Body);
    }

    [Fact]
    public async Task No_answer_ends_in_timeout_at_the_deadline()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        PresenceStore.Heartbeat(db, "quiet-peer", "kernel");
        var id = ThreadStore.PostQuestion(db, "long question", "", "poster");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await ThreadStore.WaitForAnswerAsync(db, id,
            new ThreadStore.WaitOptions { PollIntervalSeconds = 0.2, TimeoutSeconds = 5 });
        sw.Stop();
        var elapsed = sw.Elapsed.TotalSeconds;

        Assert.Equal(WaitStatus.Timeout, result.Status);
        Assert.Empty(result.Messages);
        // Must wait at least ~4s (proving it respected the 5s timeout) and stop well before
        // the 120s default even when the parallel test suite saturates the thread pool.
        Assert.InRange(elapsed, 4.0, 15.0);
    }

    [Fact]
    public void Timeout_is_clamped_to_safe_bounds()
    {
        Assert.Equal(ThreadStore.MinTimeoutSeconds, ThreadStore.ClampTimeoutSeconds(0));
        Assert.Equal(ThreadStore.MinTimeoutSeconds, ThreadStore.ClampTimeoutSeconds(-99));
        Assert.Equal(120, ThreadStore.ClampTimeoutSeconds(120));
        Assert.Equal(ThreadStore.MaxTimeoutSeconds, ThreadStore.ClampTimeoutSeconds(9999));
    }
}
