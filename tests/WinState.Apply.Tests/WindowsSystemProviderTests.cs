using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Providers.SystemControl;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class WindowsSystemProviderTests
{
    [Fact]
    public async Task Registry_Create_IsRollbackCapableAndUserScoped()
    {
        var client = new FakeWindowsSystemClient();
        var provider = new WindowsSystemProvider(client);
        var profile = new WindowsSystemProfile(
            [new RegistryValueProfile
            {
                Hive = "HKCU",
                Path = @"Software\WinState.Tests",
                Name = "Channel",
                Value = "alpha"
            }],
            Array.Empty<WindowsServiceProfile>(),
            Array.Empty<StartupEntryProfile>(),
            Array.Empty<ScheduledTaskProfile>());

        var actions = await provider.PlanAsync(
            WindowsSystemProfileMapper.CreateDesiredState(profile),
            new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
            new PlanningContext(false, false, "test"),
            CancellationToken.None);

        var action = Assert.Single(actions);
        Assert.Equal(ActionType.Create, action.Operation);
        Assert.Equal(RiskLevel.Low, action.Risk);
        Assert.False(action.RequiresAdministrator);
        Assert.True(action.SupportsRollback);
    }

    [Fact]
    public async Task Service_Disable_IsHighRiskAdministratorAction()
    {
        var client = new FakeWindowsSystemClient
        {
            Service = new ServiceSnapshot(true, "running", "automatic")
        };
        var provider = new WindowsSystemProvider(client);
        var profile = new WindowsSystemProfile(
            Array.Empty<RegistryValueProfile>(),
            [new WindowsServiceProfile
            {
                Name = "Spooler",
                State = "stopped",
                StartMode = "disabled"
            }],
            Array.Empty<StartupEntryProfile>(),
            Array.Empty<ScheduledTaskProfile>());

        var action = Assert.Single(await provider.PlanAsync(
            WindowsSystemProfileMapper.CreateDesiredState(profile),
            new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
            new PlanningContext(false, false, "test"),
            CancellationToken.None));

        Assert.Equal(ActionType.Modify, action.Operation);
        Assert.Equal(RiskLevel.High, action.Risk);
        Assert.True(action.RequiresAdministrator);
    }

    [Fact]
    public async Task Startup_ApplyVerifyRollback_RestoresAbsence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-system-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var client = new FakeWindowsSystemClient();
            var provider = new WindowsSystemProvider(client);
            var profile = new WindowsSystemProfile(
                Array.Empty<RegistryValueProfile>(),
                Array.Empty<WindowsServiceProfile>(),
                [new StartupEntryProfile
                {
                    Name = "WinState Test",
                    Command = @"C:\Tools\test.exe"
                }],
                Array.Empty<ScheduledTaskProfile>());
            var action = Assert.Single(await provider.PlanAsync(
                WindowsSystemProfileMapper.CreateDesiredState(profile),
                new CurrentProviderState(Array.Empty<WinState.Domain.Resources.StateResource>()),
                new PlanningContext(false, false, "test"),
                CancellationToken.None));
            var context = new ProviderExecutionContext("txn", false, root);

            var checkpoint = await provider.PrepareRollbackAsync(action, context, CancellationToken.None);
            var applied = await provider.ApplyAsync(action, context, CancellationToken.None);
            var verified = await provider.VerifyAsync(action, context, CancellationToken.None);
            var rollback = await provider.RollbackAsync(
                new RollbackAction(action.Id, provider.Id, checkpoint.BackupReference!),
                context,
                CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, applied.Status);
            Assert.True(verified.IsMatch);
            Assert.True(rollback.Succeeded);
            Assert.False(client.Startup.Exists);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Loader_RejectsRegistryOutsideSoftwareAllowlist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profile.yaml");
        await File.WriteAllTextAsync(path, """
            schemaVersion: 1
            metadata:
              name: unsafe
            registry:
              - hive: HKLM
                path: SYSTEM\\CurrentControlSet
                name: Unsafe
                value: blocked
            """);
        try
        {
            var loader = new WindowsSystemProfileLoader();
            await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(
                path,
                null,
                new Dictionary<string, string?>(),
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeWindowsSystemClient : IWindowsSystemClient
    {
        public bool IsSupported => true;
        public RegistryValueSnapshot Registry { get; private set; } = new(false, "string", null);
        public ServiceSnapshot Service { get; set; } = new(false, "missing", "missing");
        public StartupEntrySnapshot Startup { get; private set; } = new(false, null);
        public ScheduledTaskSnapshot Task { get; private set; } = new(false, null);

        public Task<RegistryValueSnapshot> GetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(Registry);

        public Task<WindowsSystemOperationResult> SetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
        {
            Registry = new RegistryValueSnapshot(true, profile.Type, profile.Value);
            return Success("registry set");
        }

        public Task<WindowsSystemOperationResult> DeleteRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
        {
            Registry = new RegistryValueSnapshot(false, profile.Type, null);
            return Success("registry deleted");
        }

        public Task<ServiceSnapshot> GetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(Service);

        public Task<WindowsSystemOperationResult> SetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken)
        {
            Service = new ServiceSnapshot(
                true,
                profile.State == "unchanged" ? Service.State : profile.State,
                profile.StartMode == "unchanged" ? Service.StartMode : profile.StartMode);
            return Success("service set");
        }

        public Task<StartupEntrySnapshot> GetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(Startup);

        public Task<WindowsSystemOperationResult> SetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
        {
            Startup = new StartupEntrySnapshot(true, profile.Command);
            return Success("startup set");
        }

        public Task<WindowsSystemOperationResult> DeleteStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
        {
            Startup = new StartupEntrySnapshot(false, null);
            return Success("startup deleted");
        }

        public Task<ScheduledTaskSnapshot> GetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
            => Task.FromResult(Task);

        public Task<WindowsSystemOperationResult> SetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
        {
            Task = new ScheduledTaskSnapshot(true, "<Task><Triggers><LogonTrigger /></Triggers><Actions><Exec><Command>test.exe</Command></Exec></Actions></Task>");
            return Success("task set");
        }

        public Task<WindowsSystemOperationResult> DeleteTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
        {
            Task = new ScheduledTaskSnapshot(false, null);
            return Success("task deleted");
        }

        public Task<WindowsSystemOperationResult> RestoreTaskAsync(string name, string xml, CancellationToken cancellationToken)
        {
            Task = new ScheduledTaskSnapshot(true, xml);
            return Success("task restored");
        }

        private static Task<WindowsSystemOperationResult> Success(string message)
            => Task.FromResult(new WindowsSystemOperationResult(true, message));
    }
}