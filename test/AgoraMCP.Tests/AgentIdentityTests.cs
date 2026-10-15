using AgoraMCP.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The board addresses the AGENT (the client), not the model — but the
/// history carries model-flavoured names (muse-spark, zcode-glm, claude-opus,
/// codex-sol, gemini-antigravity) and those rows are immutable. Renaming an
/// agent must therefore not silence its mail: routing resolves every name an
/// agent has used, while writes keep the name the agent posted under.
///
/// The failure this guards against is silent: exact-match routing plus a
/// rename means an agent's directed threads and turn tokens simply stop
/// arriving, with no error on either side.
/// </summary>
public sealed class AgentIdentityTests
{
    [Fact]
    public void A_current_name_resolves_to_itself_and_its_former_names()
    {
        Assert.Equal("zcode", AgentIdentity.NamesFor("zcode")[0]);
        Assert.Contains("zcode-glm", AgentIdentity.NamesFor("zcode"));
        Assert.Contains("muse-spark", AgentIdentity.NamesFor("opencode"));
        Assert.Contains("claude-opus", AgentIdentity.NamesFor("claudecode"));
        Assert.Contains("gemini-antigravity", AgentIdentity.NamesFor("antigravity"));
    }

    [Fact]
    public void A_former_name_resolves_to_the_SAME_agent_as_its_current_name()
    {
        // The whole point: old rows and new rows must land on one identity.
        var asOld = AgentIdentity.NamesFor("zcode-glm");
        var asNew = AgentIdentity.NamesFor("zcode");
        Assert.Equal(asNew.OrderBy(x => x), asOld.OrderBy(x => x));

        // And an old name answering to itself means mail sent to the old name
        // is still matched when the agent calls in under the old name.
        Assert.True(AgentIdentity.AnswersTo("zcode", "zcode-glm"));
        Assert.True(AgentIdentity.AnswersTo("zcode-glm", "zcode"));
        Assert.True(AgentIdentity.AnswersTo("codex", "codex-opus"));
        Assert.True(AgentIdentity.AnswersTo("codex", "Codex-opus"));   // case-insensitive
    }

    [Fact]
    public void An_unknown_name_resolves_to_itself_alone()
    {
        Assert.Equal(["solo-agent"], AgentIdentity.NamesFor("solo-agent"));
        Assert.False(AgentIdentity.AnswersTo("zcode", "solo-agent"));
        Assert.Empty(AgentIdentity.NamesFor("  "));
    }

    [Fact]
    public void Directed_mail_sent_to_a_former_name_still_reaches_the_agent()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        // Written BEFORE the rename: addressed to the old name.
        var old = ThreadStore.PostQuestion(db, "old-name mail", "", "asker", directedTo: "zcode-glm");

        // The agent now calls in under its current name.
        var pending = ThreadStore.FetchPending(db, agent: "zcode");
        Assert.Contains(pending, t => t.Id == old);

        // And under the old name too (an agent mid-migration is not locked out).
        Assert.Contains(ThreadStore.FetchPending(db, agent: "zcode-glm"), t => t.Id == old);

        // A non-target does NOT get it via the directed branch; it appears only
        // because an unfiltered call sees the whole open board (documented
        // behavior). With a specialty filter, the directed branch is the only
        // route, and it stays closed to others.
        Assert.DoesNotContain(
            ThreadStore.FetchPending(db, agent: "opencode", specialty: "kernel"), t => t.Id == old);
    }

    [Fact]
    public void A_turn_passed_under_a_former_name_is_still_the_agents_turn()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var thread = ThreadStore.PostQuestion(db, "turn handover", "", "asker");

        // Passed to the OLD name, before the rename.
        TurnStore.Pass(db, thread, "asker", "claude-opus", "please review");

        // The agent now identifies as the current name and must still see it.
        var held = TurnStore.HeldBy(db, "claudecode");
        Assert.Contains(held, h => h.ThreadId == thread);

        // And the old name still resolves to the same turn.
        Assert.Contains(TurnStore.HeldBy(db, "claude-opus"), h => h.ThreadId == thread);

        // Another agent does not hold it.
        Assert.DoesNotContain(TurnStore.HeldBy(db, "zcode"), h => h.ThreadId == thread);
    }

    [Fact]
    public void Delivery_receipts_written_under_a_former_name_carry_over()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var thread = ThreadStore.PostQuestion(db, "receipts", "", "asker", directedTo: "codex-sol");
        var msg = ThreadStore.ReadThread(db, thread).Messages.Single().Id;
        RecipientStore.Acknowledge(db, thread, "codex-sol", msg);

        // The agent now calls in as `codex`: same receipt, not a second unseen one.
        var state = RecipientStore.DeliveryFor(db, "codex").Single(s => s.ThreadId == thread);
        Assert.True(state.Handled);
        Assert.Equal(msg, state.LastAckMsgId);
    }

    [Fact]
    public void Pending_with_no_agent_still_returns_open_threads()
    {
        // The alias layer must not change the no-name path. The trap here is
        // SQL three-valued logic: a `NOT IN (NULL)` for the empty case
        // evaluates to NULL rather than TRUE, which would silently drop every
        // directed thread from an unaddressed call.
        using var temp = new TempDb();
        using var db = temp.Open();
        var open = ThreadStore.PostQuestion(db, "public question", "", "asker");
        var directed = ThreadStore.PostQuestion(db, "someone's mail", "", "asker", directedTo: "zcode");

        var all = ThreadStore.FetchPending(db, agent: "");
        Assert.Equal(new[] { open, directed }, all.Select(t => t.Id));

        // With a specialty filter and no agent, the directed thread is excluded
        // — the "someone else's mail" branch requires an empty specialty, so an
        // unaddressed filtered call sees only the open board. Asserted because
        // the alias rewrite touched this clause and the pre-alias behavior is
        // the contract.
        Assert.DoesNotContain(ThreadStore.FetchPending(db, agent: "", specialty: "kernel"),
            t => t.DirectedTo != "");
    }

    [Fact]
    public void The_pending_query_binds_every_alias_without_parameter_collisions()
    {
        // Regression guard for the string-built IN-list: a parameter named twice
        // (or an unbound {ME}) would throw at the driver, not silently pass.
        using var temp = new TempDb();
        using var db = temp.Open();
        ThreadStore.PostQuestion(db, "codex mail", "", "asker", directedTo: "codex-sol");
        ThreadStore.PostQuestion(db, "second codex mail", "", "asker", directedTo: "codex-opus");

        var pending = ThreadStore.FetchPending(db, agent: "codex");
        Assert.Equal(2, pending.Count);   // both historical names reached
    }
}
