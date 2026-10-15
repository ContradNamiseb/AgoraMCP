using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgoraMCP.McpServer.Watch;

/// <summary>
/// One thread's read cursor, plan-v1 "state v2": ids, not counts. A count
/// cannot tell "the peer posted" from "I posted" and goes stale silently; ids
/// make dedupe exact and a stale file self-heal — respond only to peer-authored
/// message ids above <see cref="LastSeenMsgId"/>, and a corrupted file simply
/// re-derives once from the board.
/// </summary>
public sealed record ThreadCursor(
    [property: JsonPropertyName("last_seen_msg_id")] long LastSeenMsgId,
    [property: JsonPropertyName("last_peer_msg_id")] long LastPeerMsgId,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// The shared watch state file (one per agent identity). Owned by the spawned
/// turn (the agent that responds updates it); `agora watch` only reads it to
/// decide whether a peer delta exists. Board is truth, file is cache.
///
/// <para>Serialized exactly as documented: {"threads": {"<id>":
/// {"last_seen_msg_id": N, "last_peer_msg_id": N, "status": "..."}}}. A
/// spawned agent that writes the documented snake_case schema round-trips.
/// Legacy PascalCase files (from the first build) load and are migrated on the
/// next save; pre-v2 shapes (v1 counts, no "threads" key) re-derive once.</para>
/// </summary>
public sealed class WatchState
{
    public const string DocumentedSchema = """
        {
          "threads": {
            "1": { "last_seen_msg_id": 4, "last_peer_msg_id": 3, "status": "answered" }
          }
        }
        """;

    [JsonPropertyName("threads")]
    public Dictionary<string, ThreadCursor> Threads { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What Load found on disk. Absent and Unusable have opposite ack semantics (thread #23 #259): an absent file has nothing to protect and seeds cleanly; an unusable one must be refused.</summary>
    public enum WatchFileKind { Current, Absent, Unusable }

    /// <summary>Load, or re-derive. Accepts the documented snake_case schema and legacy PascalCase; a missing, corrupt, or pre-v2 file (v1 counts, no "threads") yields FreshBaseline.</summary>
    public static WatchState Load(string path, out bool freshBaseline)
    {
        var state = LoadWithKind(path, out var kind);
        freshBaseline = kind != WatchFileKind.Current;
        return state;
    }

    /// <summary>Load reporting WHAT was found: the current file, an absent one, or an unusable (corrupt / legacy / id-less) one. Distinct name, not an overload: out-var call sites cannot disambiguate out types.</summary>
    public static WatchState LoadWithKind(string path, out WatchFileKind kind)
    {
        kind = WatchFileKind.Current;
        try
        {
            if (!File.Exists(path))
            {
                kind = WatchFileKind.Absent;
                return new WatchState();
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                kind = WatchFileKind.Unusable;
                return new WatchState();
            }

            if (root.TryGetProperty("threads", out var canonical))
            {
                var state = CanonicalState(canonical) ?? new WatchState();
                if (state.Threads.Count > 0 && state.Threads.Values.All(c => c.LastSeenMsgId == 0))
                {
                    kind = WatchFileKind.Unusable; // id-less cursors are unusable for id-based dedupe
                }
                return state;
            }

            if (root.TryGetProperty("Threads", out var legacy))
            {
                // First-build PascalCase files: same content, different property
                // names — load through the legacy shape, migrate on next save.
                var state = new WatchState();
                if (legacy.ValueKind == JsonValueKind.Object)
                {
                    foreach (var thread in legacy.EnumerateObject())
                    {
                        long seen = 0, peer = 0;
                        var status = "";
                        if (thread.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var field in thread.Value.EnumerateObject())
                            {
                                switch (field.Name)
                                {
                                    case "LastSeenMsgId" when field.Value.ValueKind == JsonValueKind.Number:
                                        seen = field.Value.GetInt64();
                                        break;
                                    case "LastPeerMsgId" when field.Value.ValueKind == JsonValueKind.Number:
                                        peer = field.Value.GetInt64();
                                        break;
                                    case "Status" when field.Value.ValueKind == JsonValueKind.String:
                                        status = field.Value.GetString() ?? "";
                                        break;
                                }
                            }
                        }
                        state.Threads[thread.Name] = new ThreadCursor(seen, peer, status);
                    }
                }
                if (state.Threads.Count > 0 && state.Threads.Values.All(c => c.LastSeenMsgId == 0))
                {
                    kind = WatchFileKind.Unusable; // id-less cursors are unusable for id-based dedupe
                }
                return state;
            }

            // No "threads"/"Threads" key: pre-v2 (v1 status/count files are
            // top-level {"<id>": {...}}) or foreign — re-derive from the board once.
            kind = WatchFileKind.Unusable;
            return new WatchState();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Corrupt: keep the bad file aside for forensics, start clean. The
            // first diff after this seeds cursors from the board — no spawn.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (IOException) { }
            kind = WatchFileKind.Unusable;
            return new WatchState();
        }
    }

    private static WatchState? CanonicalState(JsonElement threadsElement)
    {
        var state = new WatchState();
        if (threadsElement.ValueKind != JsonValueKind.Object) return state;
        foreach (var thread in threadsElement.EnumerateObject())
        {
            long seen = 0, peer = 0;
            var status = "";
            if (thread.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in thread.Value.EnumerateObject())
                {
                    switch (field.Name)
                    {
                        case "last_seen_msg_id" when field.Value.ValueKind == JsonValueKind.Number:
                            seen = field.Value.GetInt64();
                            break;
                        case "last_peer_msg_id" when field.Value.ValueKind == JsonValueKind.Number:
                            peer = field.Value.GetInt64();
                            break;
                        case "status" when field.Value.ValueKind == JsonValueKind.String:
                            status = field.Value.GetString() ?? "";
                            break;
                    }
                }
            }
            state.Threads[thread.Name] = new ThreadCursor(seen, peer, status);
        }
        return state;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}
