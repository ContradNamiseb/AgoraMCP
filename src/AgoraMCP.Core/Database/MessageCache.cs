using System.Text;
using AgoraMCP.Models;

namespace AgoraMCP.Database;

/// <summary>
/// The in-memory chat box in front of the database.
///
/// <para>Every message is written through to SQLite as it happens — the database
/// is the blackboard between separate agent processes, and only it makes a
/// review visible to the poster's process immediately. On top of that, this
/// cache keeps message BODIES resident so reads hit memory first and fall back
/// to the database when a message was compacted out. Messages are immutable,
/// so a cached body can never go stale — the only thing that may never be
/// cached is liveness (presence) and the inbox (pending queries), which stay
/// SQLite-only by design.</para>
///
/// <para>Compaction: a byte budget (default 1 GiB, AGORA_CACHE_BYTES to change
/// or 0 to disable). When the budget is exceeded, the oldest entries leave
/// memory first; they are already in the database, so nothing is lost — recall
/// just re-reads them from SQLite and re-warms the cache.</para>
/// </summary>
public sealed class MessageCache
{
    /// <summary>1 GiB of message bodies is on the order of a million messages.</summary>
    public const long DefaultBudgetBytes = 1L << 30;

    public const string BudgetEnvVar = "AGORA_CACHE_BYTES";

    /// <summary>Per-entry overhead (dictionary, list, object headers) charged against the budget.</summary>
    private const long EntryOverheadBytes = 96;

    private readonly object _gate = new();
    private readonly Dictionary<long, ThreadMessage> _messages = [];
    private readonly Queue<long> _insertionOrder = new();
    private long _bytes;

    public MessageCache(long budgetBytes = DefaultBudgetBytes)
    {
        if (budgetBytes < 0) budgetBytes = 0;
        BudgetBytes = budgetBytes;
    }

    /// <summary>Memory budget for message bodies; 0 disables caching entirely.</summary>
    public long BudgetBytes { get; }

    /// <summary>Resolve the budget: explicit value, then AGORA_CACHE_BYTES, then 1 GiB.</summary>
    public static long ResolveBudget(long? explicitBytes = null)
    {
        if (explicitBytes is { } e) return Math.Max(0, e);
        var raw = Environment.GetEnvironmentVariable(BudgetEnvVar);
        return long.TryParse(raw, out var parsed) ? Math.Max(0, parsed) : DefaultBudgetBytes;
    }

    public (int Messages, long Bytes) Usage
    {
        get
        {
            lock (_gate) return (_messages.Count, _bytes);
        }
    }

    /// <summary>
    /// Put a message in memory — called on every write (write-through) and on
    /// every database re-read (re-warm). When the budget is hit, the oldest
    /// entries are compacted out first; a single message larger than the whole
    /// budget is never cached.
    /// </summary>
    public void Remember(ThreadMessage message)
    {
        var size = Encoding.UTF8.GetByteCount(message.Body) + EntryOverheadBytes;
        lock (_gate)
        {
            if (BudgetBytes == 0 || size > BudgetBytes) return;

            // Same id arriving again (a re-read of an already-cached message):
            // keep the newest entry and its position out of the eviction queue.
            if (_messages.TryGetValue(message.Id, out var existing)) _bytes -= SizeOf(existing);
            else _insertionOrder.Enqueue(message.Id);

            _messages[message.Id] = message;
            _bytes += size;

            while (_bytes > BudgetBytes && _messages.Count > 0)
            {
                var oldest = _insertionOrder.Dequeue();
                if (_messages.Remove(oldest, out var evicted)) _bytes -= SizeOf(evicted);
            }
        }
    }

    /// <summary>Fast path: the chat box has it. False means recall from the database.</summary>
    public bool TryGet(long id, out ThreadMessage? message)
    {
        lock (_gate)
        {
            if (_messages.TryGetValue(id, out var m))
            {
                message = m;
                return true;
            }
        }
        message = null;
        return false;
    }

    private long SizeOf(ThreadMessage m) => Encoding.UTF8.GetByteCount(m.Body) + EntryOverheadBytes;
}
