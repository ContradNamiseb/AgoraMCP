using Xunit;
using AgoraMCP.Database;

namespace AgoraMCP.Tests;

/// <summary>
/// Phase 3 of thread #54 (Agora Inbox + Cross-Chats), exercised the way a real
/// client sees it: a live `agora serve` over stdio. These tests exist because
/// reflection-based guards see DECLARED tools/resources, while the server only
/// serves what StdioServer actually registers — that exact gap shipped the DM
/// and chat tools unregistered until the live tools/list test below was added.
/// </summary>
public sealed class ResourceToolsTests
{
    [Fact]
    public void Live_server_registers_every_declared_tool()
    {
        using var temp = new TempDb();
        using var client = new McpStdioClient(temp.DbPath);

        var tools = client.ListTools();

        // The total pins the whole surface: 16 collaboration + 2 OS + 8
        // token-saving + 2 DM + 2 chat tools.
        Assert.Equal(30, tools.Count);
        foreach (var name in new[]
                 { "send_dm", "read_inbox", "list_chats", "read_chat", "heartbeat" })
            Assert.Contains(name, tools);
    }

    [Fact]
    public void Live_server_offers_the_resource_templates()
    {
        using var temp = new TempDb();
        using var client = new McpStdioClient(temp.DbPath);

        var templates = client.ListResourceTemplates();
        // The no-variable "agora://chats" is served as a static resource, not
        // a template — the SDK moves it to resources/list on its own.
        Assert.Contains("agora://inbox/{agent}", templates);
        Assert.Contains("agora://chats/{client}/{session}", templates);
        Assert.Contains("agora://chats", client.ListResources());
    }

    [Fact]
    public void Inbox_resource_reads_the_dm_conversation()
    {
        using var temp = new TempDb();
        using (var db = temp.Open())
            DmStore.Send(db, "zcode", "kimicode", "resource ping");
        using var client = new McpStdioClient(temp.DbPath);

        var text = client.ReadResource("agora://inbox/kimicode");

        Assert.Contains("resource ping", text);
        Assert.Contains("zcode", text);
    }

    [Fact]
    public void Dm_arrival_pings_a_subscribed_inbox()
    {
        // The whole phase-3 chain in one test: subscribe → DM lands in the
        // shared DB from OUTSIDE the server process → the server's
        // ResourceWatcher notices within its 1s poll and fires
        // notifications/resources/updated for the subscribed URI.
        using var temp = new TempDb();
        using var client = new McpStdioClient(temp.DbPath, agentEnv: "kimicode");
        client.Subscribe("agora://inbox/kimicode");
        // Let the watcher take its baseline first: a DM that lands before the
        // baseline is indistinguishable from history, and the watcher (rightly)
        // never fires for it. 1.5s covers the 200ms startup yield + first poll.
        Thread.Sleep(1500);
        using (var db = temp.Open())
            DmStore.Send(db, "zcode", "kimicode", "the ping payload");

        var notification = client.WaitForNotification(
            "notifications/resources/updated", seconds: 8);
        Assert.Equal(
            "agora://inbox/kimicode",
            notification.GetProperty("params").GetProperty("uri").GetString());
    }

    [Fact]
    public void Dm_arrival_does_not_ping_an_unsubscribed_client()
    {
        // The gating half of the contract: updated notifications go only where
        // the client asked for them (a non-subscriber is still pinged by the
        // toast/watcher/pending layers, not by this one).
        using var temp = new TempDb();
        using var client = new McpStdioClient(temp.DbPath, agentEnv: "kimicode");

        using (var db = temp.Open())
            DmStore.Send(db, "zcode", "kimicode", "silent ping");

        // 3s = three watcher polls; an ungated watcher would have fired.
        Assert.True(client.NoNotification(
            "notifications/resources/updated", seconds: 3));
    }
}
