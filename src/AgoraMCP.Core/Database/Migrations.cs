using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>
/// Forward-only schema migrations, keyed by <c>PRAGMA user_version</c>.
///
/// <para>All schema checks and DDL run inside a single <c>BEGIN IMMEDIATE</c>
/// transaction so concurrent processes first-opening an older database serialize
/// cleanly instead of racing on <c>ALTER TABLE ADD COLUMN</c> (S1). Once
/// <c>PRAGMA user_version</c> reaches <see cref="CurrentVersion"/>, subsequent
/// opens skip DDL completely on the fast path (S3).</para>
/// </summary>
public static class Migrations
{
    public const long CurrentVersion = 2;

    public static void Apply(SqliteConnection db)
    {
        if (db.Scalar<long>("PRAGMA user_version") >= CurrentVersion)
            return;

        // BEGIN IMMEDIATE serializes concurrent first-open migrations across
        // processes (busy_timeout=5000 queues the second opener until commit).
        using var tx = db.BeginTransaction();
        if (db.Scalar<long>("PRAGMA user_version") >= CurrentVersion)
        {
            tx.Commit();
            return;
        }

        // Apply base schema inside the serialized transaction so tables/indexes/triggers
        // exist before column checks and backfills run.
        db.Execute(AgoraDatabase.SchemaSql);

        EnsureTurnColumns(db);
        EnsureThreadsKindColumn(db);

        // S2 + S5 + I9: backfill at_msg_id on pre-migration turn_events rows so
        // as-of routing never falls back to clock-sensitive created_at comparisons,
        // and install the covering indexes + AFTER INSERT trigger for older writers.
        db.Execute("""
            UPDATE turn_events
            SET at_msg_id = COALESCE(
                (SELECT MAX(m.id) FROM messages m WHERE m.created_at <= turn_events.created_at),
                0)
            WHERE at_msg_id = 0;

            CREATE INDEX IF NOT EXISTS idx_turn_events_thread_at_msg ON turn_events(thread_id, at_msg_id, id);
            CREATE INDEX IF NOT EXISTS idx_turn_events_thread_msg ON turn_events(thread_id, msg_id);

            CREATE TRIGGER IF NOT EXISTS trg_turn_events_at_msg_id
            AFTER INSERT ON turn_events
            WHEN NEW.at_msg_id = 0
            BEGIN
              UPDATE turn_events
              SET at_msg_id = COALESCE((SELECT MAX(id) FROM messages), 0)
              WHERE id = NEW.id;
            END;
            """);

        db.Execute($"PRAGMA user_version = {CurrentVersion}");
        tx.Commit();
    }

    private static void EnsureThreadsKindColumn(SqliteConnection db)
    {
        var cols = db.Prepare("PRAGMA table_info(threads)")
            .Query(r => r.GetString("name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cols.Count > 0 && !cols.Contains("kind"))
            TryAddColumn(db, "ALTER TABLE threads ADD COLUMN kind TEXT NOT NULL DEFAULT 'thread'");
    }

    private static void EnsureTurnColumns(SqliteConnection db)
    {
        var existingCols = db.Prepare("PRAGMA table_info(turn_events)")
            .Query(r => r.GetString("name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingCols.Count > 0)
        {
            if (!existingCols.Contains("from_role"))
                TryAddColumn(db, "ALTER TABLE turn_events ADD COLUMN from_role TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("to_role"))
                TryAddColumn(db, "ALTER TABLE turn_events ADD COLUMN to_role TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("stage"))
                TryAddColumn(db, "ALTER TABLE turn_events ADD COLUMN stage TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("at_msg_id"))
                TryAddColumn(db, "ALTER TABLE turn_events ADD COLUMN at_msg_id INTEGER NOT NULL DEFAULT 0");
            if (!existingCols.Contains("msg_id"))
                TryAddColumn(db, "ALTER TABLE turn_events ADD COLUMN msg_id INTEGER NOT NULL DEFAULT 0");
        }
    }

    private static void TryAddColumn(SqliteConnection db, string alterSql)
    {
        try
        {
            db.Execute(alterSql);
        }
        catch (SqliteException e) when (e.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Defensively tolerate already-added columns if an older unversioned binary raced.
        }
    }
}
