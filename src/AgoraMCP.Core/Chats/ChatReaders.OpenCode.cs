using System.Text.Json;
using AgoraMCP.Database;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Chats;

public static partial class ChatReaders
{
    // --- opencode ---------------------------------------------------------------
    // SQLite store at $XDG_DATA_HOME/opencode/opencode.db or
    // ~/.local/share/opencode/opencode.db.
    // Supports both the v2 schema (session_v2 + session_message) and the v1
    // schema (session + message + part), strictly read-only.

    private static string OpenCodeDbPath(string? explicitHome, string resolvedHome)
    {
        if (!IsSyntheticHome(explicitHome, resolvedHome))
        {
            var xdg = NonEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME"));
            if (xdg is not null)
            {
                var xdgPath = Path.Combine(xdg, "opencode", "opencode.db");
                if (File.Exists(xdgPath)) return xdgPath;
            }
        }
        return Path.Combine(resolvedHome, ".local", "share", "opencode", "opencode.db");
    }

    private static IEnumerable<ChatSession> OpenCodeSessions(
        string? explicitHome, string resolvedHome, int perClient)
    {
        var dbPath = OpenCodeDbPath(explicitHome, resolvedHome);
        if (!File.Exists(dbPath)) return [];

        using var conn = OpenReadOnlySqlite(dbPath);
        var hasV2 = SqliteTableExists(conn, "session_v2");
        var hasV1 = SqliteTableExists(conn, "session");
        if (!hasV2 && !hasV1) return [];

        var byId = new Dictionary<string, (string Model, long UpdatedMs)>(StringComparer.Ordinal);

        if (hasV2)
            CollectOpenCodeSessionRows(conn, "session_v2", perClient, byId);
        if (hasV1)
            CollectOpenCodeSessionRows(conn, "session", perClient, byId);

        var hasV1Msg = SqliteTableExists(conn, "message");
        var hasV2Msg = SqliteTableExists(conn, "session_message");

        var top = byId
            .OrderByDescending(kv => kv.Value.UpdatedMs)
            .Take(perClient)
            .Select(kv =>
            {
                var model = kv.Value.Model;
                if (model == "?")
                    model = OpenCodeFallbackModel(conn, kv.Key, hasV2Msg, hasV1Msg) ?? "?";
                var stamp = FormatEpoch(kv.Value.UpdatedMs) ?? FileTimeUtc(dbPath);
                return new ChatSession("opencode", kv.Key, model, stamp, -1, dbPath);
            })
            .ToList();

        return top;
    }

    private static void CollectOpenCodeSessionRows(
        SqliteConnection conn,
        string table,
        int limit,
        Dictionary<string, (string Model, long UpdatedMs)> byId)
    {
        var hasModel = SqliteColumnExists(conn, table, "model");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasModel
            ? $"SELECT id, model, time_updated FROM \"{table}\" ORDER BY time_updated DESC LIMIT $n"
            : $"SELECT id, NULL, time_updated FROM \"{table}\" ORDER BY time_updated DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (id.Length == 0) continue;
            var model = ExtractOpenCodeModel(reader.IsDBNull(1) ? null : reader.GetString(1));
            var updated = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            if (!byId.TryGetValue(id, out var existing) || updated > existing.UpdatedMs)
            {
                var bestModel = model != "?" ? model : (existing.Model ?? "?");
                byId[id] = (bestModel, updated);
            }
            else if (existing.Model == "?" && model != "?")
            {
                byId[id] = (model, existing.UpdatedMs);
            }
        }
    }

    private static string ExtractOpenCodeModel(string? raw)
    {
        var s = NonEmpty(raw);
        if (s is null) return "?";
        if (s.StartsWith('{'))
        {
            using var doc = Parse(s);
            if (doc is not null)
            {
                var root = doc.RootElement;
                var id = NonEmpty(Str(root.GetPropertyOrDefault("id")))
                    ?? NonEmpty(Str(root.GetPropertyOrDefault("modelID")))
                    ?? NonEmpty(Str(root.GetPropertyOrDefault("model")));
                if (id is not null) return id;
            }
            return "?";
        }
        return s;
    }

    private static string? OpenCodeFallbackModel(
        SqliteConnection conn, string sessionId, bool hasV2Msg, bool hasV1Msg)
    {
        if (hasV2Msg)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT data FROM session_message
                WHERE session_id = $sid AND type IN ('assistant', 'user')
                ORDER BY seq DESC, time_created DESC LIMIT 5
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                using var doc = Parse(reader.GetString(0));
                if (doc is null) continue;
                var root = doc.RootElement;
                var model = NonEmpty(Str(root.GetPropertyOrDefault("model").GetPropertyOrDefault("id")))
                    ?? NonEmpty(Str(root.GetPropertyOrDefault("metadata").GetPropertyOrDefault("model").GetPropertyOrDefault("modelID")));
                if (model is not null) return model;
            }
        }
        if (hasV1Msg)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT data FROM message
                WHERE session_id = $sid
                ORDER BY time_created DESC LIMIT 5
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                using var doc = Parse(reader.GetString(0));
                if (doc is null) continue;
                var root = doc.RootElement;
                var model = NonEmpty(Str(root.GetPropertyOrDefault("modelID")))
                    ?? NonEmpty(Str(root.GetPropertyOrDefault("model").GetPropertyOrDefault("modelID")))
                    ?? NonEmpty(Str(root.GetPropertyOrDefault("model").GetPropertyOrDefault("id")));
                if (model is not null) return model;
            }
        }
        return null;
    }

    private static IReadOnlyList<ChatTurn> OpenCodeRead(
        string? explicitHome, string resolvedHome, string sessionId)
    {
        var dbPath = OpenCodeDbPath(explicitHome, resolvedHome);
        if (!File.Exists(dbPath))
            throw new AgoraException($"no opencode session '{sessionId}' found");

        using var conn = OpenReadOnlySqlite(dbPath);
        var exists = OpenCodeSessionExists(conn, "session_v2", sessionId)
            || OpenCodeSessionExists(conn, "session", sessionId);
        if (!exists)
            throw new AgoraException($"no opencode session '{sessionId}' found");

        if (SqliteTableExists(conn, "session_message"))
        {
            var v2Turns = OpenCodeReadV2(conn, sessionId);
            if (v2Turns.Count > 0) return v2Turns;
        }

        if (SqliteTableExists(conn, "message") && SqliteTableExists(conn, "part"))
            return OpenCodeReadV1(conn, sessionId);

        return [];
    }

    private static bool OpenCodeSessionExists(SqliteConnection conn, string table, string sessionId)
    {
        if (!SqliteTableExists(conn, table)) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM \"{table}\" WHERE id = $sid LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        return cmd.ExecuteScalar() is not null;
    }

    private static List<ChatTurn> OpenCodeReadV2(SqliteConnection conn, string sessionId)
    {
        var turns = new List<ChatTurn>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type, time_created, data
            FROM session_message
            WHERE session_id = $sid AND type IN ('user', 'assistant')
            ORDER BY seq ASC, time_created ASC
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var created = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            var raw = reader.IsDBNull(2) ? "" : reader.GetString(2);
            using var doc = Parse(raw);
            if (doc is null) continue;
            var root = doc.RootElement;
            var text = type == "user"
                ? (Str(root.GetPropertyOrDefault("text")) ?? "").Trim()
                : ContentText(root.GetPropertyOrDefault("content")).Trim();
            if (text.Length > 0)
                turns.Add(new ChatTurn(type, text, FormatEpoch(created)));
        }
        return turns;
    }

    private static List<ChatTurn> OpenCodeReadV1(SqliteConnection conn, string sessionId)
    {
        var turns = new List<ChatTurn>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.time_created, m.data, p.data
            FROM message m
            JOIN part p ON p.message_id = m.id
            WHERE m.session_id = $sid
            ORDER BY m.time_created ASC, p.time_created ASC, p.id ASC
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);

        string? currentMsgId = null;
        string currentRole = "assistant";
        long currentCreated = 0;
        var currentParts = new List<string>();

        void Flush()
        {
            if (currentMsgId is null || currentParts.Count == 0) return;
            var text = string.Join("\n", currentParts).Trim();
            if (text.Length > 0 && currentRole is "user" or "assistant")
                turns.Add(new ChatTurn(currentRole, text, FormatEpoch(currentCreated)));
            currentParts.Clear();
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var msgId = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (msgId != currentMsgId)
            {
                Flush();
                currentMsgId = msgId;
                currentCreated = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var msgRaw = reader.IsDBNull(2) ? "" : reader.GetString(2);
                using var msgDoc = Parse(msgRaw);
                currentRole = msgDoc is not null
                    ? Str(msgDoc.RootElement.GetPropertyOrDefault("role")) ?? "assistant"
                    : "assistant";
            }

            var partRaw = reader.IsDBNull(3) ? "" : reader.GetString(3);
            using var partDoc = Parse(partRaw);
            if (partDoc is null) continue;
            var root = partDoc.RootElement;
            if (Str(root.GetPropertyOrDefault("type")) == "text")
            {
                var partText = Str(root.GetPropertyOrDefault("text"));
                if (!string.IsNullOrEmpty(partText))
                    currentParts.Add(partText);
            }
        }
        Flush();
        return turns;
    }
}
