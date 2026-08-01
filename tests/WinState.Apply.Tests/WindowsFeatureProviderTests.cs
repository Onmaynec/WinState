using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Providers;
using WinState.Providers.Features;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class WindowsFeatureProviderTests
{
    [Fact]
    public async Task Plan_EnableFeature_RequiresAdminAndRebootBoundary()
    {
        var client = new FakeFeatureClient(
            [new WindowsFeatureState("Microsoft-Windows-Subsystem-Linux", false, "Disabled")]);
        var provider = new WindowsFeatureProvider(client);
        var discovery = await provider.DiscoverAsync(
            new ProviderContext("test", false, Environment.CurrentDirectory),
            CancellationToken.None);
        var desired = WindowsFeatureProfileMapper.CreateResource(
            new WindowsFeatureProfile
            {
                Name = "Microsoft-Windows-Subsystem-Linux",
                State = "enabled"
            });

        var actions = await provider.PlanAsync(
            new DesiredProviderState([desired]),
            new CurrentProviderState(discovery.Resources),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        var action = Assert.Single(actions);
        Assert.Equal(ActionType.Enable, action.Operation);
        Assert.Equal(RiskLevel.Medium, action.Risk);
        Assert.True(action.RequiresAdministrator);
        Assert.True(action.MayRequireReboot);
        Assert.True(action.SupportsRollback);
    }

    [Fact]
    public async Task Feature_Checkpoint_Apply_Verify_AndRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-feature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string name = "Containers-DisposableClientVM";
            var client = new FakeFeatureClient([new WindowsFeatureState(name, false, "Disabled")]);
            var provider = new WindowsFeatureProvider(client);
            var discovery = await provider.DiscoverAsync(
                new ProviderContext("test", false, root),
                CancellationToken.None);
            var desired = WindowsFeatureProfileMapper.CreateResource(
                new WindowsFeatureProfile { Name = name, State = "enabled" });
            var action = (await provider.PlanAsync(
                new DesiredProviderState([desired]),
                new CurrentProviderState(discovery.Resources),
                new PlanningContext(false, false, "test"),
                CancellationToken.None)).Single();
            var context = new ProviderExecutionContext("txn", true, root);

            var checkpoint = await provider.PrepareRollbackAsync(action, context, CancellationToken.None);
            var applied = await provider.ApplyAsync(action, context, CancellationToken.None);
            var verification = await provider.VerifyAsync(action, context, CancellationToken.None);
            var rollback = await provider.RollbackAsync(
                new RollbackAction(action.Id, provider.Id, checkpoint.BackupReference!),
                context,
                CancellationToken.None);
            var restored = await client.GetAsync(name, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, applied.Status);
            Assert.True(verification.IsMatch);
            Assert.True(rollback.Succeeded);
            Assert.NotNull(restored);
            Assert.False(restored.Enabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnknownFeature_ProducesUnsupportedIrreversibleAction()
    {
        var provider = new WindowsFeatureProvider(new FakeFeatureClient());
        var desired = WindowsFeatureProfileMapper.CreateResource(
            new WindowsFeatureProfile { Name = "Unknown-Feature" });

        var actions = await provider.PlanAsync(
            new DesiredProviderState([desired]),
            new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        var action = Assert.Single(actions);
        Assert.Equal(ActionType.Unsupported, action.Operation);
        Assert.False(action.SupportsRollback);
    }

    private sealed class FakeFeatureClient : IWindowsFeatureClient
    {
        private readonly Dictionary<string, WindowsFeatureState> _features;

        public FakeFeatureClient(IReadOnlyCollection<WindowsFeatureState>? features = null)
        {
            _features = (features ?? Array.Empty<WindowsFeatureState>())
                .ToDictionary(feature => feature.Name, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsSupported => true;

        public Task<IReadOnlyList<WindowsFeatureState>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WindowsFeatureState>>(_features.Values.ToArray());
        }

        public Task<WindowsFeatureState?> GetAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _features.TryGetValue(name, out var feature);
            return Task.FromResult(feature);
        }

        public Task<WindowsFeatureOperationResult> EnableAsync(
            string name,
            bool includeParents,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = includeParents;
            _features[name] = new WindowsFeatureState(name, true, "Enabled");
            return Task.FromResult(new WindowsFeatureOperationResult(true, true, "enabled"));
        }

        public Task<WindowsFeatureOperationResult> DisableAsync(
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _features[name] = new WindowsFeatureState(name, false, "Disabled");
            return Task.FromResult(new WindowsFeatureOperationResult(true, true, "disabled"));
        }
    }
}
