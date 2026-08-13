using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore :
    IPrincipalRepository,
    IEvidenceRepository,
    IEventRepository,
    IAssertionRepository,
    IActionRepository,
    IActionAuthorizationRepository,
    IActionExecutionRepository,
    IWorkflowRepository,
    IKernelObservationRepository,
    IFollowUpRepository,
    ICapabilityAvailability,
    IDurableExecutionRequestRepository,
    IRealtimeVoiceRepository
{
    private const string TimestampFormat = "O";
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly long? _maxDatabaseBytes;

    internal Func<CancellationToken, Task>? RemoteHostBeforeCommitAsync { get; set; }

    public SqliteKernelStore(string databasePath, long? maxDatabaseBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (maxDatabaseBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDatabaseBytes));
        _databasePath = Path.GetFullPath(databasePath);
        _maxDatabaseBytes = maxDatabaseBytes;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => InitializeAsync(KernelMigrations.LatestVersion, cancellationToken);

    internal async Task InitializeAsync(
        int targetVersion,
        CancellationToken cancellationToken = default)
    {
        if (targetVersion < 1 || targetVersion > KernelMigrations.LatestVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var applied = await GetAppliedMigrationVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var migration in KernelMigrations.All.Where(item => item.Version <= targetVersion))
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $appliedAt);";
                command.Parameters.AddWithValue("$version", migration.Version);
                command.Parameters.AddWithValue("$appliedAt", FormatTimestamp(DateTimeOffset.UtcNow));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<int>> GetAppliedMigrationVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetAppliedMigrationVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SqliteConnectionSettings> GetConnectionSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadPragmaAsync(connection, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false);
        var journalMode = await ReadPragmaAsync(connection, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
        var busyTimeout = await ReadPragmaAsync(connection, "PRAGMA busy_timeout;", cancellationToken).ConfigureAwait(false);
        var pageSize = await ReadPragmaAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        var maxPageCount = await ReadPragmaAsync(connection, "PRAGMA max_page_count;", cancellationToken).ConfigureAwait(false);
        return new SqliteConnectionSettings(
            Convert.ToInt32(foreignKeys, CultureInfo.InvariantCulture) == 1,
            Convert.ToString(journalMode, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToInt32(busyTimeout, CultureInfo.InvariantCulture),
            Convert.ToInt64(pageSize, CultureInfo.InvariantCulture),
            Convert.ToInt64(maxPageCount, CultureInfo.InvariantCulture));
    }

    public async Task<ProductRuntimeHealth> GetRuntimeHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection=await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT
              COALESCE((SELECT MAX(version) FROM schema_migrations),0),
              (SELECT COUNT(*) FROM plugin_installations WHERE enabled=1 AND removed=0),
              (SELECT COUNT(*) FROM model_profiles WHERE enabled=1),
              (SELECT COUNT(*) FROM connected_accounts WHERE lifecycle='CONNECTED'),
              (SELECT COUNT(*) FROM connected_accounts WHERE lifecycle='AUTH_REQUIRED'),
              (SELECT COUNT(*) FROM job_runs WHERE state IN ('QUEUED','RUNNING','WAITING_FOR_APPROVAL','RECONCILIATION_REQUIRED')),
              (SELECT COUNT(*) FROM job_runs WHERE state='FAILED');
            """;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))throw new InvalidDataException("Product health query returned no row.");
        return new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6));
    }

    public Task BackupAsync(string destinationPath,CancellationToken cancellationToken=default)
        =>CreateVerifiedCopyAsync(_databasePath,destinationPath,cancellationToken);

    public static async Task RestoreBackupAsync(string backupPath,string destinationPath,CancellationToken cancellationToken=default)
    {
        var verification=await VerifyBackupAsync(backupPath,cancellationToken).ConfigureAwait(false);
        if(!verification.IntegrityOk)throw new InvalidDataException("Backup failed SQLite integrity verification.");
        await CreateVerifiedCopyAsync(backupPath,destinationPath,cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SqliteBackupVerification> VerifyBackupAsync(string databasePath,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path=Path.GetFullPath(databasePath);
        if(!File.Exists(path))throw new FileNotFoundException("SQLite backup does not exist.",path);
        var connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString();
        await using var connection=new SqliteConnection(connectionString);await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command=connection.CreateCommand();command.CommandText="""
            SELECT
              (SELECT quick_check FROM pragma_quick_check LIMIT 1),
              COALESCE((SELECT MAX(version) FROM schema_migrations),0),
              (SELECT COUNT(*) FROM conversations),
              (SELECT COUNT(*) FROM assertions),
              (SELECT COUNT(*) FROM jobs),
              (SELECT COUNT(*) FROM connected_accounts),
              (SELECT COUNT(*) FROM actions);
            """;
        await using var reader=await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))throw new InvalidDataException("Backup verification returned no row.");
        return new(string.Equals(reader.GetString(0),"ok",StringComparison.OrdinalIgnoreCase),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6));
    }

    private static async Task CreateVerifiedCopyAsync(string sourcePath,string destinationPath,CancellationToken token)
    {
        var source=Path.GetFullPath(sourcePath);var destination=Path.GetFullPath(destinationPath);
        if(string.Equals(source,destination,StringComparison.Ordinal))throw new ArgumentException("Backup source and destination must differ.",nameof(destinationPath));
        if(File.Exists(destination))throw new IOException("Backup destination already exists; overwrite is not supported.");
        var directory=Path.GetDirectoryName(destination)??throw new InvalidOperationException("Backup destination has no parent directory.");Directory.CreateDirectory(directory);
        var temporary=Path.Combine(directory,$".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await CopyDatabaseAsync(source,temporary,token).ConfigureAwait(false);
            var verification=await VerifyBackupAsync(temporary,token).ConfigureAwait(false);
            if(!verification.IntegrityOk)throw new InvalidDataException("Created SQLite image failed integrity verification.");
            File.Move(temporary,destination);
        }
        finally
        {
            DeleteIfExists(temporary);DeleteIfExists($"{temporary}-wal");DeleteIfExists($"{temporary}-shm");
        }
    }

    private static async Task CopyDatabaseAsync(string source,string destination,CancellationToken token)
    {
        var sourceConnectionString=new SqliteConnectionStringBuilder{DataSource=source,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString();
        var destinationConnectionString=new SqliteConnectionStringBuilder{DataSource=destination,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString();
        await using var sourceConnection=new SqliteConnection(sourceConnectionString);await sourceConnection.OpenAsync(token).ConfigureAwait(false);
        await using var destinationConnection=new SqliteConnection(destinationConnectionString);await destinationConnection.OpenAsync(token).ConfigureAwait(false);
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private static void DeleteIfExists(string path){if(File.Exists(path))File.Delete(path);}

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (_maxDatabaseBytes is { } maxDatabaseBytes)
            {
                var pageSize = Convert.ToInt64(
                    await ReadPragmaAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                var requestedPageCount = maxDatabaseBytes / pageSize;
                if (requestedPageCount <= 0)
                    throw new InvalidOperationException("Configured SQLite size limit is smaller than one database page.");
                command.CommandText = $"PRAGMA max_page_count={requestedPageCount.ToString(CultureInfo.InvariantCulture)};";
                var effectivePageCount = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (effectivePageCount > requestedPageCount)
                    throw new InvalidOperationException("Existing SQLite database exceeds the configured size limit.");
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<int>> GetAppliedMigrationVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions.AsReadOnly();
    }

    private static async Task<object?> ReadPragmaAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureOwner(string ownerPrincipalId, string recordOwnerPrincipalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        if (!string.Equals(ownerPrincipalId, recordOwnerPrincipalId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Record owner does not match the required principal scope.");
        }
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.ParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private static ReadOnlyCollection<string> DeserializeReferences(string value)
        => Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(value) ?? []);

    private static Dictionary<string, string> DeserializeAttributes(string value)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(value)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed record SqliteConnectionSettings(
    bool ForeignKeysEnabled,
    string JournalMode,
    int BusyTimeoutMilliseconds,
    long PageSizeBytes,
    long MaxPageCount)
{
    public long MaxDatabaseBytes => checked(PageSizeBytes * MaxPageCount);
}

public sealed record ProductRuntimeHealth(int SchemaVersion,int EnabledPlugins,int EnabledModelProfiles,int ConnectedAccounts,int AuthRequiredAccounts,int PendingRuns,int FailedRuns);
public sealed record SqliteBackupVerification(bool IntegrityOk,int SchemaVersion,int Conversations,int Assertions,int Jobs,int Accounts,int Actions);