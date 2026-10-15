using AgoraMCP.Database;
using AgoraMCP.McpServer;
using Xunit;

namespace AgoraMCP.Tests;

public sealed class CliTests
{
    private static (int Code, string Stdout, string Stderr) Run(TempDb temp, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Cli.Run([.. args, "--db", temp.DbPath], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void List_read_search_stats_smoke()
    {
        using var temp = new TempDb();
        using (var db = temp.Open())
        {
            var id = ThreadStore.PostQuestion(db, "cli visible topic", "some context", "claude", specialty: "kernel");
            ThreadStore.SubmitReview(db, id, "cli visible answer", "glm");
        }

        var list = Run(temp, "list");
        Assert.Equal(0, list.Code);
        Assert.Contains("cli visible topic", list.Stdout);

        var filtered = Run(temp, "list", "--status", "answered", "--specialty", "kernel");
        Assert.Contains("cli visible topic", filtered.Stdout);

        var empty = Run(temp, "list", "--status", "open");
        Assert.Contains("none", empty.Stdout);

        var read = Run(temp, "read", "1");
        Assert.Equal(0, read.Code);
        Assert.Contains("cli visible answer", read.Stdout);

        var readMissing = Run(temp, "read", "999");
        Assert.Equal(1, readMissing.Code); // CLI errors go to stderr with a real exit code
        Assert.Contains("does not exist", readMissing.Stderr);

        var search = Run(temp, "search", "visible");
        Assert.Equal(0, search.Code);
        Assert.Contains("cli visible topic", search.Stdout);

        var stats = Run(temp, "stats");
        Assert.Equal(0, stats.Code);
        Assert.Contains("threads:", stats.Stdout);
        Assert.Contains("memory cache:", stats.Stdout);

        var online = Run(temp, "online");
        Assert.Contains("online now", online.Stdout);
    }

    [Fact]
    public void Unknown_command_and_missing_args_fail_with_usage()
    {
        using var temp = new TempDb();
        Assert.Equal(2, Cli.Run(["frobnicate", "--db", temp.DbPath], new StringWriter(), new StringWriter()));
    }
}
