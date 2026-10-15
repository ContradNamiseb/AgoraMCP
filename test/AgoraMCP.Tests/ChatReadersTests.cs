using System.Text.Json;
using Xunit;
using AgoraMCP.Chats;
using AgoraMCP.Database;

namespace AgoraMCP.Tests;

/// <summary>
/// ChatReaders tests against synthetic home directories whose files mimic the
/// real transcript shapes of each client (shapes verified against live logs on
/// this box, 2026-09-30). RealChatsFact additionally runs the readers against
/// the actual transcripts when AGORA_REAL_CHATS=1.
/// </summary>
public sealed class ChatReadersTests
{
    // Runs only when the environment asks for it; reading the real transcript
    // tree makes no sense on a CI box without agents installed.
    private sealed class RealChatsFactAttribute : FactAttribute
    {
        public RealChatsFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AGORA_REAL_CHATS") != "1")
                Skip = "Set AGORA_REAL_CHATS=1 to read real transcripts on this box";
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "agora-chats-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string WriteFile(string root, string relative, params string[] lines)
    {
        var path = System.IO.Path.Combine(root, relative.Replace(
            '/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static void TouchUtc(string path, string stamp) =>
        File.SetLastWriteTimeUtc(path, DateTime.Parse(stamp).ToUniversalTime());

    /// <summary>One synthetic session per client, with distinct file times.</summary>
    private static TempDir FourClients()
    {
        var temp = new TempDir();
        var claude = WriteFile(temp.Root, ".claude/projects/p1/csess1.jsonl",
            """
            {"type":"summary","summary":"not a turn"}
            """,
            """
            {"type":"user","sessionId":"csess1","timestamp":"2026-09-30T10:00:00Z","message":{"role":"user","content":"hello from user"}}
            """,
            """
            {"type":"user","isSidechain":true,"sessionId":"csess1","timestamp":"2026-09-30T10:00:01Z","message":{"role":"user","content":"sidechain secret"}}
            """,
            """
            {"type":"assistant","sessionId":"csess1","timestamp":"2026-09-30T10:00:02Z","message":{"role":"assistant","model":"claude-opus-5-5","content":[{"type":"thinking","thinking":"hm"},{"type":"text","text":"assistant reply"}]}}
            """,
            "not json {");
        var zcode = WriteFile(temp.Root, ".zcode/cli/rollout/model-io-sess_abc123.jsonl",
            """
            {"model":{"modelId":"OLD"},"request":{"body":{"messages":[{"role":"user","content":"stale"}]}}}
            """,
            """
            {"model":{"modelId":"GLM-5.3"},"request":{"body":{"messages":[{"role":"user","content":"zcode question"},{"role":"assistant","content":[{"type":"text","text":"zcode answer"}]}]}}}
            """);
        var kimi = WriteFile(temp.Root,
            ".kimi-code/sessions/wd_test/session_ksess1/agents/agent-0/wire.jsonl",
            """
            {"type":"metadata","protocol_version":"1.5"}
            """,
            """
            {"type":"turn.prompt","input":"kimi do this","time":"2026-09-30T07:00:00Z"}
            """,
            """
            {"type":"agent.message.appended","message":{"message":{"role":"assistant","content":[{"type":"text","text":"kimi done"}]}},"time":"2026-09-30T07:00:05Z"}
            """);
        var codex = WriteFile(temp.Root,
            ".codex/sessions/2026/09/30/rollout-2026-09-30T09-00-00-c1111111-2222-3333-4444-555555555555.jsonl",
            """
            {"type":"event_msg","timestamp":"2026-09-30T09:00:01Z","payload":{"type":"user_message","message":"codex user line"}}
            """,
            """
            {"type":"response_item","timestamp":"2026-09-30T09:00:02Z","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"codex assistant line"}]}}
            """,
            """
            {"type":"response_item","timestamp":"2026-09-30T09:00:03Z","payload":{"type":"function_call","name":"shell"}}
            """);
        TouchUtc(claude, "2026-09-30T10:00:00Z");
        TouchUtc(zcode, "2026-09-30T09:30:00Z");
        TouchUtc(kimi, "2026-09-30T08:00:00Z");
        TouchUtc(codex, "2026-09-30T07:00:00Z");
        return temp;
    }

    [Fact]
    public void ListSessions_finds_all_clients_newest_first()
    {
        using var temp = FourClients();
        var home = temp.Root;

        var sessions = ChatReaders.ListSessions(home);

        Assert.Equal(4, sessions.Count);
        Assert.Equal(new[] { "claudecode", "zcode", "kimicode", "codex" },
            sessions.Select(s => s.Client).ToArray());
        var claude = sessions[0];
        Assert.Equal("csess1", claude.SessionId);
        Assert.Equal("claude-opus-5-5", claude.Model);
        Assert.Equal("2026-09-30T10:00:02Z", claude.LastActivity);
        Assert.Equal(2, claude.Turns);  // sidechain and summary lines skipped
        var zcode = sessions[1];
        Assert.Equal("sess_abc123", zcode.SessionId);
        Assert.Equal("GLM-5.3", zcode.Model);
        var kimi = sessions[2];
        Assert.Equal("session_ksess1", kimi.SessionId);
        var codex = sessions[3];
        Assert.Equal("c1111111-2222-3333-4444-555555555555", codex.SessionId);
    }

    [Fact]
    public void ListSessions_on_empty_home_returns_nothing()
    {
        using var temp = new TempDir();
        Assert.Empty(ChatReaders.ListSessions(temp.Root));
    }

    [Fact]
    public void ClaudeRead_returns_user_and_assistant_text_only()
    {
        using var temp = FourClients();
        var home = temp.Root;

        var turns = ChatReaders.Read("claudecode", "csess1", home);

        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("hello from user", turns[0].Text);
        Assert.Equal("2026-09-30T10:00:00Z", turns[0].Timestamp);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("assistant reply", turns[1].Text);  // thinking block skipped
        Assert.DoesNotContain(turns, t => t.Text.Contains("sidechain"));
    }

    [Fact]
    public void ZcodeRead_reads_the_last_records_conversation()
    {
        using var temp = FourClients();
        var home = temp.Root;

        var turns = ChatReaders.Read("zcode", "sess_abc123", home);

        Assert.Equal(2, turns.Count);
        Assert.Equal("zcode question", turns[0].Text);
        Assert.Equal("zcode answer", turns[1].Text);
        Assert.DoesNotContain(turns, t => t.Text.Contains("stale"));
    }

    [Fact]
    public void KimiRead_returns_prompts_and_appended_messages()
    {
        using var temp = FourClients();
        var home = temp.Root;

        var turns = ChatReaders.Read("kimicode", "session_ksess1", home);

        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("kimi do this", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("kimi done", turns[1].Text);
    }

    [Fact]
    public void CodexRead_reads_input_and_output_text_blocks()
    {
        using var temp = FourClients();
        var home = temp.Root;

        var turns = ChatReaders.Read(
            "codex", "c1111111-2222-3333-4444-555555555555", home);

        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("codex user line", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("codex assistant line", turns[1].Text);
        Assert.DoesNotContain(turns, t => t.Text.Contains("function_call"));
    }

    [Fact]
    public void Read_rejects_unknown_client_and_missing_session()
    {
        using var temp = FourClients();
        var home = temp.Root;

        Assert.Throws<AgoraException>(() => ChatReaders.Read("nope", "csess1", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("claudecode", "does-not-exist", home));
    }

    [RealChatsFact]
    public void Real_transcripts_read_end_to_end()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessions = ChatReaders.ListSessions(home, perClient: 5);
        Assert.Contains(sessions, s => s.Client == "zcode");

        // Every installed client yields at least one session with readable
        // turns among its five newest.
        var with_turns = new HashSet<string>();
        foreach (var group in sessions.GroupBy(s => s.Client))
            foreach (var session in group)
                if (ChatReaders.Read(session.Client, session.SessionId).Count > 0)
                {
                    with_turns.Add(group.Key);
                    break;
                }
        Assert.Contains("zcode", with_turns);
        Assert.Contains("claudecode", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".kimi-code", "sessions")))
            Assert.Contains("kimicode", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".codex", "sessions")))
            Assert.Contains("codex", with_turns);
    }
}
