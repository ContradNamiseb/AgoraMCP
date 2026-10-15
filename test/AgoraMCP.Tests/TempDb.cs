using AgoraMCP.Database;
using Microsoft.Data.Sqlite;

namespace AgoraMCP.Tests;

/// <summary>
/// A throwaway database per test, in its OWN temp subdirectory: the watch
/// registry lives next to the db file, so tests sharing %TEMP% directly would
/// pollute each other's registries.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), $"agora-test-{Guid.NewGuid():N}");

    public string DbPath { get; } = default!;

    public TempDb()
    {
        Directory.CreateDirectory(DirectoryPath);
        DbPath = Path.Combine(DirectoryPath, "agora.db");
        using var db = AgoraDatabase.Open(DbPath);
    }

    public SqliteConnection Open() => AgoraDatabase.Open(DbPath);

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); } catch (IOException) { }
    }
}
