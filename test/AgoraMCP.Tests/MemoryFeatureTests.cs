using AgoraMCP.Database;
using AgoraMCP.McpServer;
using AgoraMCP.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The token-saving layer: thread digests, agent notes, answer memoization,
/// and the token ledger. Each has one property that matters more than the
/// rest, and those are the ones tested hardest here:
///
///   digest — the briefing must actually send FEWER tokens, and must say which
///            range it replaced and where the raw history still is;
///   note   — a fresh session reads back what the previous one saved, per slot;
///   memo   — an exact match is served, a near match is NOT (a wrong answer
///            served as fact costs more than a miss), and age is enforced;
///   tokens — spend and saved never get confused, and a negative count is
///            refused rather than double-signed.
/// </summary>
public sealed class MemoryFeatureTests
{
    private static long SeedThread(TempDb temp, int messages = 3)
    {
        long id;
        using (var db = temp.Open())
        {
            id = ThreadStore.PostQuestion(db, "long thread", "", "asker");
            for (var i = 0; i < messages - 1; i++)
                ThreadStore.Reply(db, id, $"message {i}", "peer-a");
        }
        return id;
    }

    // ---------- digests ----------

    [Fact]
    public void Digest_requires_a_real_message_on_the_thread()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp);
        long other;
        using (var db = temp.Open()) other = ThreadStore.PostQuestion(db, "other", "", "asker");

        using var check = temp.Open();
        // A coverage claim about a message that does not exist is a false claim
        // about the record — the one thing a digest must never hold.
        var bogus = Assert.Throws<AgoraException>(() =>
            DigestStore.Save(check, thread, 999_999, "summary", "peera"));
        Assert.Contains("not on thread", bogus.Message);

        // A real message, but on a DIFFERENT thread, is equally false.
        var wrongThread = ThreadStore.ReadThread(check, other).Messages.Last().Id;
        Assert.Throws<AgoraException>(() =>
            DigestStore.Save(check, thread, wrongThread, "summary", "peera"));

        Assert.Throws<AgoraException>(() =>
            DigestStore.Save(check, thread, ThreadStore.ReadThread(check, thread).Messages.Last().Id, "  ", "peera"));
        Assert.Throws<AgoraException>(() =>
            DigestStore.Save(check, thread, ThreadStore.ReadThread(check, thread).Messages.Last().Id, "ok", "  "));
        Assert.Throws<AgoraException>(() => DigestStore.Save(check, 999_999, 1, "ok", "peera"));
    }

    [Fact]
    public void Current_digest_is_the_one_covering_the_most_history()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp, messages: 5);
        using var db = temp.Open();
        var ids = ThreadStore.ReadThread(db, thread).Messages.Select(m => m.Id).ToList();

        // A partial digest written LATER must not displace a fuller one.
        DigestStore.Save(db, thread, ids[1], "early, partial", "peer-a");
        DigestStore.Save(db, thread, ids[3], "later, fuller", "peer-b");

        var current = DigestStore.Current(db, thread);
        Assert.NotNull(current);
        Assert.Equal(ids[3], current!.CoveredThroughMsgId);
        Assert.Equal("later, fuller", current.Body);

        // Both are kept: nothing is edited or hidden.
        Assert.Equal(2, DigestStore.ForThread(db, thread).Count);
    }

    [Fact]
    public void Briefing_serves_the_digest_and_actually_shrinks_the_read()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp, messages: 12);
        using var db = temp.Open();

        var rawBriefing = ToolResponses.RenderBriefing(db, thread, "reader", 0, 0, null);

        var last = ThreadStore.ReadThread(db, thread).Messages.Last().Id;
        DigestStore.Save(db, thread, last, "SUMMARY: all resolved, nothing open.", "peer-a");
        var digested = ToolResponses.RenderBriefing(db, thread, "reader", 0, 0, null);

        // The digest is served, labelled as a peer's summary, and points at the
        // raw history — a summary the reader cannot audit is one it must trust.
        Assert.Contains("DIGEST of messages #1–#" + last, digested);
        Assert.Contains("SUMMARY: all resolved, nothing open.", digested);
        Assert.Contains("not the messages", digested);
        Assert.Contains($"read_thread {thread}", digested);
        Assert.Contains("estimated tokens here instead of", digested);

        // And it is smaller. This is the whole point of the feature.
        Assert.True(digested.Length < rawBriefing.Length,
            $"digest briefing ({digested.Length} chars) should be smaller than the raw one ({rawBriefing.Length})");

        // The messages the digest covers are gone from the window...
        Assert.DoesNotContain("message 5", digested);
        // ...but nothing is lost: digest=false returns them.
        var forced = ToolResponses.RenderBriefing(db, thread, "reader", 0, 0, null, useDigest: false);
        Assert.Contains("message 5", forced);
    }

    [Fact]
    public void Briefing_without_a_digest_is_unchanged()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp, messages: 3);
        using var db = temp.Open();

        var text = ToolResponses.RenderBriefing(db, thread, "", 0, 5, null);
        Assert.Contains("#" + ThreadStore.ReadThread(db, thread).Messages.First().Id, text);
        Assert.DoesNotContain("DIGEST", text);
        Assert.Contains("RECENT MESSAGES", text);
    }

    // ---------- agent notes ----------

    [Fact]
    public void Notes_round_trip_per_slot_and_the_newest_wins()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        AgentNoteStore.Save(db, "zcode-glm", "doing A; next B", "work");
        AgentNoteStore.Save(db, "zcode-glm", "doing C; next D", "work");
        AgentNoteStore.Save(db, "zcode-glm", "other workstream", "side");
        AgentNoteStore.Save(db, "other-agent", "not mine", "work");

        var current = AgentNoteStore.Current(db, "zcode-glm", "work");
        Assert.NotNull(current);
        Assert.Equal("doing C; next D", current!.Body);

        var all = AgentNoteStore.CurrentForAgent(db, "zcode-glm");
        Assert.Equal(2, all.Count);                       // one per slot
        Assert.DoesNotContain(all, n => n.Body == "not mine");  // scoped to the agent
        Assert.Contains(all, n => n.Slot == "side" && n.Body == "other workstream");

        // History is kept, not overwritten.
        Assert.Equal(2, AgentNoteStore.History(db, "zcode-glm", "work").Count);
    }

    [Fact]
    public void Note_validation_and_resume_rendering()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        Assert.Throws<AgoraException>(() => AgentNoteStore.Save(db, "  ", "body"));
        Assert.Throws<AgoraException>(() => AgentNoteStore.Save(db, "agent", "   "));

        var empty = ToolResponses.RenderResume(AgentNoteStore.CurrentForAgent(db, "nobody"), "nobody");
        Assert.Contains("no saved notes", empty);
        Assert.Contains("agora note", empty);   // tells the reader how to fix it

        AgentNoteStore.Save(db, "nobody", "was doing X\nnext: Y");
        var text = ToolResponses.RenderResume(AgentNoteStore.CurrentForAgent(db, "nobody"), "nobody");
        Assert.Contains("RESUME", text);
        Assert.Contains("was doing X", text);
        Assert.Contains("not a verified record", text);   // provenance cue
        Assert.Contains("estimated tokens", text);
    }

    // ---------- answer memoization ----------

    [Fact]
    public void Memo_serves_an_exact_match_and_refuses_a_near_one()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        AnswerCacheStore.File(db, "What is the DML mini-batch size?",
            "clamp(2 x EstimatedComputeUnits, min_batch, max_batch).", "zcode-glm");

        // Exact, and case/whitespace-insensitively the same: hit.
        Assert.NotNull(AnswerCacheStore.Lookup(db, "What is the DML mini-batch size?"));
        var loose = AnswerCacheStore.Lookup(db, "  what   IS the dml   mini-batch   size?  ");
        Assert.NotNull(loose);
        Assert.Contains("EstimatedComputeUnits", loose!.Answer);

        // A DIFFERENT question must miss — serving a near answer as fact is
        // worse than a miss, so matching never guesses.
        Assert.Null(AnswerCacheStore.Lookup(db, "What is the SYCL mini-batch size?"));
        Assert.Null(AnswerCacheStore.Lookup(db, "DML mini-batch size"));
        Assert.Null(AnswerCacheStore.Lookup(db, "What is the DML mini-batch size"));   // '?' is not dropped
    }

    [Fact]
    public void Memo_hits_are_counted_and_the_newest_filing_wins()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        AnswerCacheStore.File(db, "q", "first answer", "peer-a");
        var hit = AnswerCacheStore.Lookup(db, "q");
        Assert.Equal(1, hit!.Hits);

        AnswerCacheStore.File(db, "q", "corrected answer", "peer-b");
        var corrected = AnswerCacheStore.Lookup(db, "q");
        Assert.Equal("corrected answer", corrected!.Answer);
        Assert.Equal("peer-b", corrected.FiledBy);
        Assert.Equal(1, corrected.Hits);          // the correction starts its own count

        // The superseded memo survives in history — corrections don't erase.
        var all = AnswerCacheStore.List(db);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Answer == "first answer" && m.Hits == 1);
    }

    [Fact]
    public void Memo_age_limit_makes_an_old_answer_a_miss()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        AnswerCacheStore.File(db, "q", "old answer", "peer-a");

        // Backdate it past the default window by hand — the store has no
        // "created_at" parameter on purpose (the record keeps real time).
        db.Prepare("UPDATE answer_cache SET created_at = $t", "$t")
          .Set("$t", DateTime.UtcNow.AddDays(-45).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
          .ExecuteNonQuery();

        Assert.Null(AnswerCacheStore.Lookup(db, "q"));                       // default 30d
        Assert.NotNull(AnswerCacheStore.Lookup(db, "q", maxAgeDays: 0));     // 0 = no limit
        Assert.NotNull(AnswerCacheStore.Lookup(db, "q", maxAgeDays: 60));
    }

    [Fact]
    public void Memo_validation_and_miss_rendering()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        Assert.Throws<AgoraException>(() => AnswerCacheStore.File(db, "", "a", "peer"));
        Assert.Throws<AgoraException>(() => AnswerCacheStore.File(db, "q", "  ", "peer"));
        Assert.Throws<AgoraException>(() => AnswerCacheStore.File(db, "q", "a", " "));
        Assert.Throws<AgoraException>(() => AnswerCacheStore.File(db, "q", "a", "peer", sourceThreadId: 999));

        var thread = SeedThread(temp, messages: 1);
        var filed = AnswerCacheStore.File(db, "q2", "answer with provenance", "peer-a",
            sourceThreadId: thread, sourceMsgId: ThreadStore.ReadThread(db, thread).Messages.First().Id);
        var served = ToolResponses.RenderMemo(filed, "q2");
        Assert.Contains("MEMO HIT", served);
        Assert.Contains("answer with provenance", served);
        Assert.Contains("peer-a", served);
        Assert.Contains($"thread #{thread}", served);
        Assert.Contains("go stale", served);            // the reader is told to distrust it
        Assert.Contains("a recorded claim, verify before relying on it", served);
    }

    // ---------- token ledger ----------

    [Fact]
    public void Token_ledger_separates_spend_from_saved()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        TokenStore.Record(db, "peer-a", TokenStore.KindSpend, 1000, "read");
        TokenStore.Record(db, "peer-a", TokenStore.KindSpend, 500, "search");
        TokenStore.Record(db, "peer-a", TokenStore.KindSaved, 4000, "digest");
        TokenStore.Record(db, "peer-b", TokenStore.KindSpend, 200, "manual");

        var report = TokenStore.Report(db);
        var a = report.Single(r => r.Agent == "peer-a");
        Assert.Equal(1500, a.Spent);
        Assert.Equal(4000, a.Saved);      // never summed together, never signed
        Assert.Equal(2, a.SpendEvents);
        Assert.Equal(1, a.SaveEvents);

        // Biggest spender first, and the renderer shows both directions.
        Assert.Equal("peer-a", report[0].Agent);
        var text = ToolResponses.RenderTokenReport(report, [], 7);
        Assert.Contains("SELF-REPORTED", text);
        Assert.Contains("1,500", text);
        Assert.Contains("4,000", text);
    }

    [Fact]
    public void Token_entries_reject_a_negative_count_and_an_unknown_kind()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        // A negative number would double-sign the direction kind already carries.
        var negative = Assert.Throws<AgoraException>(() =>
            TokenStore.Record(db, "peer-a", TokenStore.KindSpend, -5));
        Assert.Contains("DIRECTION comes from kind", negative.Message);

        Assert.Throws<AgoraException>(() => TokenStore.Record(db, "peer-a", "burned", 5));
        Assert.Throws<AgoraException>(() => TokenStore.Record(db, "  ", TokenStore.KindSpend, 5));
        Assert.Throws<AgoraException>(() => TokenStore.Record(db, "peer-a", TokenStore.KindSpend, 5, threadId: 999));
    }

    [Fact]
    public void Saving_a_digest_records_its_saving_automatically()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp, messages: 8);
        using var db = temp.Open();

        Assert.Empty(TokenStore.Report(db));   // nothing recorded before

        var last = ThreadStore.ReadThread(db, thread).Messages.Last().Id;
        var result = DigestStore.Save(db, thread, last, "short summary", "peer-a");

        // The store measures the saving itself, so every entry point (MCP tool
        // and CLI) feeds the ledger identically — a path that saved tokens
        // without recording them would make the ledger silently wrong.
        Assert.True(result.SavedTokens > 0,
            $"a digest of {result.RawTokens} raw tokens should save something (digest was {result.DigestTokens})");
        Assert.Equal(result.RawTokens, result.DigestTokens + result.SavedTokens);

        var usage = TokenStore.Report(db).Single();
        Assert.Equal("peer-a", usage.Agent);
        Assert.Equal(result.SavedTokens, usage.Saved);
        Assert.Equal(0, usage.Spent);                       // a saving is not a spend
        Assert.Contains(TokenStore.ByThread(db), t => t.ThreadId == thread && t.Saved == result.SavedTokens);
    }

    [Fact]
    public void A_digest_longer_than_what_it_covers_saves_nothing_and_records_nothing()
    {
        using var temp = new TempDb();
        var thread = SeedThread(temp, messages: 1);        // one short question
        using var db = temp.Open();

        var only = ThreadStore.ReadThread(db, thread).Messages.Single().Id;
        var result = DigestStore.Save(db, thread, only, new string('x', 5000), "peer-a");

        Assert.Equal(0, result.SavedTokens);               // an essay is not a saving
        Assert.Empty(TokenStore.Report(db));               // and the ledger stays honest about it
    }

    [Fact]
    public void Token_estimate_is_a_documented_rough_figure()
    {
        Assert.Equal(0, TokenEstimate.ApproxTokens(""));
        Assert.Equal(25, TokenEstimate.ApproxTokens(new string('x', 100)));   // 100 chars / 4
        Assert.True(TokenEstimate.ApproxTokens(new string('x', 401)) >= 101); // rounds up, never under-states
    }
}
