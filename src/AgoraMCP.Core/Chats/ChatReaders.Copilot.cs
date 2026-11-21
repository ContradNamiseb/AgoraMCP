using AgoraMCP.Database;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Chats;

public static partial class ChatReaders
{
    // --- github copilot cli -----------------------------------------------------
    // Primary: ~/.copilot/session-state/<session-id>/events.jsonl
    //   session.start / session.model_change carry selectedModel;
    //   user.message and assistant.message carry data.content + timestamp.
    // Fallback: ~/.copilot/session-store.db (SQLite: sessions, turns,
    //   assistant_usage_events) for sessions without an events.jsonl file.

    private static string CopilotStateDir(string home) => Path.Combine(home, ".copilot", "session-state");
    private static string CopilotStoreDb(string home) => Path.Combine(home, ".copilot", "session-store.db");

    private static IEnumerable<(string SessionId, string Path, DateTime LastWriteUtc)> CopilotEventFiles(string home)
    {
        var dir = CopilotStateDir(home);
        if (!Directory.Exists(dir)) return [];
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch (Exception) { return []; }

        var results = new List<(string SessionId, string Path, DateTime LastWriteUtc)>();
        foreach (var subdir in subdirs)
        {
            var events = Path.Combine(subdir, "events.jsonl");
            if (!File.Exists(events)) continue;
            try
            {
                results.Add((Path.GetFileName(subdir), events, File.GetLastWriteTimeUtc(events)));
            }
            catch (IOException) { }
        }
        return results.OrderByDescending(x => x.LastWriteUtc);
    }

    private static IEnumerable<ChatSession> CopilotSessions(string home, int perClient)
    {
        var byId = new Dictionary<string, ChatSession>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in CopilotEventFiles(home).Take(perClient))
        {
            byId[file.SessionId] = new ChatSession(
                "github-copilot",
                file.SessionId,
                CopilotEventModel(file.Path),
                file.LastWriteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                -1,
                file.Path);
        }

        var dbPath = CopilotStoreDb(home);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = OpenReadOnlySqlite(dbPath);
                if (SqliteTableExists(conn, "sessions"))
                {
                    var hasUsage = SqliteTableExists(conn, "assistant_usage_events");
                    var hasTurns = SqliteTableExists(conn, "turns");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id, updated_at, created_at FROM sessions ORDER BY updated_at DESC LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", perClient);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var sid = reader.IsDBNull(0) ? "" : reader.GetString(0);
                        if (sid.Length == 0 || byId.ContainsKey(sid)) continue;
                        var updated = !reader.IsDBNull(1) ? NonEmpty(reader.GetString(1)) : null;
                        var created = !reader.IsDBNull(2) ? NonEmpty(reader.GetString(2)) : null;
                        var stamp = updated ?? created ?? FileTimeUtc(dbPath);
                        var model = hasUsage ? CopilotDbModel(conn, sid) ?? "?" : "?";
                        var turns = hasTurns ? CopilotDbTurnCount(conn, sid) : -1;
                        byId[sid] = new ChatSession("github-copilot", sid, model, stamp, turns, dbPath);
                    }
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException) { }
        }

        return byId.Values
            .OrderByDescending(s => s.LastActivity, StringComparer.Ordinal)
            .Take(perClient);
    }

    private static string CopilotEventModel(string path)
    {
        var scanned = 0;
        try
        {
            foreach (var line in ReadLinesShared(path))
            {
                if (++scanned > 100) break;
                using var doc = Parse(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var data = root.GetPropertyOrDefault("data");
                var model = NonEmpty(Str(data.GetPropertyOrDefault("selectedModel")))
                    ?? NonEmpty(Str(data.GetPropertyOrDefault("newModel")))
                    ?? NonEmpty(Str(data.GetPropertyOrDefault("model")));
                if (model is not null) return model;
            }
        }
        catch (IOException) { }
        return "?";
    }

    private static string? CopilotDbModel(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT model FROM assistant_usage_events
            WHERE session_id = $sid AND model IS NOT NULL AND model <> ''
            ORDER BY id DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return NonEmpty(cmd.ExecuteScalar() as string);
    }

    private static int CopilotDbTurnCount(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM turns WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static IReadOnlyList<ChatTurn> CopilotRead(string home, string sessionId)
    {
        string? eventPath = null;
        if (IsSafeIdSegment(sessionId))
        {
            var direct = Path.Combine(CopilotStateDir(home), sessionId, "events.jsonl");
            if (File.Exists(direct)) eventPath = direct;
        }
        eventPath ??= CopilotEventFiles(home)
            .FirstOrDefault(x => x.SessionId.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            .Path;

        if (eventPath is not null)
        {
            var turns = new List<ChatTurn>();
            foreach (var line in ReadLinesShared(eventPath))
            {
                using var doc = Parse(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var type = Str(root.GetPropertyOrDefault("type"));
                if (type is not ("user.message" or "assistant.message")) continue;
                var text = (Str(root.GetPropertyOrDefault("data").GetPropertyOrDefault("content")) ?? "").Trim();
                if (text.Length == 0) continue;
                var role = type == "user.message" ? "user" : "assistant";
                turns.Add(new ChatTurn(role, text, Str(root.GetPropertyOrDefault("timestamp"))));
            }
            return turns;
        }

        var dbPath = CopilotStoreDb(home);
        if (File.Exists(dbPath))
        {
            using var conn = OpenReadOnlySqlite(dbPath);
            if (SqliteTableExists(conn, "sessions"))
            {
                using var check = conn.CreateCommand();
                check.CommandText = "SELECT 1 FROM sessions WHERE id = $sid LIMIT 1";
                check.Parameters.AddWithValue("$sid", sessionId);
                if (check.ExecuteScalar() is not null)
                {
                    if (!SqliteTableExists(conn, "turns")) return [];
                    var turns = new List<ChatTurn>();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        SELECT user_message, assistant_response, timestamp
                        FROM turns
                        WHERE session_id = $sid
                        ORDER BY turn_index ASC, id ASC
                        """;
                    cmd.Parameters.AddWithValue("$sid", sessionId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var stamp = reader.IsDBNull(2) ? null : NonEmpty(reader.GetString(2));
                        var userMsg = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                        var assistantMsg = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                        if (userMsg.Length > 0) turns.Add(new ChatTurn("user", userMsg, stamp));
                        if (assistantMsg.Length > 0) turns.Add(new ChatTurn("assistant", assistantMsg, stamp));
                    }
                    return turns;
                }
            }
        }

        throw new AgoraException($"no github-copilot session '{sessionId}' found");
    }
}
