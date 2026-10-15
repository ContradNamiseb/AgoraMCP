using AgoraMCP.Database;
using Xunit;

namespace AgoraMCP.Tests;

public sealed class PresenceStoreTests
{
    [Fact]
    public void Heartbeat_upserts_identity_specialty_and_capabilities()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        PresenceStore.Heartbeat(db, "zcode-glm", "kernel", "GLM-5.3, Triton/CUDA");
        PresenceStore.Heartbeat(db, "zcode-glm", "research", "GLM-5.3, deep context");

        var roster = PresenceStore.OnlineRoster(db);
        Assert.Single(roster);
        Assert.Equal("research", roster[0].Specialty); // last beat wins
        Assert.Equal("GLM-5.3, deep context", roster[0].Capabilities);
        Assert.Equal("online", roster[0].Status);

        Assert.Throws<AgoraException>(() => PresenceStore.Heartbeat(db, "   "));
    }

    [Fact]
    public void Stale_heartbeats_leave_the_roster_within_ttl()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        PresenceStore.Heartbeat(db, "fresh-peer", "kernel", at: DateTimeOffset.UtcNow);
        PresenceStore.Heartbeat(db, "dead-peer", "kernel", at: DateTimeOffset.UtcNow.AddSeconds(-500));

        var roster = PresenceStore.OnlineRoster(db, ttlSeconds: 90);
        Assert.Single(roster);
        Assert.Equal("fresh-peer", roster[0].Agent);

        // Widening the TTL brings the stale peer back — presence is a read-time judgement.
        Assert.Equal(2, PresenceStore.OnlineRoster(db, ttlSeconds: 1000).Count);
    }

    [Fact]
    public void Offline_status_and_specialty_filter()
    {
        using var temp = new TempDb();
        using var db = temp.Open();

        PresenceStore.Heartbeat(db, "a", "kernel");
        PresenceStore.Heartbeat(db, "b", "research");
        PresenceStore.Heartbeat(db, "b", "research", offline: true);

        Assert.Single(PresenceStore.OnlineRoster(db));
        Assert.Equal("a", PresenceStore.OnlineRoster(db)[0].Agent);
        Assert.Single(PresenceStore.OnlineRoster(db, specialty: "kernel"));
        Assert.Empty(PresenceStore.OnlineRoster(db, specialty: "research"));
    }
}
