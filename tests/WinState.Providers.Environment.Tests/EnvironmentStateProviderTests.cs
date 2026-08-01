using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Providers;
using WinState.Providers.EnvironmentVariables;
using Xunit;

namespace WinState.Providers.Environment.Tests;

public sealed class EnvironmentStateProviderTests
{
    [Fact]
    public async Task Plan_detects_variable_path_addition_and_path_removal()
    {
        var store = new InMemoryEnvironmentStore(
            userVariables: new Dictionary<string, string?> { ["DEV_MODE"] = "false" },
            userPath: [@"C:\Bin", @"C:\Legacy"]);
        var provider = new EnvironmentStateProvider(store);
        var profile = Profile(
            user: new Dictionary<string, string> { ["DEV_MODE"] = "true" },
            userPath:
            [
                new PathEntryProfile { Path = @"C:\Tools", Position = "prepend" },
                new PathEntryProfile { Path = @"C:\Legacy", State = "absent" }
            ]);

        var discovered = await provider.DiscoverAsync(Context(), CancellationToken.None);
        var actions = await provider.PlanAsync(
            EnvironmentProfileMapper.CreateDesiredState(profile),
            new CurrentProviderState(discovered.Resources),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        Assert.Equal(3, actions.Count);
        Assert.Contains(actions, action => action.Operation == ActionType.Modify);
        Assert.Contains(actions, action => action.Operation == ActionType.Create
            && action.Resource.ResourceType == EnvironmentProfileMapper.PathResourceType);
        Assert.Contains(actions, action => action.Operation == ActionType.Remove);
        Assert.All(actions, action => Assert.True(action.SupportsRollback));
    }

    [Fact]
    public async Task Apply_verify_and_rollback_restore_original_state()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-env-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEnvironmentStore(
                userVariables: new Dictionary<string, string?> { ["DEV_MODE"] = "false" },
                userPath: [@"C:\Bin"]);
            var provider = new EnvironmentStateProvider(store);
            var profile = Profile(
                user: new Dictionary<string, string> { ["DEV_MODE"] = "true" },
                userPath: [new PathEntryProfile { Path = @"C:\Tools", Position = "append" }]);
            var discovered = await provider.DiscoverAsync(Context(), CancellationToken.None);
            var actions = await provider.PlanAsync(
                EnvironmentProfileMapper.CreateDesiredState(profile),
                new CurrentProviderState(discovered.Resources),
                new PlanningContext(false, false, "test"),
                CancellationToken.None);
            var execution = new ProviderExecutionContext("tx-test", true, root);
            var rollbacks = new List<RollbackAction>();

            foreach (var action in actions)
            {
                var checkpoint = await provider.PrepareRollbackAsync(
                    action,
                    execution,
                    CancellationToken.None);
                Assert.True(checkpoint.IsSupported);
                Assert.NotNull(checkpoint.BackupReference);
                rollbacks.Add(new RollbackAction(action.Id, provider.Id, checkpoint.BackupReference!));

                var result = await provider.ApplyAsync(action, execution, CancellationToken.None);
                Assert.Equal(ActionStatus.Succeeded, result.Status);
                var verification = await provider.VerifyAsync(action, execution, CancellationToken.None);
                Assert.True(verification.IsMatch, verification.Message);
            }

            Assert.Equal("true", await store.ReadVariableAsync(
                EnvironmentScope.User,
                "DEV_MODE",
                CancellationToken.None));
            Assert.Contains(@"C:\Tools", await store.ReadPathAsync(
                EnvironmentScope.User,
                CancellationToken.None));

            foreach (var rollback in rollbacks.AsEnumerable().Reverse())
            {
                var result = await provider.RollbackAsync(rollback, execution, CancellationToken.None);
                Assert.True(result.Succeeded, result.Message);
            }

            Assert.Equal("false", await store.ReadVariableAsync(
                EnvironmentScope.User,
                "DEV_MODE",
                CancellationToken.None));
            Assert.Equal(new[] { @"C:\Bin" }, await store.ReadPathAsync(
                EnvironmentScope.User,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Plan_is_empty_when_environment_already_matches()
    {
        var store = new InMemoryEnvironmentStore(
            userVariables: new Dictionary<string, string?> { ["DEV_MODE"] = "true" },
            userPath: [@"C:\Tools"]);
        var provider = new EnvironmentStateProvider(store);
        var profile = Profile(
            user: new Dictionary<string, string> { ["DEV_MODE"] = "true" },
            userPath: [new PathEntryProfile { Path = @"C:\Tools", Position = "append" }]);
        var discovered = await provider.DiscoverAsync(Context(), CancellationToken.None);

        var actions = await provider.PlanAsync(
            EnvironmentProfileMapper.CreateDesiredState(profile),
            new CurrentProviderState(discovered.Resources),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task Machine_actions_are_medium_risk_and_require_administrator()
    {
        var store = new InMemoryEnvironmentStore();
        var provider = new EnvironmentStateProvider(store);
        var profile = Profile(machine: new Dictionary<string, string> { ["WINSTATE_MACHINE_TEST"] = "1" });
        var discovered = await provider.DiscoverAsync(Context(), CancellationToken.None);

        var action = Assert.Single(await provider.PlanAsync(
            EnvironmentProfileMapper.CreateDesiredState(profile),
            new CurrentProviderState(discovered.Resources),
            new PlanningContext(false, false, "test"),
            CancellationToken.None));

        Assert.Equal(RiskLevel.Medium, action.Risk);
        Assert.True(action.RequiresAdministrator);
    }

    private static ProviderContext Context()
        => new("test", true, System.Environment.CurrentDirectory);

    private static WinStateProfile Profile(
        IReadOnlyDictionary<string, string>? user = null,
        IReadOnlyDictionary<string, string>? machine = null,
        IReadOnlyCollection<PathEntryProfile>? userPath = null,
        IReadOnlyCollection<PathEntryProfile>? machinePath = null)
        => new()
        {
            Metadata = new ProfileMetadata { Name = "Environment Provider Test" },
            Environment = new EnvironmentProfileSection
            {
                User = user ?? new Dictionary<string, string>(),
                Machine = machine ?? new Dictionary<string, string>(),
                UserPath = userPath ?? Array.Empty<PathEntryProfile>(),
                MachinePath = machinePath ?? Array.Empty<PathEntryProfile>()
            }
        };
}
