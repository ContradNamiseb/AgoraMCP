using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgoraMCP.McpServer.Watch;

/// <summary>
/// The cross-process wake signal behind `agora watch`: each watcher owns ONE
/// auto-reset event, named per db-path + agent, and writers BROADCAST to every
/// registered watcher's event.
///
/// <para>Why per-watcher events instead of one shared manual-reset event: a
/// shared manual-reset with independently resetting consumers cannot broadcast
/// reliably — watcher A can wake and Reset before watcher B samples, and B
/// misses the post (caught in review, thread #5 #30). With one auto-reset event
/// per watcher that only its owner waits on and only the broadcast resets,
/// every post wakes every watcher by construction.</para>
///
/// <para>Registration is one file PER WATCHER (watch-events/<agent>.event next
/// to the database), not a shared merged file: a read-merge-write registry
/// loses entries when two watchers register concurrently (last write wins —
/// caught live in the two-watcher e2e). Each watcher writes ONLY its own file;
/// broadcast enumerates the directory. Stale entries from dead watchers are
/// harmless: OpenExisting fails, the broadcast skips them, the dead watcher's
/// coarse wait catches it up when it returns. Writers enumerate best-effort —
/// a failed signal never fails the write. Windows-only by nature; a no-op
/// elsewhere.</para>
/// </summary>
public static class NamedBoardEvent
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private static string HashFor(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..16];
    }

    public static string EventsDirectoryFor(string dbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".",
            "watch-events");

    /// <summary>The watcher's own event: auto-reset, owned solely by this agent. The agent component is HASHED, not slugged — "a-b" and "a_b" slugs would collide into one shared event and resurrect the fan-out starvation (codex-sol, thread #5 review).</summary>
    public static string EventNameFor(string dbPath, string agent)
    {
        return $@"Local\agoramcp-{HashFor(dbPath)}-{AgentHash(agent)}";
    }

    private static string AgentHash(string agent)
    {
        var normalized = agent.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
    }

    public static EventWaitHandle? OpenOwnEvent(string dbPath, string agent)
    {
        if (!IsSupported) return null;
        return new EventWaitHandle(false, EventResetMode.AutoReset, EventNameFor(dbPath, agent), out _);
    }

    /// <summary>Watcher side: write this agent's event file (its own file — no merge, no lost updates). Never throws.</summary>
    public static void Register(string dbPath, string agent)
    {
        if (!IsSupported) return;
        try
        {
            var file = EventFileFor(dbPath, agent);
            Directory.CreateDirectory(EventsDirectoryFor(dbPath));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, EventNameFor(dbPath, agent));
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException)
        {
            // A failed registration costs latency (writers skip us until the
            // next successful registration), never correctness.
        }
    }

    /// <summary>Watcher side: remove this listener's event file on clean exit. Never throws.</summary>
    public static void Unregister(string dbPath, string agent)
    {
        if (!IsSupported) return;
        try
        {
            var file = EventFileFor(dbPath, agent);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Per-agent event file name: readable slug plus the agent hash, so "a-b" and "a_b" get distinct files.</summary>
    public static string EventFileFor(string dbPath, string agent)
    {
        var slug = new string(agent.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(EventsDirectoryFor(dbPath), $"{slug}-{AgentHash(agent)}.event");
    }

    /// <summary>Writer side: broadcast to every registered watcher event. Best-effort — never throws.</summary>
    public static void Broadcast(string dbPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var dir = EventsDirectoryFor(dbPath);
            if (!Directory.Exists(dir)) return;
            var staleCutoff = DateTime.UtcNow.AddMinutes(-2);
            foreach (var file in Directory.EnumerateFiles(dir, "*.event"))
            {
                try
                {
                    var name = File.ReadAllText(file).Trim();
                    if (name.Length == 0) continue;
                    using var handle = EventWaitHandle.OpenExisting(name);
                    handle.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // Listener is not up right now. Prune orphaned ephemeral tail/channel/bridge
                    // event files so crashed sessions do not accumulate files forever.
                    var leaf = Path.GetFileName(file);
                    if (leaf.StartsWith("tail-", StringComparison.OrdinalIgnoreCase) ||
                        leaf.StartsWith("channel-", StringComparison.OrdinalIgnoreCase) ||
                        leaf.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < staleCutoff)
                                File.Delete(file);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    // Unreadable file: skip it; listener's coarse wait catches up.
                }
            }
        }
        catch (IOException)
        {
            // Events directory unreadable: watchers fall back to their coarse timeout.
        }
    }
}
