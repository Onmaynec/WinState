using Microsoft.Data.Sqlite;
using WinState.Infrastructure.Configuration;

namespace WinState.Storage;

public sealed record StoredTransactionAction(
    string ActionId,
    string ProviderId,
    string Status,
    string? ResultJson,
    string? BackupReference);

public sealed record StoredTransaction(
    string Id,
    string ProfileId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Status,
    string Mode,
    bool RebootRequired,
    IReadOnlyCollection<StoredTransactionAction> Actions);

/// <summary>Сохраняет execution history в подготовленные таблицы SQLite.</summary>
public sealed class TransactionHistoryStore
{
    private readonly WinStateOptions _options;
    private readonly SqliteStateStore _stateStore;

    public TransactionHistoryStore(WinStateOptions options, SqliteStateStore stateStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task RecordAsync(StoredTransaction record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _stateStore.InitializeAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO Transactions
                    (Id, ProfileId, StartedAtUtc, CompletedAtUtc, Status, Mode, RebootRequired)
                VALUES
                    ($id, $profileId, $startedAt, $completedAt, $status, $mode, $rebootRequired);
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$profileId", record.ProfileId);
            command.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completedAt", record.CompletedAt.ToString("O"));
            command.Parameters.AddWithValue("$status", record.Status);
            command.Parameters.AddWithValue("$mode", record.Mode);
            command.Parameters.AddWithValue("$rebootRequired", record.RebootRequired ? 1 : 0);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var action in record.Actions)
        {
            await using (var actionCommand = connection.CreateCommand())
            {
                actionCommand.Transaction = transaction;
                actionCommand.CommandText = """
                    INSERT OR REPLACE INTO TransactionActions
                        (TransactionId, ActionId, ProviderId, Status, ResultJson)
                    VALUES
                        ($transactionId, $actionId, $providerId, $status, $resultJson);
                    """;
                actionCommand.Parameters.AddWithValue("$transactionId", record.Id);
                actionCommand.Parameters.AddWithValue("$actionId", action.ActionId);
                actionCommand.Parameters.AddWithValue("$providerId", action.ProviderId);
                actionCommand.Parameters.AddWithValue("$status", action.Status);
                actionCommand.Parameters.AddWithValue(
                    "$resultJson",
                    action.ResultJson is null ? DBNull.Value : action.ResultJson);
                _ = await actionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(action.BackupReference))
            {
                continue;
            }

            await using var backupCommand = connection.CreateCommand();
            backupCommand.Transaction = transaction;
            backupCommand.CommandText = """
                INSERT INTO ActionBackups
                    (TransactionId, ActionId, BackupReference, CreatedAtUtc)
                VALUES
                    ($transactionId, $actionId, $backupReference, $createdAt);
                """;
            backupCommand.Parameters.AddWithValue("$transactionId", record.Id);
            backupCommand.Parameters.AddWithValue("$actionId", action.ActionId);
            backupCommand.Parameters.AddWithValue("$backupReference", action.BackupReference);
            backupCommand.Parameters.AddWithValue("$createdAt", record.CompletedAt.ToString("O"));
            _ = await backupCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
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
}
