using System.Text.Json;
using System.Text.RegularExpressions;
using AgoraMCP.Database;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Chats;

public static partial class ChatReaders
{
    // --- cursor -----------------------------------------------------------------
    // Cursor stores conversations in two SQLite databases under
    // <CursorUser>/globalStorage/, both read strictly read-only:
    //   1. conversation-search.db: `conversations` joined with `conversation_fts`
    //      (covers Cloud/Project agents and indexed local chats). Its `body`
    //      lists turns in REVERSE chronological order ("assistant:\n...\n\nuser:\n..."),
    //      so we reverse the parsed blocks to return oldest -> newest.
    //   2. state.vscdb (`cursorDiskKV`): classic local Composer chats stored under
    //      `composerData:<id>` + `bubbleId:<id>:<bubbleId>` (type 1=user, 2=assistant).

    [GeneratedRegex(@"(?:^|\r?\n\r?\n)(user|assistant|system):\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex CursorFtsTurnBoundary();

    private static IEnumerable<ChatSession> CursorSessions(
        string? explicitHome, string resolvedHome, int perClient)
    {
        var byId = new Dictionary<string, ChatSession>(StringComparer.OrdinalIgnoreCase);

        foreach (var userDir in AppDataUserDirs(explicitHome, resolvedHome, "Cursor"))
        {
            var globalStorage = Path.Combine(userDir, "globalStorage");
            CollectCursorStateVscdbSessions(Path.Combine(globalStorage, "state.vscdb"), byId);
            CollectCursorSearchDbSessions(Path.Combine(globalStorage, "conversation-search.db"), perClient, byId);
        }

        return byId.Values
            .OrderByDescending(s => s.LastActivity, StringComparer.Ordinal)
            .Take(perClient);
    }

    private static void CollectCursorSearchDbSessions(
        string dbPath, int perClient, Dictionary<string, ChatSession> byId)
    {
        if (!File.Exists(dbPath)) return;
        try
        {
            using var conn = OpenReadOnlySqlite(dbPath);
            if (!SqliteTableExists(conn, "conversations") || !SqliteTableExists(conn, "conversation_fts"))
                return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.updated_at
                FROM conversations c
                JOIN conversation_fts f ON f.rowid = c.fts_rowid
                WHERE length(f.body) > 0
                ORDER BY c.updated_at DESC
                LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", perClient);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (id.Length == 0) continue;
                var updated = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                var stamp = FormatEpoch(updated) ?? FileTimeUtc(dbPath);
                if (!byId.TryGetValue(id, out var existing) ||
                    string.CompareOrdinal(stamp, existing.LastActivity) > 0)
                {
                    var model = existing?.Model is { Length: > 0 } m && m != "?" ? m : "?";
                    byId[id] = new ChatSession("cursor", id, model, stamp, -1, dbPath);
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException) { }
    }

    private static void CollectCursorStateVscdbSessions(
        string dbPath, Dictionary<string, ChatSession> byId)
    {
        if (!File.Exists(dbPath)) return;
        try
        {
            using var conn = OpenReadOnlySqlite(dbPath);
            if (!SqliteTableExists(conn, "cursorDiskKV")) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM cursorDiskKV WHERE key LIKE 'composerData:%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var raw = ReadSqliteText(reader, 1);
                if (raw is null) continue;
                using var doc = Parse(raw);
                if (doc is null) continue;
                var root = doc.RootElement;

                var headers = root.GetPropertyOrDefault("fullConversationHeadersOnly");
                var inline = root.GetPropertyOrDefault("conversation");
                var hasHeaders = headers.ValueKind == JsonValueKind.Array && headers.GetArrayLength() > 0;
                var hasInline = inline.ValueKind == JsonValueKind.Array && inline.GetArrayLength() > 0;
                if (!hasHeaders && !hasInline) continue;

                var sid = NonEmpty(Str(root.GetPropertyOrDefault("composerId")))
                    ?? (key.Length > "composerData:".Length ? key["composerData:".Length..] : "");
                if (sid.Length == 0) continue;

                var stamp = ExtractCursorTimestamp(root, "lastUpdatedAt")
                    ?? ExtractCursorTimestamp(root, "createdAt")
                    ?? FileTimeUtc(dbPath);
                var model = NonEmpty(Str(root.GetPropertyOrDefault("modelConfig").GetPropertyOrDefault("modelName")))
                    ?? "?";
                var turns = hasHeaders ? headers.GetArrayLength() : inline.GetArrayLength();

                if (!byId.TryGetValue(sid, out var existing) ||
                    string.CompareOrdinal(stamp, existing.LastActivity) >= 0)
                {
                    byId[sid] = new ChatSession("cursor", sid, model, stamp, turns, dbPath);
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException) { }
    }

    private static IReadOnlyList<ChatTurn> CursorRead(
        string? explicitHome, string resolvedHome, string sessionId)
    {
        foreach (var userDir in AppDataUserDirs(explicitHome, resolvedHome, "Cursor"))
        {
            var globalStorage = Path.Combine(userDir, "globalStorage");

            // Prefer structured local composer bubbles when present (they carry timestamps).
            var stateDb = Path.Combine(globalStorage, "state.vscdb");
            if (TryCursorReadFromStateVscdb(stateDb, sessionId, out var stateTurns))
                return stateTurns;

            var searchDb = Path.Combine(globalStorage, "conversation-search.db");
            if (TryCursorReadFromSearchDb(searchDb, sessionId, out var searchTurns))
                return searchTurns;
        }

        throw new AgoraException($"no cursor session '{sessionId}' found");
    }

    private static bool TryCursorReadFromStateVscdb(
        string dbPath, string sessionId, out IReadOnlyList<ChatTurn> turns)
    {
        turns = [];
        if (!File.Exists(dbPath)) return false;

        using var conn = OpenReadOnlySqlite(dbPath);
        if (!SqliteTableExists(conn, "cursorDiskKV")) return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = $k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", $"composerData:{sessionId}");
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        var raw = ReadSqliteText(reader, 0);
        if (raw is null) return false;

        using var doc = Parse(raw);
        if (doc is null) return false;
        var root = doc.RootElement;

        var list = new List<ChatTurn>();
        var headers = root.GetPropertyOrDefault("fullConversationHeadersOnly");
        if (headers.ValueKind == JsonValueKind.Array && headers.GetArrayLength() > 0)
        {
            foreach (var h in headers.EnumerateArray())
            {
                var bubbleId = NonEmpty(Str(h.GetPropertyOrDefault("bubbleId")));
                if (bubbleId is null) continue;
                using var bCmd = conn.CreateCommand();
                bCmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = $k LIMIT 1";
                bCmd.Parameters.AddWithValue("$k", $"bubbleId:{sessionId}:{bubbleId}");
                using var bReader = bCmd.ExecuteReader();
                if (!bReader.Read()) continue;
                var bRaw = ReadSqliteText(bReader, 0);
                if (bRaw is null) continue;
                using var bDoc = Parse(bRaw);
                if (bDoc is null) continue;
                if (ParseCursorBubble(bDoc.RootElement, IntOrNull(h.GetPropertyOrDefault("type"))) is { } turn)
                    list.Add(turn);
            }
        }
        else if (root.GetPropertyOrDefault("conversation") is { ValueKind: JsonValueKind.Array } inline &&
                 inline.GetArrayLength() > 0)
        {
            foreach (var item in inline.EnumerateArray())
            {
                if (ParseCursorBubble(item, null) is { } turn)
                    list.Add(turn);
            }
        }

        if (list.Count == 0) return false;
        turns = list;
        return true;
    }

    private static ChatTurn? ParseCursorBubble(JsonElement bubble, int? fallbackType)
    {
        var type = IntOrNull(bubble.GetPropertyOrDefault("type")) ?? fallbackType;
        var role = type switch
        {
            1 => "user",
            2 => "assistant",
            _ => null,
        };
        if (role is null) return null;

        var text = NonEmpty(Str(bubble.GetPropertyOrDefault("text")))
            ?? NonEmpty(Str(bubble.GetPropertyOrDefault("richText")));
        if (text is null) return null;

        var stamp = ExtractCursorTimestamp(bubble, "createdAt")
            ?? ExtractCursorTimestamp(bubble, "timestamp");
        return new ChatTurn(role, text, stamp);
    }

    private static bool TryCursorReadFromSearchDb(
        string dbPath, string sessionId, out IReadOnlyList<ChatTurn> turns)
    {
        turns = [];
        if (!File.Exists(dbPath)) return false;

        using var conn = OpenReadOnlySqlite(dbPath);
        if (!SqliteTableExists(conn, "conversations") || !SqliteTableExists(conn, "conversation_fts"))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.body
            FROM conversations c
            JOIN conversation_fts f ON f.rowid = c.fts_rowid
            WHERE c.id = $sid
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var bodyObj = cmd.ExecuteScalar();
        if (bodyObj is null || bodyObj is DBNull) return false;

        var body = bodyObj.ToString() ?? "";
        turns = ParseCursorFtsBody(body);
        return true;
    }

    private static List<ChatTurn> ParseCursorFtsBody(string body)
    {
        var turns = new List<ChatTurn>();
        if (string.IsNullOrWhiteSpace(body)) return turns;

        var matches = CursorFtsTurnBoundary().Matches(body);
        if (matches.Count == 0) return turns;

        for (var i = 0; i < matches.Count; i++)
        {
            var role = matches[i].Groups[1].Value;
            if (role is not ("user" or "assistant")) continue;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            if (end <= start) continue;
            var text = body[start..end].Trim();
            if (text.Length > 0)
                turns.Add(new ChatTurn(role, text, null));
        }

        // conversation_fts stores turns newest-first; flip to chronological order.
        turns.Reverse();
        return turns;
    }

    private static string? ExtractCursorTimestamp(JsonElement obj, string property)
    {
        var el = obj.GetPropertyOrDefault(property);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
            return FormatEpoch(ms);
        return NonEmpty(Str(el));
    }

    private static string? ReadSqliteText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var val = reader.GetValue(ordinal);
        return val switch
        {
            string s => s,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => val.ToString(),
        };
    }
}
