using Xunit;
using AgoraMCP.Database;

namespace AgoraMCP.Tests;

public sealed class DmStoreTests
{
    [Fact]
    public void Send_creates_thread_and_message()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var (thread, msg) = DmStore.Send(db, "zcode", "kimicode", "ping");

        Assert.True(thread > 0);
        Assert.True(msg > 0);
        var inbox = DmStore.Inbox(db, "kimicode");
        var row = Assert.Single(inbox);
        Assert.Equal("zcode", row.From);
        Assert.Equal("kimicode", row.To);
        Assert.Equal("zcode", row.LastAuthor);
        Assert.Equal("ping", row.LastMessage);
        Assert.Equal(1, row.TotalMessages);
    }

    [Fact]
    public void Pair_reuses_one_conversation_in_both_directions()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var (t1, _) = DmStore.Send(db, "zcode", "kimicode", "first");
        var (t2, _) = DmStore.Send(db, "zcode", "kimicode", "second");
        var (t3, _) = DmStore.Send(db, "kimicode", "zcode", "back");

        Assert.Equal(t1, t2);
        Assert.Equal(t1, t3);
        var inbox = DmStore.Inbox(db, "zcode");
        Assert.Single(inbox);
        Assert.Equal(3, inbox[0].TotalMessages);
        Assert.Equal("kimicode", inbox[0].LastAuthor);
    }

    [Fact]
    public void Inbox_resolves_former_names()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        // zcode's former name is zcode-glm; mail sent under it must surface.
        DmStore.Send(db, "kimicode", "zcode-glm", "old-name mail");

        var inbox = DmStore.Inbox(db, "zcode");
        Assert.Single(inbox);
        Assert.Equal("old-name mail", inbox[0].LastMessage);
    }

    [Fact]
    public void Board_listing_hides_dm_conversations()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        ThreadStore.PostQuestion(db, "board topic", "ctx", "zcode", "", "");
        DmStore.Send(db, "zcode", "kimicode", "secret ping");

        var listed = ThreadStore.ListThreads(db);
        Assert.All(listed, t => Assert.DoesNotContain("DM", t.Topic));
        Assert.Single(listed);
    }

    [Fact]
    public void Dm_thread_is_directed_so_pending_sees_it()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        var (thread, _) = DmStore.Send(db, "zcode", "kimicode", "review ping");

        var detail = ThreadStore.ReadThread(db, thread);
        Assert.Equal("kimicode", detail.Header.DirectedTo);
    }

    [Fact]
    public void Send_rejects_missing_fields()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        Assert.Throws<AgoraException>(() => DmStore.Send(db, "", "kimicode", "x"));
        Assert.Throws<AgoraException>(() => DmStore.Send(db, "zcode", " ", "x"));
        Assert.Throws<AgoraException>(() => DmStore.Send(db, "zcode", "kimicode", ""));
    }
}
