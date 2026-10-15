using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Database;

/// <summary>Raised when the environment or the caller's arguments cannot support the operation.</summary>
public sealed class AgoraException(string message) : Exception(message);

/// <summary>
/// Opening the database and applying the schema.
///
/// The hub is the database file, not a daemon: one agora process per agent, all
/// pointed at the same file. WAL lets every process read concurrently; the busy
/// timeout makes concurrent writers queue for a few milliseconds instead of
/// failing with SQLITE_BUSY.
/// </summary>
public static class AgoraDatabase
{
    /// <summary>
    /// Open (creating if needed) the agora database with the schema applied.
    /// </summary>
    public static SqliteConnection Open(string dbPath)
    {
        RequireFts5();

        if (dbPath != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling off: a pooled connection can outlive Close() and keep the
            // -wal file locked, which breaks test cleanup on Windows. Opening a
            // local file is sub-millisecond, so per-call opens cost nothing
            // against LLM thinking time.
            Pooling = false,
        }.ToString());

        try
        {
            db.Open();
        }
        catch (SqliteException e)
        {
            // SQLite reports only "unable to open database file", which says
            // nothing about which file it tried or why the path was chosen.
            db.Dispose();
            throw new AgoraException(
                $"could not open the agora database at '{Path.GetFullPath(dbPath)}' ({e.Message}). " +
                "Pass --db to choose a path, or set AGORA_DB. Every agent that wants to share the " +
                "blackboard must point at the SAME file.");
        }

        // An in-memory database has no journal to switch and no other writers.
        if (dbPath != ":memory:") db.Execute("PRAGMA journal_mode = WAL");
        db.Execute("PRAGMA busy_timeout = 5000");
        db.Execute("PRAGMA foreign_keys = ON");
        db.Execute("PRAGMA synchronous = NORMAL");
        db.Execute(SchemaSql);

        // Schema.sql only ever creates what is missing, so an existing database
        // would otherwise stay on whatever shape it was first created with.
        Migrations.Apply(db);
        return db;
    }

    /// <summary>The schema, embedded so a published binary carries it.</summary>
    public static string SchemaSql
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Check the capability, not a version string.
    ///
    /// Microsoft.Data.Sqlite normally bundles a native SQLite with FTS5 compiled
    /// in, but a host that supplies its own sqlite3 library may not have it. In
    /// that case the connection opens happily and only the virtual table fails.
    /// </summary>
    public static void RequireFts5()
    {
        try
        {
            using var probe = new SqliteConnection("Data Source=:memory:");
            probe.Open();
            probe.Execute("CREATE VIRTUAL TABLE probe_fts USING fts5(body)");
            probe.Execute("INSERT INTO probe_fts(body) VALUES ('probe')");
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT bm25(probe_fts) FROM probe_fts WHERE probe_fts MATCH 'probe'";
            cmd.ExecuteScalar();
        }
        catch (Exception e)
        {
            throw new AgoraException(
                "agora needs SQLite with FTS5 and bm25, which this runtime could not provide " +
                $"({e.Message}). Microsoft.Data.Sqlite ships a bundled SQLite that has FTS5 enabled; " +
                "check that no other SQLitePCLRaw provider is overriding it.");
        }
    }

    /// <summary>UTC now in the same ISO-8601 shape the schema defaults produce.</summary>
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
