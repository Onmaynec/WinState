using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinState.Infrastructure.Configuration;

namespace WinState.Storage;

public sealed record StorageStatus(
    string DatabasePath,
    int AppliedMigrations,
    int LatestMigrationVersion,
    long DatabaseSizeBytes);

internal sealed record DatabaseMigration(int Version, string Name, string Sql);

/// <summary>Локальная SQLite-база состояния WinState с идемпотентными миграциями.</summary>
public sealed class SqliteStateStore
{
    private readonly WinStateOptions _options;
    private readonly ILogger<SqliteStateStore> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public SqliteStateStore(WinStateOptions options, ILogger<SqliteStateStore>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<SqliteStateStore>.Instance;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectories();
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureMigrationHistoryAsync(connection, cancellationToken);
            var applied = await ReadAppliedVersionsAsync(connection, cancellationToken);

            foreach (var migration in Migrations.All.Where(item => !applied.Contains(item.Version)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyMigration(connection, migration, cancellationToken);
                _logger.LogInformation("Применена миграция SQLite {Version}: {Name}", migration.Version, migration.Name);
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<StorageStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureMigrationHistoryAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(Version), 0) FROM MigrationHistory;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);

        var size = File.Exists(_options.DatabasePath) ? new FileInfo(_options.DatabasePath).Length : 0L;
        return new StorageStatus(
            _options.DatabasePath,
            reader.GetInt32(0),
            reader.GetInt32(1),
            size);
    }

    public async Task<IReadOnlyCollection<string>> ListTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_options.HomeDirectory);
        Directory.CreateDirectory(_options.ProfilesDirectory);
        Directory.CreateDirectory(_options.LogsDirectory);
        var databaseDirectory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private static async Task EnsureMigrationHistoryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MigrationHistory (
                Version INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM MigrationHistory;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void ApplyMigration(
        SqliteConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        using var schemaCommand = connection.CreateCommand();
        schemaCommand.Transaction = transaction;
        schemaCommand.CommandText = migration.Sql;
        _ = schemaCommand.ExecuteNonQuery();

        using var historyCommand = connection.CreateCommand();
        historyCommand.Transaction = transaction;
        historyCommand.CommandText = """
            INSERT INTO MigrationHistory (Version, Name, AppliedAtUtc)
            VALUES ($version, $name, $appliedAtUtc);
            """;
        historyCommand.Parameters.AddWithValue("$version", migration.Version);
        historyCommand.Parameters.AddWithValue("$name", migration.Name);
        historyCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        _ = historyCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    private static class Migrations
    {
        public static IReadOnlyCollection<DatabaseMigration> All { get; } =
        [
            new(1, "initial-state-store", """
                CREATE TABLE IF NOT EXISTS Profiles (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Path TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ProfileVersions (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ProfileId TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    ContentHash TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE,
                    UNIQUE (ProfileId, Version)
                );
                CREATE TABLE IF NOT EXISTS ManagedResources (
                    ResourceId TEXT NOT NULL PRIMARY KEY,
                    ProfileId TEXT NOT NULL,
                    ProviderId TEXT NOT NULL,
                    LastAppliedTransaction TEXT,
                    MetadataJson TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS CurrentBaselines (
                    ResourceId TEXT NOT NULL PRIMARY KEY,
                    ProfileId TEXT NOT NULL,
                    StateJson TEXT NOT NULL,
                    CapturedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Transactions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ProfileId TEXT NOT NULL,
                    StartedAtUtc TEXT NOT NULL,
                    CompletedAtUtc TEXT,
                    Status TEXT NOT NULL,
                    Mode TEXT NOT NULL,
                    RebootRequired INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS TransactionActions (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TransactionId TEXT NOT NULL,
                    ActionId TEXT NOT NULL,
                    ProviderId TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    ResultJson TEXT,
                    FOREIGN KEY (TransactionId) REFERENCES Transactions(Id) ON DELETE CASCADE,
                    UNIQUE (TransactionId, ActionId)
                );
                CREATE TABLE IF NOT EXISTS ActionBackups (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TransactionId TEXT NOT NULL,
                    ActionId TEXT NOT NULL,
                    BackupReference TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (TransactionId) REFERENCES Transactions(Id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS ProviderStates (
                    ProviderId TEXT NOT NULL PRIMARY KEY,
                    StateJson TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS DriftResults (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ProfileId TEXT NOT NULL,
                    ResourceId TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    DetailsJson TEXT,
                    DetectedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ApplicationSettings (
                    Key TEXT NOT NULL PRIMARY KEY,
                    Value TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """)
        ];
    }
}
