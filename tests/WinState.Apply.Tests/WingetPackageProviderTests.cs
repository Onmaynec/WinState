using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Providers;
using WinState.Providers.Packages;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class WingetPackageProviderTests
{
    [Fact]
    public async Task Plan_NewPackage_IsRollbackCapableInstall()
    {
        var client = new FakeWingetClient();
        var provider = new WingetPackageProvider(client);
        var package = new WingetPackageProfile { Id = "Git.Git", Scope = "user" };

        var actions = await provider.PlanAsync(
            new DesiredProviderState([WingetProfileMapper.CreateResource(package)]),
            new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        var action = Assert.Single(actions);
        Assert.Equal(ActionType.Install, action.Operation);
        Assert.Equal(RiskLevel.Low, action.Risk);
        Assert.True(action.SupportsRollback);
        Assert.False(action.RequiresAdministrator);
    }

    [Fact]
    public async Task Plan_Upgrade_IsExplicitlyIrreversible()
    {
        var client = new FakeWingetClient(
            [new WingetInstalledPackage("Git.Git", "2.40.0", "2.50.0", "winget")]);
        var provider = new WingetPackageProvider(client);
        var discovery = await provider.DiscoverAsync(
            new ProviderContext("test", false, Environment.CurrentDirectory),
            CancellationToken.None);
        var package = new WingetPackageProfile { Id = "Git.Git", Version = "latest" };

        var actions = await provider.PlanAsync(
            new DesiredProviderState([WingetProfileMapper.CreateResource(package)]),
            new CurrentProviderState(discovery.Resources),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        var action = Assert.Single(actions);
        Assert.Equal(ActionType.Update, action.Operation);
        Assert.False(action.SupportsRollback);
        Assert.Equal(RiskLevel.Medium, action.Risk);
    }

    [Fact]
    public async Task Install_Checkpoint_Apply_Verify_AndRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-winget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var client = new FakeWingetClient();
            var provider = new WingetPackageProvider(client);
            var resource = WingetProfileMapper.CreateResource(
                new WingetPackageProfile { Id = "Git.Git", Version = "2.50.0" });
            var action = (await provider.PlanAsync(
                new DesiredProviderState([resource]),
                new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
                new PlanningContext(false, false, "test"),
                CancellationToken.None)).Single();
            var context = new ProviderExecutionContext("txn", false, root);

            var checkpoint = await provider.PrepareRollbackAsync(action, context, CancellationToken.None);
            var applied = await provider.ApplyAsync(action, context, CancellationToken.None);
            var verification = await provider.VerifyAsync(action, context, CancellationToken.None);
            var rollback = await provider.RollbackAsync(
                new RollbackAction(action.Id, provider.Id, checkpoint.BackupReference!),
                context,
                CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, applied.Status);
            Assert.True(verification.IsMatch);
            Assert.True(rollback.Succeeded);
            Assert.Null(await client.GetInstalledAsync("Git.Git", "winget", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeWingetClient : IWingetClient
    {
        private readonly Dictionary<string, WingetInstalledPackage> _packages;

        public FakeWingetClient(IReadOnlyCollection<WingetInstalledPackage>? packages = null)
        {
            _packages = (packages ?? Array.Empty<WingetInstalledPackage>())
                .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsSupported => true;

        public Task<IReadOnlyList<WingetInstalledPackage>> ListInstalledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WingetInstalledPackage>>(_packages.Values.ToArray());
        }

        public Task<WingetInstalledPackage?> GetInstalledAsync(
            string id,
            string source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = source;
            _packages.TryGetValue(id, out var package);
            return Task.FromResult(package);
        }

        public Task<WingetOperationResult> InstallAsync(
            WingetPackageProfile package,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
                ? "1.0.0"
                : package.Version;
            _packages[package.Id] = new WingetInstalledPackage(package.Id, version, null, package.Source);
            return Task.FromResult(new WingetOperationResult(true, false, "installed"));
        }

        public Task<WingetOperationResult> UpgradeAsync(
            WingetPackageProfile package,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
                ? "2.0.0"
                : package.Version;
            _packages[package.Id] = new WingetInstalledPackage(package.Id, version, null, package.Source);
            return Task.FromResult(new WingetOperationResult(true, false, "upgraded"));
        }

        public Task<WingetOperationResult> UninstallAsync(
            WingetPackageProfile package,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _packages.Remove(package.Id);
            return Task.FromResult(new WingetOperationResult(true, false, "uninstalled"));
        }
    }
}
