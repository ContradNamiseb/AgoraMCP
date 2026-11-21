using System.Text.Json;
using AgoraMCP.Database;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Chats;

/// <summary>One conversation turn extracted from a client transcript.</summary>
public sealed record ChatTurn(string Role, string Text, string? Timestamp);

/// <summary>One session of one client, as list_chats reports it.</summary>
public sealed record ChatSession(
    string Client,
    string SessionId,
    string Model,
    string LastActivity,
    int Turns,
    string Path);

/// <summary>
/// Read-only readers for the chat transcripts each client on this box already
/// writes — the cross-agent visibility layer: with these, what any agent said
/// in its own session is readable by any other agent through agora. Nothing
/// here writes to any client's files or databases.
/// </summary>
public static partial class ChatReaders
{
    private const string KnownClientsList =
        "claudecode, zcode, kimicode, codex, antigravity, opencode, github-copilot, vscode, cursor";

    /// <summary>
    /// Lists the newest sessions of every known client. `home` is injectable
    /// for tests; production calls pass nothing and get the user profile.
    /// </summary>
    public static IReadOnlyList<ChatSession> ListSessions(string? home = null, int perClient = 5)
    {
        var explicitHome = string.IsNullOrWhiteSpace(home) ? null : home;
        var resolvedHome = explicitHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessions = new List<ChatSession>();
        AddSafe(sessions, () => ClaudeSessions(resolvedHome, perClient));
        AddSafe(sessions, () => ZcodeSessions(resolvedHome, perClient));
        AddSafe(sessions, () => KimiSessions(resolvedHome, perClient));
        AddSafe(sessions, () => CodexSessions(resolvedHome, perClient));
        AddSafe(sessions, () => AntigravitySessions(resolvedHome, perClient));
        AddSafe(sessions, () => OpenCodeSessions(explicitHome, resolvedHome, perClient));
        AddSafe(sessions, () => CopilotSessions(resolvedHome, perClient));
        AddSafe(sessions, () => VsCodeSessions(explicitHome, resolvedHome, perClient));
        AddSafe(sessions, () => CursorSessions(explicitHome, resolvedHome, perClient));
        sessions.Sort((a, b) => string.CompareOrdinal(b.LastActivity, a.LastActivity));
        return sessions;
    }

    /// <summary>
    /// Reads one session's turns (user and assistant text only; tool traffic
    /// is skipped). `client` is a client name like "zcode" or "claudecode".
    /// </summary>
    public static IReadOnlyList<ChatTurn> Read(
        string client, string sessionId, string? home = null, int limit = 200)
    {
        var explicitHome = string.IsNullOrWhiteSpace(home) ? null : home;
        var resolvedHome = explicitHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return client.Trim().ToLowerInvariant() switch
        {
            "zcode" => ZcodeRead(resolvedHome, sessionId),
            "claudecode" or "claude" => ClaudeRead(resolvedHome, sessionId),
            "kimicode" or "kimi" => KimiRead(resolvedHome, sessionId),
            "codex" => CodexRead(resolvedHome, sessionId),
            "antigravity" or "agy" or "antigravity-ide" => AntigravityRead(resolvedHome, sessionId),
            "opencode" => OpenCodeRead(explicitHome, resolvedHome, sessionId),
            "github-copilot" or "copilot" or "gh-copilot" => CopilotRead(resolvedHome, sessionId),
            "vscode" or "vs-code" or "code" => VsCodeRead(explicitHome, resolvedHome, sessionId),
            "cursor" => CursorRead(explicitHome, resolvedHome, sessionId),
            _ => throw new AgoraException(
                $"unknown client '{client}' — known: {KnownClientsList}"),
        };
    }

    // --- shared JSON, file, path and SQLite helpers -----------------------------

    private static void AddSafe(List<ChatSession> target, Func<IEnumerable<ChatSession>> collect)
    {
        try
        {
            target.AddRange(collect());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // A client mid-write or a locked DB must not take down listing for the others.
        }
    }

    private static JsonDocument? Parse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>
    /// Opens a file with ReadWrite|Delete sharing so active writers on Windows
    /// (VS Code, Antigravity, Copilot, etc.) do not cause sharing violations.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SqliteConnection OpenReadOnlySqlite(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static bool SqliteTableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool SqliteColumnExists(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? FormatEpoch(long value)
    {
        if (value <= 0) return null;
        try
        {
            var dto = value < 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeSeconds(value)
                : DateTimeOffset.FromUnixTimeMilliseconds(value);
            return dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsSafeIdSegment(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.IndexOfAny(['/', '\\']) < 0 &&
        !sessionId.Contains("..", StringComparison.Ordinal);

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSyntheticHome(string? explicitHome, string resolvedHome)
    {
        if (explicitHome is null) return false;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return true;
        var normExplicit = Path.GetFullPath(resolvedHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normProfile = Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !string.Equals(normExplicit, normProfile, comparison);
    }

    /// <summary>
    /// Resolves Electron/VS Code-style `<appName>/User` directories for both live
    /// user profiles (Windows %APPDATA%, macOS Application Support, Linux XDG) and
    /// synthetic test homes.
    /// </summary>
    private static IEnumerable<string> AppDataUserDirs(
        string? explicitHome, string resolvedHome, params string[] appNames)
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var synthetic = IsSyntheticHome(explicitHome, resolvedHome);

        foreach (var appName in appNames)
        {
            IEnumerable<string> candidates;
            if (!synthetic)
            {
                if (OperatingSystem.IsWindows())
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    candidates = string.IsNullOrEmpty(appData)
                        ? [Path.Combine(resolvedHome, "AppData", "Roaming", appName, "User")]
                        : [Path.Combine(appData, appName, "User")];
                }
                else if (OperatingSystem.IsMacOS())
                {
                    candidates = [Path.Combine(resolvedHome, "Library", "Application Support", appName, "User")];
                }
                else
                {
                    var xdg = NonEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
                        ?? Path.Combine(resolvedHome, ".config");
                    candidates = [Path.Combine(xdg, appName, "User")];
                }
            }
            else
            {
                candidates =
                [
                    Path.Combine(resolvedHome, "AppData", "Roaming", appName, "User"),
                    Path.Combine(resolvedHome, ".config", appName, "User"),
                    Path.Combine(resolvedHome, "Library", "Application Support", appName, "User"),
                ];
            }

            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir) && seen.Add(dir))
                    yield return dir;
            }
        }
    }

    /// <summary>Claude-style content: a plain string, or blocks with text parts.</summary>
    private static string ContentText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    // Claude/OpenCode write "text" blocks, codex "input_text"/"output_text".
                    if (block.TryGetProperty("type", out var type) &&
                        Str(type) is "text" or "input_text" or "output_text" &&
                        block.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? "");
                }
                return string.Join("\n", parts);
            default:
                return "";
        }
    }

    private static string FileTimeUtc(string path) =>
        File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static IEnumerable<string> NewestFiles(string dir, string pattern, int take)
    {
        DirectoryInfo info;
        try { info = new DirectoryInfo(dir); }
        catch { return []; }
        FileInfo[] files;
        try { files = info.GetFiles(pattern, SearchOption.AllDirectories); }
        catch (Exception) { return []; }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(take)
            .Select(f => f.FullName);
    }

    // --- claude code ------------------------------------------------------------

    private static string ClaudeHome(string home) => Path.Combine(home, ".claude", "projects");

    private static IEnumerable<ChatSession> ClaudeSessions(string home, int perClient) =>
        NewestFiles(ClaudeHome(home), "*.jsonl", perClient)
            .Select(p => ClaudeSummary("claudecode", p))
            .Where(s => s is not null)
            .Select(s => s!);

    private static ChatSession? ClaudeSummary(string client, string path)
    {
        // Two passes would cost a full read; one line-scan collects everything.
        string? model = null, last = null, sid = Path.GetFileNameWithoutExtension(path);
        var turns = 0;
        foreach (var line in ReadLinesShared(path))
        {
            using var doc = Parse(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (Str(root.GetPropertyOrDefault("type")) is not ("user" or "assistant")) continue;
            if (root.GetPropertyOrDefault("isSidechain").ValueKind == JsonValueKind.True) continue;
            if (++turns > 4 && model is not null && last is not null) continue;
            if (root.TryGetProperty("timestamp", out var ts)) last = Str(ts);
            if (root.TryGetProperty("sessionId", out var s) && Str(s) is { Length: > 0 }) sid = Str(s)!;
            if (root.TryGetProperty("message", out var m) &&
                m.TryGetProperty("model", out var mod) && Str(mod) is { Length: > 0 })
                model ??= Str(mod);
        }
        return new ChatSession(client, sid, model ?? "?", last ?? FileTimeUtc(path), turns, path);
    }

    private static IReadOnlyList<ChatTurn> ClaudeRead(string home, string sessionId)
    {
        var path = NewestFiles(ClaudeHome(home), $"{sessionId}.jsonl", 1).FirstOrDefault()
            ?? throw new AgoraException($"no claudecode session '{sessionId}' found");
        var turns = new List<ChatTurn>();
        foreach (var line in ReadLinesShared(path))
        {
            using var doc = Parse(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Str(root.GetPropertyOrDefault("type"));
            if (type is not ("user" or "assistant")) continue;
            if (root.GetPropertyOrDefault("isSidechain").ValueKind == JsonValueKind.True) continue;
            if (!root.TryGetProperty("message", out var message)) continue;
            var text = ContentText(message.GetPropertyOrDefault("content"));
            if (text.Length == 0) continue;
            turns.Add(new ChatTurn(type, text, Str(root.GetPropertyOrDefault("timestamp"))));
        }
        return turns;
    }

    // --- zcode ------------------------------------------------------------------
    // One rollout per session; the LAST model_io record's request.body.messages
    // carries the whole conversation in Claude message format — one read yields
    // everything.

    private static string ZcodeHome(string home) => Path.Combine(home, ".zcode", "cli", "rollout");

    private static IEnumerable<ChatSession> ZcodeSessions(string home, int perClient) =>
        NewestFiles(ZcodeHome(home), "model-io-sess_*.jsonl", perClient)
            .Select(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                var sid = name["model-io-".Length..];
                return new ChatSession("zcode", sid, ZcodeModel(p), FileTimeUtc(p), -1, p);
            });

    private static string ZcodeModel(string path)
    {
        // The model id sits on every record; the last line is cheapest.
        string? last = null;
        foreach (var line in ReadLinesShared(path)) last = line;
        if (last is null) return "?";
        using var doc = Parse(last);
        if (doc?.RootElement.TryGetProperty("model", out var m) == true &&
            m.TryGetProperty("modelId", out var id))
            return Str(id) ?? "?";
        return "?";
    }

    private static IReadOnlyList<ChatTurn> ZcodeRead(string home, string sessionId)
    {
        var path = NewestFiles(ZcodeHome(home), $"model-io-{sessionId}.jsonl", 1).FirstOrDefault()
            ?? NewestFiles(ZcodeHome(home), $"*{sessionId}*.jsonl", 1).FirstOrDefault()
            ?? throw new AgoraException($"no zcode session '{sessionId}' found");
        string? last = null;
        foreach (var line in ReadLinesShared(path)) last = line;
        using var doc = last is null ? null : Parse(last);
        // Current rollouts nest the API body under request.body; older ones put
        // the messages directly on request.
        if (doc is null ||
            doc.RootElement.TryGetProperty("request", out var request) != true)
            return [];
        var messages = request.GetPropertyOrDefault("body").GetPropertyOrDefault("messages");
        if (messages.ValueKind != JsonValueKind.Array)
            messages = request.GetPropertyOrDefault("messages");
        if (messages.ValueKind != JsonValueKind.Array)
            return [];
        var turns = new List<ChatTurn>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = Str(message.GetPropertyOrDefault("role")) ?? "?";
            var text = ContentText(message.GetPropertyOrDefault("content"));
            if (text.Length > 0) turns.Add(new ChatTurn(role, text, null));
        }
        return turns;
    }

    // --- kimi code ----------------------------------------------------------------
    // wire.jsonl: turn.prompt records carry the user's prompts; the assistant
    // side lives in appended message records. Listed by file freshness.

    private static string KimiHome(string home) => Path.Combine(home, ".kimi-code", "sessions");

    private static IEnumerable<ChatSession> KimiSessions(string home, int perClient) =>
        NewestFiles(KimiHome(home), "wire.jsonl", perClient)
            .Select(p =>
            {
                var sessionDir = Directory.GetParent(Directory.GetParent(
                    Directory.GetParent(p)!.FullName)!.FullName)!.Name;
                return new ChatSession("kimicode", sessionDir, "?", FileTimeUtc(p), -1, p);
            });

    private static IReadOnlyList<ChatTurn> KimiRead(string home, string sessionId)
    {
        var path = NewestFiles(KimiHome(home), "wire.jsonl", int.MaxValue)
            .FirstOrDefault(p => p.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgoraException($"no kimicode session '{sessionId}' found");
        var turns = new List<ChatTurn>();
        foreach (var line in ReadLinesShared(path))
        {
            using var doc = Parse(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Str(root.GetPropertyOrDefault("type"));
            if (type == "turn.prompt")
            {
                var text = ContentText(root.GetPropertyOrDefault("input"));
                if (text.Length > 0)
                    turns.Add(new ChatTurn("user", text, Str(root.GetPropertyOrDefault("time"))));
            }
            else if (type == "agent.message.appended")
            {
                var message = root.GetPropertyOrDefault("message");
                var inner = message.GetPropertyOrDefault("message");
                var role = Str(inner.GetPropertyOrDefault("role"));
                var text = ContentText(inner.GetPropertyOrDefault("content"));
                if (text.Length == 0) text = ContentText(message.GetPropertyOrDefault("content"));
                if (text.Length > 0 && role != "user")
                    turns.Add(new ChatTurn(role ?? "assistant", text,
                        Str(root.GetPropertyOrDefault("time"))));
            }
        }
        return turns;
    }

    // --- codex ---------------------------------------------------------------------
    // rollout-*.jsonl: event_msg payloads carry user messages, response_item
    // payloads the assistant's. payload.message / payload.content[].

    private static string CodexHome(string home) => Path.Combine(home, ".codex", "sessions");

    private static IEnumerable<ChatSession> CodexSessions(string home, int perClient) =>
        NewestFiles(CodexHome(home), "rollout-*.jsonl", perClient)
            .Select(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p); // rollout-<ts>-<uuid>
                var parts = name.Split('-');
                var sid = parts.Length > 2 ? string.Join("-", parts[^5..]) : name;
                return new ChatSession("codex", sid, "?", FileTimeUtc(p), -1, p);
            });

    private static IReadOnlyList<ChatTurn> CodexRead(string home, string sessionId)
    {
        var path = NewestFiles(CodexHome(home), "rollout-*.jsonl", int.MaxValue)
            .FirstOrDefault(p => p.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgoraException($"no codex session '{sessionId}' found");
        var turns = new List<ChatTurn>();
        foreach (var line in ReadLinesShared(path))
        {
            using var doc = Parse(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Str(root.GetPropertyOrDefault("type"));
            if (type != "event_msg" && type != "response_item") continue;
            if (!root.TryGetProperty("payload", out var payload)) continue;
            var stamp = Str(root.GetPropertyOrDefault("timestamp"));
            var payloadType = Str(payload.GetPropertyOrDefault("type"));
            if (type == "event_msg" && payloadType == "user_message" &&
                payload.TryGetProperty("message", out var msg))
                turns.Add(new ChatTurn("user", Str(msg) ?? "", stamp));
            else if (type == "response_item" && payloadType == "message")
            {
                var role = Str(payload.GetPropertyOrDefault("role")) ?? "assistant";
                var text = ContentText(payload.GetPropertyOrDefault("content"));
                if (text.Length > 0) turns.Add(new ChatTurn(role, text, stamp));
            }
        }
        return turns;
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value)
            ? value
            : default;
}
