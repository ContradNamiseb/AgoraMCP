using AgoraMCP.Database;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The load-bearing test for the one-process-per-agent design: several
/// connections on ONE file behave like several agents on one blackboard. WAL
/// lets readers run while a writer writes; the busy timeout serializes writers
/// into queue-then-succeed instead of SQLITE_BUSY failures.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public void Reader_sees_writer_across_connections_on_one_file()
    {
        using var temp = new TempDb();
        using var writer = temp.Open();
        using var reader = temp.Open();

        var id = ThreadStore.PostQuestion(writer, "visible across processes", "", "a");
        var pending = ThreadStore.FetchPending(reader);
        Assert.Single(pending);
        Assert.Equal(id, pending[0].Id);

        ThreadStore.SubmitReview(writer, id, "from the writer side", "b");
        Assert.Equal(ThreadStore.StatusAnswered, ThreadStore.ReadThread(reader, id).Header.Status);
    }

    [Fact]
    public async Task Concurrent_writers_both_succeed_via_busy_timeout()
    {
        using var temp = new TempDb();
        const int writers = 6;
        using var barrier = new Barrier(writers);

        var threads = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            using var db = temp.Open();
            barrier.SignalAndWait(TimeSpan.FromSeconds(10)); // maximize contention
            PresenceStore.Heartbeat(db, $"agent-{i}", "specialty");
            ThreadStore.PostQuestion(db, $"question {i}", "", $"agent-{i}");
        })).ToArray();

        await Task.WhenAll(threads);

        using var check = temp.Open();
        Assert.Equal(writers, ThreadStore.ListThreads(check).Count);
    }

    [Fact]
    public void Database_is_in_wal_mode()
    {
        using var temp = new TempDb();
        using var db = temp.Open();
        Assert.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
    }
}
