using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>
/// Forward-only schema migrations, keyed by PRAGMA user_version. Schema.sql
/// always runs first and handles fresh databases; migrations here reshape
/// databases created by an older build. Empty for now — the machinery exists so
/// the first breaking change lands as a numbered step, not as an ad-hoc hack.
/// </summary>
public static class Migrations
{
    // Step N runs when the database's user_version is exactly N-1, then the
    // version is bumped. Append only; never edit or reorder existing steps.
    private static readonly (int Version, string Description, string Sql)[] Steps = [];

    public static void Apply(SqliteConnection db)
    {
        EnsureTurnColumns(db);
        EnsureThreadsKindColumn(db);

        var current = db.Scalar<long>("PRAGMA user_version");
        foreach (var (version, description, sql) in Steps)
        {
            if (current < version)
            {
                if (!string.IsNullOrWhiteSpace(sql)) db.Execute(sql);
                db.Execute($"PRAGMA user_version = {version}");
            }
        }

        // Mark freshly created databases as current even with no steps yet.
        if (db.Scalar<long>("PRAGMA user_version") < 1)
            db.Execute("PRAGMA user_version = 1");
    }

    // threads.kind ('thread' | 'dm') arrived with the DM layer. Schema.sql
    // creates it on fresh databases; this reshapes older ones in place. A
    // numbered step cannot express it: it would double-add on fresh databases,
    // where Schema.sql has already created the column.
    private static void EnsureThreadsKindColumn(SqliteConnection db)
    {
        var cols = db.Prepare("PRAGMA table_info(threads)")
            .Query(r => r.GetString("name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cols.Count > 0 && !cols.Contains("kind"))
            db.Execute("ALTER TABLE threads ADD COLUMN kind TEXT NOT NULL DEFAULT 'thread'");
    }

    private static void EnsureTurnColumns(SqliteConnection db)
    {
        var existingCols = db.Prepare("PRAGMA table_info(turn_events)")
            .Query(r => r.GetString("name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingCols.Count > 0)
        {
            if (!existingCols.Contains("from_role"))
                db.Execute("ALTER TABLE turn_events ADD COLUMN from_role TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("to_role"))
                db.Execute("ALTER TABLE turn_events ADD COLUMN to_role TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("stage"))
                db.Execute("ALTER TABLE turn_events ADD COLUMN stage TEXT NOT NULL DEFAULT ''");
            if (!existingCols.Contains("at_msg_id"))
                db.Execute("ALTER TABLE turn_events ADD COLUMN at_msg_id INTEGER NOT NULL DEFAULT 0");
            if (!existingCols.Contains("msg_id"))
                db.Execute("ALTER TABLE turn_events ADD COLUMN msg_id INTEGER NOT NULL DEFAULT 0");
        }
    }
}
