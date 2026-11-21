using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using AgoraMCP.Chats;
using AgoraMCP.Database;
using AgoraMCP.McpServer;

namespace AgoraMCP.Tests;

/// <summary>
/// ChatReaders tests against synthetic home directories whose files mimic the
/// real transcript shapes of each client. RealChatsFact additionally runs the
/// readers against the actual transcripts when AGORA_REAL_CHATS=1.
/// </summary>
public sealed class ChatReadersTests(ITestOutputHelper output)
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

    private static string CreateSqlite(string root, string relative, Action<SqliteConnection> populate)
    {
        var path = System.IO.Path.Combine(root, relative.Replace(
            '/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        populate(conn);
        return path;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void TouchUtc(string path, string stamp) =>
        File.SetLastWriteTimeUtc(path, DateTime.Parse(stamp).ToUniversalTime());

    /// <summary>One synthetic session per original client, with distinct file times.</summary>
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

    private static void AddExtendedClients(string root)
    {
        // Antigravity (2.0 + IDE)
        var agy = WriteFile(root,
            ".gemini/antigravity/brain/agy-sess-1/.system_generated/logs/transcript.jsonl",
            """{"type":"SYSTEM_MESSAGE","source":"SYSTEM","created_at":"2026-09-30T11:00:00Z","content":"system"}""",
            """{"type":"USER_INPUT","source":"USER_EXPLICIT","created_at":"2026-09-30T11:00:01Z","content":"<USER_REQUEST>\ncheck agora\n</USER_REQUEST>\n<ADDITIONAL_METADATA>now</ADDITIONAL_METADATA>"}""",
            """{"type":"USER_INPUT","source":"SYSTEM","created_at":"2026-09-30T11:00:02Z","content":"<CONTEXT_SUMMARY>\ncompacted\n</CONTEXT_SUMMARY>"}""",
            """{"type":"PLANNER_RESPONSE","source":"MODEL","created_at":"2026-09-30T11:00:03Z","model":"gemini-4-neon-xhigh","thinking":"hmm","tool_calls":[{"name":"read_file"}]}""",
            """{"type":"PLANNER_RESPONSE","source":"MODEL","created_at":"2026-09-30T11:00:04Z","model":"gemini-4-neon-xhigh","content":"all clear on agora"}""");
        TouchUtc(agy, "2026-09-30T11:00:04Z");

        var agyIde = WriteFile(root,
            ".gemini/antigravity-ide/brain/agy-ide-1/.system_generated/logs/transcript.jsonl",
            """{"type":"USER_INPUT","source":"USER_EXPLICIT","created_at":"2026-09-30T10:50:00Z","content":"plain ide prompt"}""",
            """{"type":"PLANNER_RESPONSE","source":"MODEL","created_at":"2026-09-30T10:50:05Z","model":"gemini-4-hydrogen","content":"ide reply"}""");
        TouchUtc(agyIde, "2026-09-30T10:50:05Z");

        // OpenCode (v2 + v1 fallback in one SQLite DB)
        CreateSqlite(root, ".local/share/opencode/opencode.db", conn =>
        {
            Exec(conn, """
                CREATE TABLE session_v2 (id TEXT PRIMARY KEY, model TEXT, time_created INTEGER, time_updated INTEGER);
                CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER, time_created INTEGER, data TEXT);
                CREATE TABLE session (id TEXT PRIMARY KEY, model TEXT, time_created INTEGER, time_updated INTEGER);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);
                CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT, time_created INTEGER, data TEXT);
                """);
            var tsV2 = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Exec(conn, "INSERT INTO session_v2 VALUES ($id, $m, $tc, $tu)",
                ("$id", "ses_oc_v2"),
                ("$m", """{"id":"pulse-alpha-free","providerID":"opencode"}"""),
                ("$tc", tsV2 - 5000),
                ("$tu", tsV2));
            Exec(conn, "INSERT INTO session_message VALUES ('m1','ses_oc_v2','user',1,$t,$d)",
                ("$t", tsV2 - 4000),
                ("$d", """{"text":"opencode v2 prompt"}"""));
            Exec(conn, "INSERT INTO session_message VALUES ('m2','ses_oc_v2','assistant',2,$t,$d)",
                ("$t", tsV2 - 3000),
                ("$d", """{"content":[{"type":"reasoning","text":"secret"},{"type":"tool","name":"bash"}]}"""));
            Exec(conn, "INSERT INTO session_message VALUES ('m3','ses_oc_v2','assistant',3,$t,$d)",
                ("$t", tsV2),
                ("$d", """{"content":[{"type":"reasoning","text":"still secret"},{"type":"text","text":"opencode v2 reply"}]}"""));

            var tsV1 = new DateTimeOffset(2026, 9, 30, 11, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Exec(conn, "INSERT INTO session VALUES ($id, $m, $tc, $tu)",
                ("$id", "ses_oc_v1"),
                ("$m", "deepseek-v4"),
                ("$tc", tsV1 - 5000),
                ("$tu", tsV1));
            Exec(conn, "INSERT INTO message VALUES ('vm1','ses_oc_v1',$t,$d)",
                ("$t", tsV1 - 2000),
                ("$d", """{"role":"user"}"""));
            Exec(conn, "INSERT INTO part VALUES ('vp1','vm1','ses_oc_v1',$t,$d)",
                ("$t", tsV1 - 2000),
                ("$d", """{"type":"text","text":"opencode v1 prompt"}"""));
            Exec(conn, "INSERT INTO message VALUES ('vm2','ses_oc_v1',$t,$d)",
                ("$t", tsV1),
                ("$d", """{"role":"assistant"}"""));
            Exec(conn, "INSERT INTO part VALUES ('vp2','vm2','ses_oc_v1',$t,$d)",
                ("$t", tsV1),
                ("$d", """{"type":"tool-invocation","name":"ls"}"""));
            Exec(conn, "INSERT INTO part VALUES ('vp3','vm2','ses_oc_v1',$t,$d)",
                ("$t", tsV1 + 1),
                ("$d", """{"type":"text","text":"opencode v1 reply"}"""));
        });

        // GitHub Copilot CLI (events.jsonl + session-store.db fallback)
        var copilotEvents = WriteFile(root,
            ".copilot/session-state/cop-ev-1/events.jsonl",
            """{"type":"session.start","timestamp":"2026-09-30T13:00:00Z","data":{"sessionId":"cop-ev-1","selectedModel":"gpt-5.6-sol"}}""",
            """{"type":"user.message","timestamp":"2026-09-30T13:00:01Z","data":{"content":"copilot cli prompt"}}""",
            """{"type":"assistant.message","timestamp":"2026-09-30T13:00:02Z","data":{"content":"","toolRequests":[{"name":"read"}]}}""",
            """{"type":"assistant.message","timestamp":"2026-09-30T13:00:03Z","data":{"content":"copilot cli answer"}}""");
        TouchUtc(copilotEvents, "2026-09-30T13:00:03Z");

        CreateSqlite(root, ".copilot/session-store.db", conn =>
        {
            Exec(conn, """
                CREATE TABLE sessions (id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT);
                CREATE TABLE turns (id INTEGER PRIMARY KEY, session_id TEXT, turn_index INTEGER, user_message TEXT, assistant_response TEXT, timestamp TEXT);
                CREATE TABLE assistant_usage_events (id INTEGER PRIMARY KEY, session_id TEXT, model TEXT);
                """);
            Exec(conn, "INSERT INTO sessions VALUES ('cop-db-1','2026-09-30T12:30:00Z','2026-09-30T12:31:00Z')");
            Exec(conn, "INSERT INTO assistant_usage_events VALUES (1,'cop-db-1','kimi-k2.7-code')");
            Exec(conn, "INSERT INTO turns VALUES (1,'cop-db-1',0,'copilot db prompt','copilot db answer','2026-09-30T12:31:00Z')");
        });

        // VS Code Chat (delta-log .jsonl with splice index + empty 0-request shell + legacy .json)
        var vsEmpty = WriteFile(root,
            "AppData/Roaming/Code/User/workspaceStorage/ws1/chatSessions/vs-empty.jsonl",
            """{"kind":0,"v":{"sessionId":"vs-empty","requests":[]}}""");
        TouchUtc(vsEmpty, "2026-09-30T15:00:00Z");

        var vsActive = WriteFile(root,
            "AppData/Roaming/Code/User/workspaceStorage/ws1/chatSessions/vs-sess-1.jsonl",
            """{"kind":0,"v":{"sessionId":"vs-sess-1","inputState":{"selectedModel":{"identifier":"copilot/kimi-k3"}},"requests":[]}}""",
            """{"kind":2,"k":["requests"],"v":[{"requestId":"r1","timestamp":1790776800000,"modelId":"copilot/kimi-k3","message":{"text":"vscode prompt"},"response":[]}]}""",
            """{"kind":2,"k":["requests",0,"response"],"v":[{"kind":"thinking","value":"hidden"},{"value":"draft "}]}""",
            // Splice at index 1 replaces "draft " with the final streaming items without duplicating it.
            """{"kind":2,"k":["requests",0,"response"],"i":1,"v":[{"value":"vscode "},{"kind":"toolInvocationSerialized","invocationMessage":{"value":"tool"}},{"value":"final reply"}]}""");
        TouchUtc(vsActive, "2026-09-30T14:00:00Z");

        var vsLegacy = WriteFile(root,
            "AppData/Roaming/Code/User/globalStorage/emptyWindowChatSessions/vs-legacy.json",
            """{"sessionId":"vs-legacy","requests":[{"timestamp":1790773200000,"modelId":"copilot/gpt-5","message":{"text":"legacy prompt"},"response":[{"value":"legacy answer"}]}]}""");
        TouchUtc(vsLegacy, "2026-09-30T13:30:00Z");

        // Cursor (conversation-search.db reverse-chronological FTS + state.vscdb local composer)
        CreateSqlite(root, "AppData/Roaming/Cursor/User/globalStorage/conversation-search.db", conn =>
        {
            Exec(conn, """
                CREATE TABLE conversations (fts_rowid INTEGER PRIMARY KEY, source TEXT, scope TEXT, id TEXT, title TEXT, updated_at INTEGER);
                CREATE VIRTUAL TABLE conversation_fts USING fts5(title, body, branches);
                """);
            var tsCur = new DateTimeOffset(2026, 9, 30, 14, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            // Empty local draft should be skipped in ListSessions.
            Exec(conn, "INSERT INTO conversations VALUES (1,'local','','cur-empty','', $t)", ("$t", tsCur + 60000));
            Exec(conn, "INSERT INTO conversation_fts (rowid, title, body, branches) VALUES (1,'','','')");
            // Real cloud-cache session (stored newest-first in conversation_fts.body).
            Exec(conn, "INSERT INTO conversations VALUES (2,'cloud-cache','s','bc-cur-1','Coding',$t)", ("$t", tsCur));
            Exec(conn, "INSERT INTO conversation_fts (rowid, title, body, branches) VALUES (2,'Coding',$b,'')",
                ("$b", "assistant:\nsecond reply\n\nuser:\nsecond prompt\n\nassistant:\nfirst reply\n\nuser:\nfirst prompt"));
        });

        CreateSqlite(root, "AppData/Roaming/Cursor/User/globalStorage/state.vscdb", conn =>
        {
            Exec(conn, "CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)");
            var tsLocal = new DateTimeOffset(2026, 9, 30, 14, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Exec(conn, "INSERT INTO cursorDiskKV VALUES ('composerData:cur-empty-draft', $v)",
                ("$v", """{"composerId":"cur-empty-draft","fullConversationHeadersOnly":[]}"""));
            Exec(conn, "INSERT INTO cursorDiskKV VALUES ('composerData:cur-local-1', $v)",
                ("$v", $$"""{"composerId":"cur-local-1","lastUpdatedAt":{{tsLocal}},"modelConfig":{"modelName":"claude-sonnet-4"},"fullConversationHeadersOnly":[{"bubbleId":"b1","type":1},{"bubbleId":"b2","type":2}]}"""));
            Exec(conn, "INSERT INTO cursorDiskKV VALUES ('bubbleId:cur-local-1:b1', $v)",
                ("$v", $$"""{"type":1,"text":"cursor local prompt","createdAt":{{tsLocal - 2000}}}"""));
            Exec(conn, "INSERT INTO cursorDiskKV VALUES ('bubbleId:cur-local-1:b2', $v)",
                ("$v", $$"""{"type":2,"text":"cursor local answer","createdAt":{{tsLocal}}}"""));
        });
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
    public void ListSessions_finds_all_nine_clients_newest_first()
    {
        using var temp = FourClients();
        AddExtendedClients(temp.Root);

        var sessions = ChatReaders.ListSessions(temp.Root, perClient: 1);

        Assert.Equal(9, sessions.Count);
        Assert.Equal(
            new[]
            {
                "cursor",
                "vscode",
                "github-copilot",
                "opencode",
                "antigravity",
                "claudecode",
                "zcode",
                "kimicode",
                "codex",
            },
            sessions.Select(s => s.Client).ToArray());
        Assert.DoesNotContain(sessions, s => s.SessionId is "vs-empty" or "cur-empty" or "cur-empty-draft");
        Assert.Equal("gemini-4-neon-xhigh", sessions.Single(s => s.Client == "antigravity").Model);
        Assert.Equal("pulse-alpha-free", sessions.Single(s => s.Client == "opencode").Model);
        Assert.Equal("gpt-5.6-sol", sessions.Single(s => s.Client == "github-copilot").Model);
        Assert.Equal("copilot/kimi-k3", sessions.Single(s => s.Client == "vscode").Model);
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
    public void AntigravityRead_reads_both_cli_and_ide_and_unwraps_user_request()
    {
        using var temp = new TempDir();
        AddExtendedClients(temp.Root);

        var turns = ChatReaders.Read("antigravity", "agy-sess-1", temp.Root);
        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("check agora", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("all clear on agora", turns[1].Text);
        Assert.DoesNotContain(turns, t => t.Text.Contains("compacted"));

        var ideTurns = ChatReaders.Read("antigravity", "agy-ide-1", temp.Root);
        Assert.Equal(2, ideTurns.Count);
        Assert.Equal("plain ide prompt", ideTurns[0].Text);
        Assert.Equal("ide reply", ideTurns[1].Text);
    }

    [Fact]
    public void OpenCodeRead_reads_v2_and_v1_schemas()
    {
        using var temp = new TempDir();
        AddExtendedClients(temp.Root);

        var v2 = ChatReaders.Read("opencode", "ses_oc_v2", temp.Root);
        Assert.Equal(2, v2.Count);
        Assert.Equal("user", v2[0].Role);
        Assert.Equal("opencode v2 prompt", v2[0].Text);
        Assert.Equal("assistant", v2[1].Role);
        Assert.Equal("opencode v2 reply", v2[1].Text);
        Assert.DoesNotContain(v2, t => t.Text.Contains("secret"));

        var v1 = ChatReaders.Read("opencode", "ses_oc_v1", temp.Root);
        Assert.Equal(2, v1.Count);
        Assert.Equal("opencode v1 prompt", v1[0].Text);
        Assert.Equal("opencode v1 reply", v1[1].Text);
    }

    [Fact]
    public void CopilotRead_reads_events_jsonl_and_session_store_db_fallback()
    {
        using var temp = new TempDir();
        AddExtendedClients(temp.Root);

        var evTurns = ChatReaders.Read("github-copilot", "cop-ev-1", temp.Root);
        Assert.Equal(2, evTurns.Count);
        Assert.Equal("user", evTurns[0].Role);
        Assert.Equal("copilot cli prompt", evTurns[0].Text);
        Assert.Equal("assistant", evTurns[1].Role);
        Assert.Equal("copilot cli answer", evTurns[1].Text);

        var dbTurns = ChatReaders.Read("copilot", "cop-db-1", temp.Root);
        Assert.Equal(2, dbTurns.Count);
        Assert.Equal("copilot db prompt", dbTurns[0].Text);
        Assert.Equal("copilot db answer", dbTurns[1].Text);
    }

    [Fact]
    public void VsCodeRead_replays_delta_splices_skips_empty_shells_and_reads_legacy_json()
    {
        using var temp = new TempDir();
        AddExtendedClients(temp.Root);

        var sessions = ChatReaders.ListSessions(temp.Root)
            .Where(s => s.Client == "vscode")
            .ToList();
        Assert.Equal(["vs-sess-1", "vs-legacy"], sessions.Select(s => s.SessionId).ToArray());

        var turns = ChatReaders.Read("vscode", "vs-sess-1", temp.Root);
        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("vscode prompt", turns[0].Text);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("vscode final reply", turns[1].Text);
        Assert.DoesNotContain(turns, t => t.Text.Contains("draft") || t.Text.Contains("hidden"));

        var legacyTurns = ChatReaders.Read("vscode", "vs-legacy", temp.Root);
        Assert.Equal(2, legacyTurns.Count);
        Assert.Equal("legacy prompt", legacyTurns[0].Text);
        Assert.Equal("legacy answer", legacyTurns[1].Text);
    }

    [Fact]
    public void CursorRead_reads_search_db_chronologically_and_local_state_vscdb()
    {
        using var temp = new TempDir();
        AddExtendedClients(temp.Root);

        var sessions = ChatReaders.ListSessions(temp.Root)
            .Where(s => s.Client == "cursor")
            .ToList();
        Assert.Equal(["bc-cur-1", "cur-local-1"], sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal("claude-sonnet-4", sessions.Single(s => s.SessionId == "cur-local-1").Model);

        // conversation_fts stores turns newest-first; ChatReaders must flip to chronological order.
        var cloudTurns = ChatReaders.Read("cursor", "bc-cur-1", temp.Root);
        Assert.Equal(4, cloudTurns.Count);
        Assert.Equal(["user", "assistant", "user", "assistant"], cloudTurns.Select(t => t.Role).ToArray());
        Assert.Equal(["first prompt", "first reply", "second prompt", "second reply"],
            cloudTurns.Select(t => t.Text).ToArray());

        var localTurns = ChatReaders.Read("cursor", "cur-local-1", temp.Root);
        Assert.Equal(2, localTurns.Count);
        Assert.Equal("cursor local prompt", localTurns[0].Text);
        Assert.Equal("cursor local answer", localTurns[1].Text);
    }

    [Fact]
    public void ChatClient_enum_exposes_all_nine_wire_names()
    {
        var wire = Enum.GetValues<ChatClient>().Select(c => c.Wire()).ToArray();
        Assert.Equal(
            [
                "claudecode", "zcode", "kimicode", "codex",
                "antigravity", "opencode", "github-copilot", "vscode", "cursor",
            ],
            wire);
    }

    [Fact]
    public void Read_rejects_unknown_client_and_missing_session()
    {
        using var temp = FourClients();
        var home = temp.Root;

        Assert.Throws<AgoraException>(() => ChatReaders.Read("nope", "csess1", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("claudecode", "does-not-exist", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("antigravity", "does-not-exist", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("opencode", "does-not-exist", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("github-copilot", "does-not-exist", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("vscode", "does-not-exist", home));
        Assert.Throws<AgoraException>(
            () => ChatReaders.Read("cursor", "does-not-exist", home));
    }

    [RealChatsFact]
    public void Real_transcripts_read_end_to_end()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sw = Stopwatch.StartNew();
        var sessions = ChatReaders.ListSessions(home, perClient: 5);
        sw.Stop();
        output.WriteLine($"ListSessions(perClient=5): {sessions.Count} sessions in {sw.ElapsedMilliseconds} ms");
        Assert.Contains(sessions, s => s.Client == "zcode");

        // Every installed client yields at least one session with readable
        // turns among its five newest.
        var with_turns = new HashSet<string>();
        foreach (var group in sessions.GroupBy(s => s.Client))
        {
            foreach (var session in group)
            {
                var turns = ChatReaders.Read(session.Client, session.SessionId);
                if (turns.Count > 0)
                {
                    with_turns.Add(group.Key);
                    var last = turns[^1];
                    var preview = last.Text.Replace("\r", "").Replace('\n', ' ');
                    if (preview.Length > 90) preview = preview[..90] + "…";
                    output.WriteLine(
                        $"  {session.Client,-14} {session.SessionId} model={session.Model} last={session.LastActivity} turns={turns.Count} tail=[{last.Role}] {preview}");
                    break;
                }
            }
        }
        Assert.Contains("zcode", with_turns);
        Assert.Contains("claudecode", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".kimi-code", "sessions")))
            Assert.Contains("kimicode", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".codex", "sessions")))
            Assert.Contains("codex", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".gemini", "antigravity", "brain")) ||
            Directory.Exists(System.IO.Path.Combine(home, ".gemini", "antigravity-ide", "brain")))
            Assert.Contains("antigravity", with_turns);
        if (File.Exists(System.IO.Path.Combine(home, ".local", "share", "opencode", "opencode.db")))
            Assert.Contains("opencode", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(home, ".copilot", "session-state")) ||
            File.Exists(System.IO.Path.Combine(home, ".copilot", "session-store.db")))
            Assert.Contains("github-copilot", with_turns);
        if (Directory.Exists(System.IO.Path.Combine(appData, "Code", "User", "workspaceStorage")))
            Assert.Contains("vscode", with_turns);
        if (File.Exists(System.IO.Path.Combine(appData, "Cursor", "User", "globalStorage", "conversation-search.db")))
            Assert.Contains("cursor", with_turns);
    }
}
