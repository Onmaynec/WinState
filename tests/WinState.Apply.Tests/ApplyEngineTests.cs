using WinState.Apply;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Domain.Resources;
using WinState.Domain.Transactions;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class ApplyEngineTests
{
    [Fact]
    public void BuildPlan_orders_dependencies_and_builds_risk_groups()
    {
        var engine = new ApplyEngine(
        [
            new FakeExecutor("provider-a"),
            new FakeExecutor("provider-b")
        ]);
        var first = Action("a", "provider-a", RiskLevel.Low);
        var second = Action(
            "b",
            "provider-b",
            RiskLevel.Medium,
            dependsOn: ["a"],
            administrator: true);

        var plan = engine.BuildPlan("profile", [second, first]);

        Assert.Equal(["a", "b"], plan.OrderedActions.Select(action => action.Id));
        Assert.Equal(RiskLevel.Medium, plan.MaximumRisk);
        Assert.True(plan.RequiresAdministrator);
        Assert.Equal(2, plan.RiskGroups.Count);
        Assert.Equal(["provider-a", "provider-b"], plan.Providers);
    }

    [Fact]
    public async Task Execute_rolls_back_previous_provider_when_later_provider_fails()
    {
        using var directory = new TemporaryDirectory();
        var firstExecutor = new FakeExecutor("provider-a");
        var secondExecutor = new FakeExecutor("provider-b", failOn: "b");
        var engine = new ApplyEngine([firstExecutor, secondExecutor]);
        var first = Action("a", "provider-a", RiskLevel.Low);
        var second = Action(
            "b",
            "provider-b",
            RiskLevel.Low,
            dependsOn: ["a"]);

        var report = await engine.ExecuteAsync(
            new ApplyEngineRequest(
                "profile",
                directory.Path,
                directory.Path,
                [second, first],
                new ApplyEngineOptions()),
            CancellationToken.None);

        Assert.Equal(TransactionStatus.RolledBack, report.Status);
        Assert.True(report.RolledBack);
        Assert.False(firstExecutor.Contains("a"));
        Assert.Contains(report.Results, result =>
            result.ActionId == "a" && result.Status == ActionStatus.RolledBack);
        Assert.Contains(report.Results, result =>
            result.ActionId == "b" && result.Status == ActionStatus.Failed);
        Assert.True(File.Exists(report.ManifestPath));
    }

    [Fact]
    public async Task Execute_marks_verified_graph_as_reboot_pending()
    {
        using var directory = new TemporaryDirectory();
        var executor = new FakeExecutor("provider-a");
        var engine = new ApplyEngine([executor]);
        var action = Action(
            "restart-sensitive",
            "provider-a",
            RiskLevel.Low,
            mayRequireReboot: true);

        var report = await engine.ExecuteAsync(
            new ApplyEngineRequest(
                "profile",
                directory.Path,
                directory.Path,
                [action],
                new ApplyEngineOptions()),
            CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.True(report.Verified);
        Assert.True(report.RebootRequired);
        Assert.Equal(TransactionStatus.SucceededRebootPending, report.Status);
    }

    [Fact]
    public void BuildPlan_rejects_cycles()
    {
        var engine = new ApplyEngine([new FakeExecutor("provider-a")]);
        var first = Action("a", "provider-a", RiskLevel.Low, dependsOn: ["b"]);
        var second = Action("b", "provider-a", RiskLevel.Low, dependsOn: ["a"]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            engine.BuildPlan("profile", [first, second]));

        Assert.Contains("цикл", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PlannedAction Action(
        string id,
        string providerId,
        RiskLevel risk,
        IReadOnlyCollection<string>? dependsOn = null,
        bool administrator = false,
        bool mayRequireReboot = false)
        => new()
        {
            Id = id,
            ProviderId = providerId,
            Resource = new StateResource
            {
                ProviderId = providerId,
                ResourceType = "test",
                Identity = $"test://{providerId}/{id}",
                State = DesiredState.Configured
            },
            Operation = ActionType.Modify,
            Risk = risk,
            RequiresAdministrator = administrator,
            MayRequireReboot = mayRequireReboot,
            SupportsRollback = true,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            Explanation = "test action"
        };

    private sealed class FakeExecutor : IApplyProviderExecutor
    {
        private readonly HashSet<string> _state = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _failOn;

        public FakeExecutor(string providerId, string? failOn = null)
        {
            ProviderId = providerId;
            _failOn = failOn;
        }

        public string ProviderId { get; }
        public bool IsSupported => true;

        public bool Contains(string actionId) => _state.Contains(actionId);

        public async Task<RollbackPreparationResult> PrepareRollbackAsync(
            PlannedAction action,
            ProviderExecutionContext context,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(context.BackupDirectory);
            var path = System.IO.Path.Combine(
                context.BackupDirectory,
                $"{action.Id}.backup");
            await File.WriteAllTextAsync(path, "prepared", cancellationToken);
            return new RollbackPreparationResult(true, path, "prepared");
        }

        public Task<ActionExecutionResult> ApplyAsync(
            PlannedAction action,
            ProviderExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Id.Equals(_failOn, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ActionExecutionResult(
                    ActionStatus.Failed,
                    "simulated failure",
                    Array.Empty<ProviderDiagnostic>()));
            }

            _state.Add(action.Id);
            return Task.FromResult(new ActionExecutionResult(
                ActionStatus.Succeeded,
                "applied",
                Array.Empty<ProviderDiagnostic>()));
        }

        public Task<VerificationResult> VerifyAsync(
            PlannedAction action,
            ProviderExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VerificationResult(
                _state.Contains(action.Id),
                _state.Contains(action.Id) ? "verified" : "missing"));
        }

        public Task<RollbackExecutionResult> RollbackAsync(
            RollbackAction action,
            ProviderExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Remove(action.ActionId);
            return Task.FromResult(new RollbackExecutionResult(true, "rolled back"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinState.Apply.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
