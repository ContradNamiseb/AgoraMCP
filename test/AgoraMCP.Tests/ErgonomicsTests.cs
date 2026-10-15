using System.Text.Json;
using Xunit;
using AgoraMCP.Database;
using AgoraMCP.McpServer;

namespace AgoraMCP.Tests;

/// <summary>
/// The agent-facing contract: argument repair, enum-typed schemas, the
/// compound respond call, size caps, and refusals that name valid keys. Most
/// run through a live `agora serve`, because the guard sits in the SDK's
/// request pipeline and only a real client exercises that path.
/// </summary>
public sealed class ErgonomicsTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static readonly JsonElement ReplySchema = Schema("""
        {"type":"object","properties":{
          "thread_id":{"type":"integer"},
          "body":{"type":"string"},
          "author":{"type":["string","null"],"default":null},
          "stage":{"type":["string","null"],"enum":["in_progress","reviewing","review_approved",null],"default":null},
          "ids":{"type":"array","items":{"type":"integer"}}},
         "required":["thread_id","body"]}
        """);

    [Fact]
    public void Guard_maps_aliases_camel_case_and_identity_names()
    {
        var outcome = ToolArgumentGuard.Normalize("reply", ReplySchema,
            Args(new { threadId = "#12", message = "hi", agent = "codex" }));

        Assert.Null(outcome.Error);
        Assert.Equal(12, outcome.Arguments["thread_id"].GetInt64());
        Assert.Equal("hi", outcome.Arguments["body"].GetString());
        Assert.Equal("codex", outcome.Arguments["author"].GetString());
        Assert.Contains(outcome.Notes, n => n.Contains("'message' as 'body'"));
    }

    [Fact]
    public void Guard_matches_enums_by_case_and_unique_prefix_and_refuses_ambiguity()
    {
        var cased = ToolArgumentGuard.Normalize("t", ReplySchema, Args(new { thread_id = 1, body = "b", stage = "In-Progress" }));
        Assert.Equal("in_progress", cased.Arguments["stage"].GetString());

        var prefix = ToolArgumentGuard.Normalize("t", ReplySchema, Args(new { thread_id = 1, body = "b", stage = "review_appr" }));
        Assert.Equal("review_approved", prefix.Arguments["stage"].GetString());

        var ambiguous = ToolArgumentGuard.Normalize("t", ReplySchema, Args(new { thread_id = 1, body = "b", stage = "review" }));
        Assert.NotNull(ambiguous.Error);
        Assert.Contains("use one of: in_progress, reviewing, review_approved", ambiguous.Error);
    }

    [Fact]
    public void Guard_names_missing_and_unknown_parameters_with_the_signature()
    {
        var missing = ToolArgumentGuard.Normalize("reply", ReplySchema, Args(new { thread_id = 1, bogus = 2 }));
        Assert.NotNull(missing.Error);
        Assert.Contains("missing required body", missing.Error);
        Assert.Contains("unknown 'bogus'", missing.Error);
        Assert.Contains("reply(thread_id: integer!, body: string!", missing.Error);

        // With everything required present, an unknown name only earns a note.
        var extra = ToolArgumentGuard.Normalize("reply", ReplySchema, Args(new { thread_id = 1, body = "b", bogus = 2 }));
        Assert.Null(extra.Error);
        Assert.Contains(extra.Notes, n => n.StartsWith("ignored unknown 'bogus'"));
    }

    [Fact]
    public void Guard_treats_null_or_empty_optional_values_as_omitted_and_splits_lists()
    {
        var outcome = ToolArgumentGuard.Normalize("t", ReplySchema,
            Args(new { thread_id = 1, body = "b", author = (string?)null, stage = "", ids = "3, #4,5" }));
        Assert.Null(outcome.Error);
        Assert.False(outcome.Arguments.ContainsKey("author"));
        Assert.False(outcome.Arguments.ContainsKey("stage"));
        Assert.Equal([3L, 4L, 5L], outcome.Arguments["ids"].EnumerateArray().Select(e => e.GetInt64()).ToArray());
    }

    [Fact]
    public void Slim_drops_null_members_and_null_defaults_only()
    {
        var slim = ToolArgumentGuard.Slim(ReplySchema);
        var props = slim.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("author").GetProperty("type").GetString());
        Assert.False(props.GetProperty("author").TryGetProperty("default", out _));
        Assert.DoesNotContain(props.GetProperty("stage").GetProperty("enum").EnumerateArray(),
            e => e.ValueKind == JsonValueKind.Null);
        Assert.Equal(slim.GetRawText(), ToolArgumentGuard.Slim(slim).GetRawText());
    }

    [Fact]
    public void Live_schema_carries_enums_and_compact_descriptions()
    {
        using var temp = new TempDb();
        using var client = new McpStdioClient(temp.DbPath);
        var tools = client.ListToolSchemas().ToDictionary(t => t.GetProperty("name").GetString()!);

        var verdict = tools["respond"].GetProperty("inputSchema").GetProperty("properties").GetProperty("verdict");
        foreach (var removed in new[] { "reply", "submit_peer_review", "record_review", "pass_turn" })
            Assert.False(tools.ContainsKey(removed), $"{removed} was folded into respond");
        Assert.Equal(["approved", "changes_required", "rejected", "awaiting_review"],
            verdict.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.All(tools.Values, t => Assert.DoesNotContain('\n', t.GetProperty("description").GetString()!));
    }

    [Fact]
    public void Live_aliases_reach_the_tool_and_bad_enums_come_back_as_errors()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
            thread = ThreadStore.PostQuestion(db, "alias thread", "", "peer-a");
        using var client = new McpStdioClient(temp.DbPath);

        var reply = client.CallTool("respond", new { threadId = $"#{thread}", message = "via aliases", agent = "peer-b" });
        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("(guard: read 'threadId' as 'thread_id'", reply.Text);
        using (var db = temp.Open())
            Assert.Equal("peer-b", ThreadStore.ReadThread(db, thread).Messages.Last().Author);

        var bad = client.CallTool("respond", new { thread_id = thread, verdict = "maybe", revision = "r1", author = "peer-b" });
        Assert.True(bad.IsError);
        Assert.Contains("use one of: approved, changes_required, rejected, awaiting_review", bad.Text);
    }

    [Fact]
    public void Respond_posts_records_passes_closes_acks_and_returns_only_the_delta()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
        {
            thread = ThreadStore.PostQuestion(db, "review my change", "", "implementer-a");
            ThreadStore.Reply(db, thread, "context from the implementer", "implementer-a");
        }
        using var client = new McpStdioClient(temp.DbPath);

        // The reviewer reads (records a receipt), then the implementer adds one more message.
        client.CallTool("read_thread", new { thread_id = thread, agent = "reviewer-b" });
        long late;
        using (var db = temp.Open())
            late = ThreadStore.Reply(db, thread, "one more detail", "implementer-a").Id;

        var result = client.CallTool("respond", new
        {
            thread_id = thread,
            body = "approved with nits",
            kind = "review",
            author = "reviewer-b",
            verdict = "approved",
            revision = "rev-1",
            pass_to = "implementer-a",
            stage = "review_approved",
            status = "closed",
        });

        Assert.False(result.IsError, result.Text);
        Assert.Contains("posted review #", result.Text);
        Assert.Contains("verdict APPROVED @ rev rev-1", result.Text);
        Assert.Contains("turn → implementer-a", result.Text);
        Assert.Contains("status answered → closed", result.Text);
        // The late message was unseen, so the ack stops at what had been read.
        Assert.Contains("1 unseen message(s) below stay pending", result.Text);
        Assert.Contains($"#{late} [reply] implementer-a", result.Text);
        Assert.DoesNotContain("context from the implementer", result.Text);

        using var check = temp.Open();
        Assert.Equal(ThreadStore.StatusClosed, ThreadStore.ReadThread(check, thread).Header.Status);
        Assert.Equal("approved", ReviewStore.Current(check, thread)!.State);
        Assert.Equal("implementer-a", TurnStore.CurrentHolder(check, thread));
        var cursor = RecipientStore.ForThread(check, thread).Single(c => c.Agent == "reviewer-b");
        Assert.True(cursor.LastAckMsgId < late);
        Assert.Equal(late, cursor.LastSeenMsgId);
    }

    [Fact]
    public void Respond_refuses_an_incomplete_call_before_writing_anything()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
            thread = ThreadStore.PostQuestion(db, "q", "", "peer-a");
        using var client = new McpStdioClient(temp.DbPath);

        var result = client.CallTool("respond", new
        {
            thread_id = thread, body = "text", author = "peer-b", verdict = "approved",
        });

        Assert.True(result.IsError);
        Assert.Contains("verdict needs revision", result.Text);
        Assert.Contains("Nothing was written", result.Text);
        using var check = temp.Open();
        Assert.Single(ThreadStore.ReadThread(check, thread).Messages);
    }

    [Fact]
    public void Capped_read_names_the_unshown_ids_and_the_cursor_to_continue()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
        {
            thread = ThreadStore.PostQuestion(db, "long", "", "peer-a");
            for (var i = 0; i < 6; i++) ThreadStore.Reply(db, thread, new string('x', 200) + i, "peer-a");
        }
        using var client = new McpStdioClient(temp.DbPath);

        var first = client.CallTool("read_thread", new { thread_id = thread, max_chars = 700 });
        Assert.Contains("max_chars reached", first.Text);
        var resume = long.Parse(first.Text[(first.Text.LastIndexOf("since_id=", StringComparison.Ordinal) + 9)..].TrimEnd('.'));

        var rest = client.CallTool("read_thread", new { thread_id = thread, since_id = resume, max_chars = 0 });
        Assert.DoesNotContain("max_chars reached", rest.Text);
        Assert.Contains("x5", rest.Text);
    }

    [Fact]
    public void Unknown_ids_are_refused_with_the_valid_ones()
    {
        using var temp = new TempDb();
        long thread;
        using (var db = temp.Open())
            thread = ThreadStore.PostQuestion(db, "only thread", "", "peer-a");
        using var client = new McpStdioClient(temp.DbPath);

        var read = client.CallTool("read_thread", new { thread_id = 999 });
        Assert.True(read.IsError);
        Assert.Contains($"open: #{thread}", read.Text);

        var digest = client.CallTool("save_digest", new
        {
            thread_id = thread, covered_through_msg_id = 999, body = "summary", author = "peer-a",
        });
        Assert.Contains($"thread {thread} holds messages", digest.Text);
    }

    [Fact]
    public void Search_threads_finds_by_keyword_and_respects_the_cap()
    {
        using var temp = new TempDb();
        using (var db = temp.Open())
            for (var i = 0; i < 5; i++)
                ThreadStore.PostQuestion(db, $"widget calibration case {i}", new string('c', 300), "peer-a");
        using var client = new McpStdioClient(temp.DbPath);

        var all = client.CallTool("search_threads", new { query = "widget calibration" });
        Assert.Contains("matches: 5", all.Text);

        var capped = client.CallTool("search_threads", new { query = "widget", max_chars = 200 });
        Assert.Contains("more match(es)", capped.Text);
    }
}
