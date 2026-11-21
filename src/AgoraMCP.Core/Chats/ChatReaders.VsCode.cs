using System.Text.Json;
using AgoraMCP.Database;

namespace AgoraMCP.Chats;

public static partial class ChatReaders
{
    // --- vs code chat -----------------------------------------------------------
    // Stored under <User>/workspaceStorage/*/chatSessions/*.jsonl (and legacy
    // *.json) plus <User>/globalStorage/emptyWindowChatSessions/*.jsonl.
    // VS Code's JSONL format is a state delta log:
    //   kind=0: initial snapshot object in "v" (may include initial requests)
    //   kind=1: set value "v" at path "k"
    //   kind=2: splice/append array "v" at path "k" (optional int "i" truncates
    //           the target array at index i before appending v).
    // Plain assistant markdown chunks are response objects WITHOUT a "kind"
    // property whose text lives in "value"; thinking/tool/reference objects carry
    // explicit "kind" strings and are skipped.

    private sealed class VsCodeRequest(string userText, string? timestamp, List<string?> responseItems)
    {
        public string UserText { get; } = userText;
        public string? Timestamp { get; } = timestamp;
        public List<string?> ResponseItems { get; } = responseItems;
    }

    private static IEnumerable<(string Path, DateTime LastWriteUtc)> VsCodeSessionFiles(
        string? explicitHome, string resolvedHome)
    {
        var files = new List<(string Path, DateTime LastWriteUtc)>();

        void CollectDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(dir); }
            catch (Exception) { return; }

            foreach (var f in entries)
            {
                if (!f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                    !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { files.Add((f, File.GetLastWriteTimeUtc(f))); }
                catch (IOException) { }
            }
        }

        foreach (var userDir in AppDataUserDirs(explicitHome, resolvedHome, "Code", "Code - Insiders"))
        {
            CollectDir(Path.Combine(userDir, "globalStorage", "emptyWindowChatSessions"));

            var wsRoot = Path.Combine(userDir, "workspaceStorage");
            if (!Directory.Exists(wsRoot)) continue;
            IEnumerable<string> wsDirs;
            try { wsDirs = Directory.EnumerateDirectories(wsRoot); }
            catch (Exception) { continue; }
            foreach (var wsDir in wsDirs)
                CollectDir(Path.Combine(wsDir, "chatSessions"));
        }

        return files.OrderByDescending(x => x.LastWriteUtc);
    }

    private static IEnumerable<ChatSession> VsCodeSessions(
        string? explicitHome, string resolvedHome, int perClient)
    {
        var sessions = new List<ChatSession>();
        foreach (var file in VsCodeSessionFiles(explicitHome, resolvedHome))
        {
            if (sessions.Count >= perClient) break;
            var summary = VsCodeSummary(file.Path, file.LastWriteUtc);
            if (summary is not null) sessions.Add(summary);
        }
        return sessions;
    }

    private static ChatSession? VsCodeSummary(string path, DateTime lastWriteUtc)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(path);
        var stamp = lastWriteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");

        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = Parse(ReadAllTextShared(path));
                if (doc is null) return null;
                var root = doc.RootElement;
                var requests = root.GetPropertyOrDefault("requests");
                if (requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() == 0)
                    return null;
                var sid = NonEmpty(Str(root.GetPropertyOrDefault("sessionId"))) ?? fallbackId;
                var model = ExtractVsCodeModelFromSnapshot(root) ?? ExtractVsCodeModelFromRequests(requests) ?? "?";
                return new ChatSession("vscode", sid, model, stamp, -1, path);
            }

            string sidJsonl = fallbackId;
            string? modelJsonl = null;
            var hasRequest = false;
            var lineNo = 0;

            foreach (var line in ReadLinesShared(path))
            {
                lineNo++;
                if (lineNo > 1 && !line.Contains("\"requests\"", StringComparison.Ordinal))
                    continue;
                using var doc = Parse(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var kind = IntOrNull(root.GetPropertyOrDefault("kind"));
                var v = root.GetPropertyOrDefault("v");

                if (kind == 0 && v.ValueKind == JsonValueKind.Object)
                {
                    sidJsonl = NonEmpty(Str(v.GetPropertyOrDefault("sessionId"))) ?? sidJsonl;
                    modelJsonl ??= ExtractVsCodeModelFromSnapshot(v);
                    var reqs = v.GetPropertyOrDefault("requests");
                    if (reqs.ValueKind == JsonValueKind.Array && reqs.GetArrayLength() > 0)
                    {
                        hasRequest = true;
                        modelJsonl = ExtractVsCodeModelFromRequests(reqs) ?? modelJsonl;
                        break;
                    }
                }
                else if (kind == 2 && IsPath(root.GetPropertyOrDefault("k"), "requests") &&
                         v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                {
                    hasRequest = true;
                    modelJsonl = ExtractVsCodeModelFromRequests(v) ?? modelJsonl;
                    break;
                }
            }

            // Skip blank new-chat tabs (2 KB shells with zero requests).
            return hasRequest ? new ChatSession("vscode", sidJsonl, modelJsonl ?? "?", stamp, -1, path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ExtractVsCodeModelFromSnapshot(JsonElement snapshot)
    {
        var selected = snapshot.GetPropertyOrDefault("inputState").GetPropertyOrDefault("selectedModel");
        if (selected.ValueKind == JsonValueKind.String)
            return NonEmpty(selected.GetString());
        if (selected.ValueKind == JsonValueKind.Object)
        {
            return NonEmpty(Str(selected.GetPropertyOrDefault("identifier")))
                ?? NonEmpty(Str(selected.GetPropertyOrDefault("metadata").GetPropertyOrDefault("id")));
        }
        return null;
    }

    private static string? ExtractVsCodeModelFromRequests(JsonElement requestsArray)
    {
        foreach (var req in requestsArray.EnumerateArray())
        {
            var m = NonEmpty(Str(req.GetPropertyOrDefault("modelId")));
            if (m is not null) return m;
        }
        return null;
    }

    private static IReadOnlyList<ChatTurn> VsCodeRead(
        string? explicitHome, string resolvedHome, string sessionId)
    {
        var files = VsCodeSessionFiles(explicitHome, resolvedHome).ToList();
        var path = files
            .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Path)
                .Equals(sessionId, StringComparison.OrdinalIgnoreCase)).Path
            ?? files.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Path)
                .Contains(sessionId, StringComparison.OrdinalIgnoreCase)).Path;

        if (path is null)
            throw new AgoraException($"no vscode session '{sessionId}' found");

        var requests = new List<VsCodeRequest>();

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = Parse(ReadAllTextShared(path));
            if (doc is not null)
                AppendVsCodeRequests(requests, doc.RootElement.GetPropertyOrDefault("requests"), spliceIndex: 0);
        }
        else
        {
            foreach (var line in ReadLinesShared(path))
            {
                using var doc = Parse(line);
                if (doc is null) continue;
                var root = doc.RootElement;
                var kind = IntOrNull(root.GetPropertyOrDefault("kind"));
                var v = root.GetPropertyOrDefault("v");

                if (kind == 0 && v.ValueKind == JsonValueKind.Object)
                {
                    AppendVsCodeRequests(requests, v.GetPropertyOrDefault("requests"), spliceIndex: 0);
                    continue;
                }

                var k = root.GetPropertyOrDefault("k");
                if (k.ValueKind != JsonValueKind.Array) continue;

                if (kind == 2 && IsPath(k, "requests"))
                {
                    var splice = IntOrNull(root.GetPropertyOrDefault("i"));
                    AppendVsCodeRequests(requests, v, splice);
                }
                else if (kind is 1 or 2 && TryMatchRequestResponsePath(k, out var reqIdx))
                {
                    if (reqIdx < 0 || reqIdx >= requests.Count || v.ValueKind != JsonValueKind.Array) continue;
                    var items = requests[reqIdx].ResponseItems;
                    if (kind == 1)
                    {
                        items.Clear();
                    }
                    else if (IntOrNull(root.GetPropertyOrDefault("i")) is { } spliceIdx)
                    {
                        var cut = Math.Clamp(spliceIdx, 0, items.Count);
                        if (cut < items.Count) items.RemoveRange(cut, items.Count - cut);
                    }
                    foreach (var el in v.EnumerateArray())
                        items.Add(ExtractVsCodeResponseItemText(el));
                }
                else if (kind == 1 && TryMatchRequestResponseItemValuePath(k, out var rIdx, out var itemIdx))
                {
                    if (rIdx >= 0 && rIdx < requests.Count && v.ValueKind == JsonValueKind.String)
                    {
                        var items = requests[rIdx].ResponseItems;
                        if (itemIdx >= 0 && itemIdx < items.Count)
                            items[itemIdx] = v.GetString();
                    }
                }
            }
        }

        var turns = new List<ChatTurn>();
        foreach (var req in requests)
        {
            var userText = req.UserText.Trim();
            if (userText.Length > 0)
                turns.Add(new ChatTurn("user", userText, req.Timestamp));

            var assistantText = string.Concat(req.ResponseItems.Where(s => s is not null)).Trim();
            if (assistantText.Length > 0)
                turns.Add(new ChatTurn("assistant", assistantText, req.Timestamp));
        }
        return turns;
    }

    private static void AppendVsCodeRequests(List<VsCodeRequest> target, JsonElement arr, int? spliceIndex)
    {
        if (arr.ValueKind != JsonValueKind.Array) return;
        if (spliceIndex is { } idx)
        {
            var cut = Math.Clamp(idx, 0, target.Count);
            if (cut < target.Count) target.RemoveRange(cut, target.Count - cut);
        }
        foreach (var req in arr.EnumerateArray())
        {
            if (req.ValueKind != JsonValueKind.Object) continue;
            var msgEl = req.GetPropertyOrDefault("message");
            var userText = msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : Str(msgEl.GetPropertyOrDefault("text")) ?? "";
            var tsEl = req.GetPropertyOrDefault("timestamp");
            string? stamp = tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var ms)
                ? FormatEpoch(ms)
                : Str(tsEl);

            var respItems = new List<string?>();
            var respEl = req.GetPropertyOrDefault("response");
            if (respEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in respEl.EnumerateArray())
                    respItems.Add(ExtractVsCodeResponseItemText(item));
            }
            target.Add(new VsCodeRequest(userText, stamp, respItems));
        }
    }

    private static string? ExtractVsCodeResponseItemText(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        // Tool invocations, thinking traces, inline references, etc. carry an explicit "kind".
        if (item.TryGetProperty("kind", out var kindEl) &&
            kindEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(kindEl.GetString()))
            return null;
        return item.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String
            ? valEl.GetString()
            : null;
    }

    private static int? IntOrNull(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static bool IsPath(JsonElement k, string singleSegment) =>
        k.ValueKind == JsonValueKind.Array &&
        k.GetArrayLength() == 1 &&
        Str(k[0]) == singleSegment;

    private static bool TryMatchRequestResponsePath(JsonElement k, out int reqIndex)
    {
        reqIndex = -1;
        if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() != 3) return false;
        if (Str(k[0]) != "requests" || Str(k[2]) != "response") return false;
        if (IntOrNull(k[1]) is not { } idx) return false;
        reqIndex = idx;
        return true;
    }

    private static bool TryMatchRequestResponseItemValuePath(JsonElement k, out int reqIndex, out int itemIndex)
    {
        reqIndex = -1;
        itemIndex = -1;
        if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() != 5) return false;
        if (Str(k[0]) != "requests" || Str(k[2]) != "response" || Str(k[4]) != "value") return false;
        if (IntOrNull(k[1]) is not { } r || IntOrNull(k[3]) is not { } i) return false;
        reqIndex = r;
        itemIndex = i;
        return true;
    }
}
