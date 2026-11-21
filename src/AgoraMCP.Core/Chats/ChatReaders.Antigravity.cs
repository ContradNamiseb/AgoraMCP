using AgoraMCP.Database;

namespace AgoraMCP.Chats;

public static partial class ChatReaders
{
    // --- antigravity ------------------------------------------------------------
    // Both Antigravity 2.0 (~/.gemini/antigravity/brain) and Antigravity IDE
    // (~/.gemini/antigravity-ide/brain) write per-conversation logs at:
    //   <brain>/<conversation-id>/.system_generated/logs/transcript.jsonl
    // USER_INPUT records wrap the prompt in <USER_REQUEST>...</USER_REQUEST>;
    // PLANNER_RESPONSE records carry visible assistant text in "content" and
    // the model id in "model" (tool-only steps omit/empty "content").

    private static IEnumerable<string> AntigravityBrainRoots(string home) =>
    [
        Path.Combine(home, ".gemini", "antigravity", "brain"),
        Path.Combine(home, ".gemini", "antigravity-ide", "brain"),
    ];

    private static IEnumerable<(string SessionId, string Path, DateTime LastWriteUtc)> AntigravityTranscriptFiles(string home)
    {
        var results = new List<(string SessionId, string Path, DateTime LastWriteUtc)>();
        foreach (var root in AntigravityBrainRoots(home))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> convDirs;
            try { convDirs = Directory.EnumerateDirectories(root); }
            catch (Exception) { continue; }

            foreach (var convDir in convDirs)
            {
                var transcript = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                if (!File.Exists(transcript)) continue;
                try
                {
                    results.Add((Path.GetFileName(convDir), transcript, File.GetLastWriteTimeUtc(transcript)));
                }
                catch (IOException) { }
            }
        }
        return results.OrderByDescending(x => x.LastWriteUtc);
    }

    private static IEnumerable<ChatSession> AntigravitySessions(string home, int perClient) =>
        AntigravityTranscriptFiles(home)
            .Take(perClient)
            .Select(x => new ChatSession(
                "antigravity",
                x.SessionId,
                AntigravityModel(x.Path),
                x.LastWriteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                -1,
                x.Path));

    private static string AntigravityModel(string path)
    {
        try
        {
            // Scan the tail 64 KiB backwards first: gives the current model even
            // when a long session switched models or started before "model" was logged.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const long tailBytes = 65536;
            var startedMidFile = stream.Length > tailBytes;
            if (startedMidFile)
                stream.Seek(-tailBytes, SeekOrigin.End);

            var tailLines = new List<string>();
            using (var reader = new StreamReader(stream, leaveOpen: true))
            {
                if (startedMidFile) _ = reader.ReadLine(); // discard partial first line
                while (reader.ReadLine() is { } line)
                    tailLines.Add(line);
            }

            for (var i = tailLines.Count - 1; i >= 0; i--)
            {
                var line = tailLines[i];
                if (!line.Contains("\"model\"", StringComparison.Ordinal)) continue;
                using var doc = Parse(line);
                if (doc is null) continue;
                var model = NonEmpty(Str(doc.RootElement.GetPropertyOrDefault("model")));
                if (model is not null) return model;
            }

            if (startedMidFile)
            {
                stream.Seek(0, SeekOrigin.Begin);
                using var headReader = new StreamReader(stream);
                var scanned = 0;
                while (scanned++ < 200 && headReader.ReadLine() is { } line)
                {
                    if (!line.Contains("\"model\"", StringComparison.Ordinal)) continue;
                    using var doc = Parse(line);
                    if (doc is null) continue;
                    var model = NonEmpty(Str(doc.RootElement.GetPropertyOrDefault("model")));
                    if (model is not null) return model;
                }
            }
        }
        catch (IOException) { }
        return "?";
    }

    private static IReadOnlyList<ChatTurn> AntigravityRead(string home, string sessionId)
    {
        string? path = null;
        if (IsSafeIdSegment(sessionId))
        {
            foreach (var root in AntigravityBrainRoots(home))
            {
                var candidate = Path.Combine(root, sessionId, ".system_generated", "logs", "transcript.jsonl");
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
        }

        path ??= AntigravityTranscriptFiles(home)
            .FirstOrDefault(x => x.SessionId.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
            .Path;

        if (path is null)
            throw new AgoraException($"no antigravity session '{sessionId}' found");

        var turns = new List<ChatTurn>();
        foreach (var line in ReadLinesShared(path))
        {
            using var doc = Parse(line);
            if (doc is null) continue;
            var root = doc.RootElement;
            var type = Str(root.GetPropertyOrDefault("type"));
            if (type == "USER_INPUT")
            {
                var text = ExtractAntigravityUserText(Str(root.GetPropertyOrDefault("content")));
                if (text.Length > 0)
                    turns.Add(new ChatTurn("user", text, Str(root.GetPropertyOrDefault("created_at"))));
            }
            else if (type == "PLANNER_RESPONSE")
            {
                var text = (Str(root.GetPropertyOrDefault("content")) ?? "").Trim();
                if (text.Length > 0)
                    turns.Add(new ChatTurn("assistant", text, Str(root.GetPropertyOrDefault("created_at"))));
            }
        }
        return turns;
    }

    private static string ExtractAntigravityUserText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        const string open = "<USER_REQUEST>";
        const string close = "</USER_REQUEST>";
        var start = raw.IndexOf(open, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += open.Length;
            var end = raw.IndexOf(close, start, StringComparison.Ordinal);
            return end >= start ? raw[start..end].Trim() : raw[start..].Trim();
        }
        var trimmed = raw.Trim();
        // Compaction context summaries with no <USER_REQUEST> are internal handoffs, not user prompts.
        if (trimmed.StartsWith("<CONTEXT_SUMMARY>", StringComparison.Ordinal))
            return "";
        return trimmed;
    }
}
