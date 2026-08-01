using Microsoft.Extensions.Logging;
using WinState.Infrastructure.Configuration;
using WinState.Storage;
using Xunit;

namespace WinState.Storage.Tests;

public sealed class SqliteStateStoreTests
{
    [Fact]
    public async Task Initialize_is_idempotent_and_creates_expected_schema()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-storage-{Guid.NewGuid():N}");
        try
        {
            var options = new WinStateOptions
            {
                HomeDirectory = root,
                ProfilesDirectory = Path.Combine(root, "profiles"),
                DatabasePath = Path.Combine(root, "state", "winstate.db"),
                LogsDirectory = Path.Combine(root, "logs"),
                ConfigPath = Path.Combine(root, "winstate.json"),
                MinimumLogLevel = LogLevel.Information
            };
            var storage = new SqliteStateStore(options);

            await storage.InitializeAsync(CancellationToken.None);
            await storage.InitializeAsync(CancellationToken.None);
            var status = await storage.GetStatusAsync(CancellationToken.None);
            var tables = await storage.ListTablesAsync(CancellationToken.None);

            Assert.Equal(1, status.AppliedMigrations);
            Assert.Equal(1, status.LatestMigrationVersion);
            Assert.Contains("Profiles", tables);
            Assert.Contains("Transactions", tables);
            Assert.Contains("ManagedResources", tables);
            Assert.Contains("MigrationHistory", tables);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
