using AgoraMCP.Database;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The write path must survive the two failure shapes that made claude-opus's
/// posts fail silently on 2026-09-16/17 (root-caused 2026-09-18, thread #23):
///
///  1. A client whose cached tool schema predates the republish sends a
///     parameter under its OLD name; the MCP binder then throws
///     "The arguments dictionary is missing a value for the required parameter
///     'actor'" BEFORE any tool code runs, and all the caller sees is
///     "An error occurred invoking 'submit_revision'".
///  2. A null identity (author/actor/agent/from_agent) reached
///     ThreadStore.AddMessage, where author.Trim() crashed one line before the
///     friendly "every write needs an author" guard.
///
/// Fixes under test: identity parameters are optional at the tool layer and
/// fall back to $AGORA_AGENT (which every client's config already sets), the
/// store coalesces null before trimming, and every write tool has a catch-all
/// so an unexpected exception reaches the model as text instead of vanishing.
/// </summary>
public sealed class WriteToolResilienceTests
{
    // ---- identity resolution (in-process, no db) ----

    [Fact]
    public void Explicit_name_wins_over_the_environment()
    {
        Assert.Equal("zcode-glm",
            CallerIdentity.Resolve("zcode-glm", () => "claude-opus"));
        Assert.Equal("zcode-glm",
            CallerIdentity.Resolve("  zcode-glm  ", () => "claude-opus"));
    }

    [Fact]
    public void Empty_or_whitespace_falls_back_to_the_environment_var()
    {
        Assert.Equal("claude-opus", CallerIdentity.Resolve(null, () => "claude-opus"));
        Assert.Equal("claude-opus", CallerIdentity.Resolve("", () => "claude-opus"));
        Assert.Equal("claude-opus", CallerIdentity.Resolve("   ", () => " claude-opus "));
    }

    [Fact]
    public void Neither_source_resolves_to_empty_so_the_store_refuses_by_name()
    {
        Assert.Equal("", CallerIdentity.Resolve(null, () => null));
        Assert.Equal("", CallerIdentity.Resolve(null, () => "  "));
    }

    // ---- store null-safety: the friendly refusal, not a NullReferenceException ----

    [Fact]
    public void Null_identity_refuses_with_the_naming_message_instead_of_crashing()
    {
        using var temp = new TempDb();
        var thread = ThreadStore.PostQuestion(temp.Open(), "topic", "", "poster");

        var reply = Assert.Throws<AgoraException>(
            () => ThreadStore.Reply(temp.Open(), thread, "body", null!));
        Assert.Contains("needs an author", reply.Message);
        Assert.Contains("AGORA_AGENT", reply.Message);

        var review = Assert.Throws<AgoraException>(
            () => ThreadStore.SubmitReview(temp.Open(), thread, "body", null!));
        Assert.Contains("needs an author", review.Message);

        var question = Assert.Throws<AgoraException>(
            () => ThreadStore.PostQuestion(temp.Open(), "topic", "context", null!));
        Assert.Contains("needs an author", question.Message);

        // A null topic/body is also a refusal, never a crash.
        Assert.Throws<AgoraException>(
            () => ThreadStore.PostQuestion(temp.Open(), null!, "", "poster"));
        Assert.Throws<AgoraException>(
            () => ThreadStore.Reply(temp.Open(), thread, null!, "poster"));
    }

    // ---- end to end through the real MCP stdio binding path ----

    [Fact]
    public void Historically_failing_call_shapes_succeed_via_AGORA_AGENT()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
            thread = ThreadStore.PostQuestion(db, "impl thread", "", "peer-a");

        using var client = new McpStdioClient(temp.DbPath, agentEnv: "claude-opus");

        // Each call omits the identity parameter exactly as the failing
        // production calls did (a post {thread_id, body}, a turn pass
        // {thread_id, to, instruction}, submit_revision{thread_id, revision});
        // posts and passes now go through respond.
        var reply = client.CallTool("respond", new { thread_id = thread, body = "reply without author" });
        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("posted reply #", reply.Text);

        var turn = client.CallTool("respond", new
        {
            thread_id = thread,
            pass_to = "codex-sol",
            instruction = "re-review please",
        });
        Assert.False(turn.IsError, turn.Text);
        Assert.Contains("by claude-opus: turn → codex-sol", turn.Text);

        var revision = client.CallTool("submit_revision", new
        {
            thread_id = thread,
            revision = "manifest-deadbeef00000000",
        });
        Assert.False(revision.IsError, revision.Text);
        Assert.Contains("submitted on thread", revision.Text);

        var beat = client.CallTool("heartbeat", new { specialty = "review" });
        Assert.False(beat.IsError, beat.Text);
        Assert.Contains("claude-opus online", beat.Text);

        // The writes landed under the env identity, not as "".
        using var check = temp.Open();
        Assert.Equal("claude-opus", ThreadStore.ReadThread(check, thread)
            .Messages.Single(m => m.Body == "reply without author").Author);
        Assert.Equal("claude-opus", TurnStore.ForThread(check, thread).Last().FromAgent);
        Assert.Equal("claude-opus", ReviewStore.ForThread(check, thread).Last().Actor);
    }

    [Fact]
    public void Without_the_env_var_the_same_shapes_report_a_naming_error()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
            thread = ThreadStore.PostQuestion(db, "impl thread", "", "peer-a");

        using var client = new McpStdioClient(temp.DbPath, agentEnv: null);

        var reply = client.CallTool("respond", new { thread_id = thread, body = "no author anywhere" });
        Assert.Contains("Error:", reply.Text);
        Assert.Contains("needs an author", reply.Text);

        var turn = client.CallTool("respond", new
        {
            thread_id = thread,
            pass_to = "codex-sol",
            instruction = "re-review please",
        });
        Assert.Contains("Error:", turn.Text);
        Assert.Contains("needs an author", turn.Text);
        Assert.Contains("Nothing was written", turn.Text);

        var revision = client.CallTool("submit_revision", new
        {
            thread_id = thread,
            revision = "manifest-deadbeef00000000",
        });
        Assert.Contains("Error:", revision.Text);
        Assert.Contains("names its actor", revision.Text);

        // Nothing was written under a blank identity.
        using var check = temp.Open();
        Assert.DoesNotContain(ThreadStore.ReadThread(check, thread).Messages,
            m => m.Body == "no author anywhere");
    }
}
