namespace Tessera.Persistence.Sqlite.Tests;

internal sealed class TemporaryDatabase : IDisposable
{
    public TemporaryDatabase()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tessera-kernel-tests");
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    public string Path { get; }

    public SqliteKernelStore CreateStore() => new(Path);

    public void Dispose()
    {
        DeleteIfExists(Path);
        DeleteIfExists($"{Path}-wal");
        DeleteIfExists($"{Path}-shm");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}