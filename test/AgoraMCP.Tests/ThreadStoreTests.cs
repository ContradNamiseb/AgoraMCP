using AgoraMCP.Database;
using AgoraMCP.Models;
using Xunit;

namespace AgoraMCP.Tests;

public sealed class ThreadStoreTests
{
    [Fact]
    public void Post_fetch_read_review_round_trip()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        var threadId = ThreadStore.PostQuestion(db, "warp scan layout", "16 tokens, Triton, H100",
            author: "claude-opus", specialty: "kernel");
        Assert.True(threadId > 0);

        // Pending, filterable by specialty, cheap summaries.
        var pending = ThreadStore.FetchPending(db);
        Assert.Single(pending);
        Assert.Equal("warp scan layout", pending[0].Topic);
        Assert.Equal(1, pending[0].MessageCount);

        Assert.Empty(ThreadStore.FetchPending(db, specialty: "research"));

        // A peer reviews; the thread turns answered.
        var review = ThreadStore.SubmitReview(db, threadId,
            "use tl::cumsum along axis=0; layout [lane, warp]", "zcode-glm");
        Assert.Equal("review", review.Kind);

        var detail = ThreadStore.ReadThread(db, threadId);
        Assert.Equal(ThreadStore.StatusAnswered, detail.Header.Status);
        Assert.Equal(2, detail.Messages.Count);
        Assert.Equal("question", detail.Messages[0].Kind);
        Assert.Equal("zcode-glm", detail.Messages[1].Author);

        // Answers leave the pending inbox but stay in history.
        Assert.Empty(ThreadStore.FetchPending(db));
        Assert.Single(ThreadStore.ListThreads(db, status: "answered"));
    }

    [Fact]
    public void Reply_keeps_status_and_closed_threads_accept_reply_not_review()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var id = ThreadStore.PostQuestion(db, "topic", "", "a");
        ThreadStore.SubmitReview(db, id, "answer", "b");

        var reply = ThreadStore.Reply(db, id, "thanks, incorporated", "a");
        Assert.Equal("reply", reply.Kind);
        Assert.Equal(ThreadStore.StatusAnswered, ThreadStore.ReadThread(db, id).Header.Status);

        // No resolve_thread yet, so close by hand via the same rule the tool
        // layer will use when it exists: reviews are refused on closed threads.
        db.Execute("UPDATE threads SET status = 'closed' WHERE id = " + id);
        var ex = Assert.Throws<AgoraException>(() => ThreadStore.SubmitReview(db, id, "late review", "c"));
        Assert.Contains("closed", ex.Message);
        Assert.Contains("reply", ex.Message);

        var lateReply = ThreadStore.Reply(db, id, "comment on the record", "c");
        Assert.Equal("reply", lateReply.Kind);
    }

    [Fact]
    public void Validation_fails_loud()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        Assert.Throws<AgoraException>(() => ThreadStore.PostQuestion(db, "   ", "", "a"));
        Assert.Throws<AgoraException>(() => ThreadStore.PostQuestion(db, "topic", "", "  "));
        Assert.Throws<AgoraException>(() => ThreadStore.PostQuestion(db, "topic", "", "a", directedTo: "a"));

        var unknown = Assert.Throws<AgoraException>(() => ThreadStore.ReadThread(db, 9999));
        Assert.Contains("9999", unknown.Message);
        Assert.Throws<AgoraException>(() => ThreadStore.SubmitReview(db, 9999, "body", "a"));
        Assert.Throws<AgoraException>(() => ThreadStore.Reply(db, 9999, "body", "a"));
        Assert.Throws<AgoraException>(() => ThreadStore.SubmitReview(db,
            ThreadStore.PostQuestion(db, "t", "", "a"), "", "b"));
    }

    [Fact]
    public void Fetch_pagination_and_directed_routing()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        var open = ThreadStore.PostQuestion(db, "open question", "", "claude", specialty: "kernel");
        var directed = ThreadStore.PostQuestion(db, "directed question", "", "claude",
            specialty: "kernel", directedTo: "gemini");
        var research = ThreadStore.PostQuestion(db, "research question", "", "claude", specialty: "research");

        // The target sees the directed thread even under a mismatched
        // specialty filter — plus everything the filter itself matches.
        var geminiSees = ThreadStore.FetchPending(db, agent: "gemini", specialty: "research");
        Assert.Equal(new[] { directed, research }, geminiSees.Select(t => t.Id));

        // Unfiltered, everyone sees everything; the directed one is marked.
        var unfiltered = ThreadStore.FetchPending(db, agent: "zcode");
        Assert.Equal(new[] { open, directed, research }, unfiltered.Select(t => t.Id));
        Assert.Equal(directed, unfiltered.Single(t => t.DirectedTo != "").Id);

        // A non-target with a specialty filter does NOT see the directed thread.
        var filtered = ThreadStore.FetchPending(db, agent: "zcode", specialty: "research");
        Assert.Equal(new[] { research }, filtered.Select(t => t.Id));

        // since_id paging past the first thread.
        var paged = ThreadStore.FetchPending(db, sinceId: open);
        Assert.Equal(new[] { directed, research }, paged.Select(t => t.Id));
    }

    [Fact]
    public void Search_finds_topics_context_and_bodies()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        var id = ThreadStore.PostQuestion(db, "warp scan layout", "Triton on H100",
            "claude", specialty: "kernel");
        ThreadStore.SubmitReview(db, id, "tl::cumsum solves the scan", "glm");

        Assert.Single(ThreadStore.Search(db, "warp"));
        Assert.Single(ThreadStore.Search(db, "triton"));
        Assert.Single(ThreadStore.Search(db, "cumsum"));
        Assert.Empty(ThreadStore.Search(db, "nonexistentterm"));

        // Multi-term AND semantics; on a total AND-miss the OR fallback still
        // surfaces the ranked partial match rather than silence.
        Assert.Single(ThreadStore.Search(db, "warp scan"));
        Assert.Single(ThreadStore.Search(db, "warp nonexistentterm"));

        Assert.Throws<AgoraException>(() => ThreadStore.Search(db, "   "));
    }
}
