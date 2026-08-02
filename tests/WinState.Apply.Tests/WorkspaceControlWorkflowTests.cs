using WinState.App;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class WorkspaceControlWorkflowTests
{
    [Fact]
    public async Task ApplyAndRollback_RestoresGitFilesAndOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-workspace-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var manifestPath = Path.Combine(root, "workspace.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "schemaVersion": 1,
              "name": "Test Workspace",
              "git": [
                { "key": "winstate.test", "value": "enabled", "state": "present" }
              ],
              "powerShellModules": [],
              "directories": [
                { "path": "sandbox", "state": "present" }
              ],
              "files": [
                {
                  "path": "sandbox/settings.txt",
                  "state": "present",
                  "encoding": "utf-8",
                  "content": "managed by WinState\n"
                }
              ]
            }
            """);

        try
        {
            var git = new FakeGitClient();
            var modules = new FakeModuleClient();
            var workflow = new WorkspaceControlWorkflow(home, git, modules);

            var validation = await workflow.ValidateAsync(manifestPath, CancellationToken.None);
            Assert.True(validation.IsValid);

            var plan = await workflow.PlanAsync(manifestPath, null, CancellationToken.None);
            Assert.True(plan.IsValid);
            Assert.True(plan.IsSupported);
            Assert.Equal(3, plan.Changes);
            Assert.True(File.Exists(plan.JsonReportPath));
            Assert.True(File.Exists(plan.MarkdownReportPath));

            var execution = await workflow.ApplyAsync(
                manifestPath,
                allowPowerShellModules: false,
                allowDelete: false,
                reportDirectory: null,
                CancellationToken.None);
            Assert.True(execution.Succeeded);
            Assert.Equal("enabled", await git.ReadGlobalAsync("winstate.test", CancellationToken.None));
            Assert.Equal(
                "managed by WinState\n",
                await File.ReadAllTextAsync(Path.Combine(root, "sandbox", "settings.txt")));

            var status = await workflow.GetStatusAsync(CancellationToken.None);
            Assert.Equal(1, status.OwnedGitSettings);
            Assert.Equal(1, status.OwnedFiles);
            Assert.Equal(1, status.OwnedDirectories);

            var rollback = await workflow.RollbackAsync(execution.TransactionPath, CancellationToken.None);
            Assert.True(rollback.Succeeded);
            Assert.Null(await git.ReadGlobalAsync("winstate.test", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "sandbox", "settings.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "sandbox")));

            status = await workflow.GetStatusAsync(CancellationToken.None);
            Assert.Equal(0, status.OwnedGitSettings);
            Assert.Equal(0, status.OwnedFiles);
            Assert.Equal(0, status.OwnedDirectories);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Plan_BlocksDeletionOfUnownedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-workspace-block-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var target = Path.Combine(root, "foreign.txt");
        var manifestPath = Path.Combine(root, "workspace.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(target, "not owned");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "schemaVersion": 1,
              "name": "Ownership Guard",
              "git": [],
              "powerShellModules": [],
              "directories": [],
              "files": [
                { "path": "foreign.txt", "state": "absent" }
              ]
            }
            """);

        try
        {
            var workflow = new WorkspaceControlWorkflow(
                home,
                new FakeGitClient(),
                new FakeModuleClient());
            var plan = await workflow.PlanAsync(manifestPath, null, CancellationToken.None);
            var action = Assert.Single(plan.Actions);
            Assert.True(action.Blocked);
            Assert.Equal("Заблокировано", action.Operation);
            Assert.True(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UpdateRestore_PreparesSafetyBackupAndScript()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-restore-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "home");
        var backup = Path.Combine(root, "backup");
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(Path.Combine(backup, "winstate.exe"), "backup-exe");
        await File.WriteAllTextAsync(Path.Combine(backup, "winstate.release.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(install, "winstate.exe"), "current-exe");
        Directory.CreateDirectory(Path.Combine(install, "profiles"));
        await File.WriteAllTextAsync(Path.Combine(install, "profiles", "keep.yaml"), "keep");

        try
        {
            var workflow = new UpdateBackupRestoreWorkflow(home);
            var report = await workflow.PrepareAsync(
                backup,
                install,
                launch: false,
                CancellationToken.None);
            Assert.False(report.Scheduled);
            Assert.True(File.Exists(report.ScriptPath));
            Assert.True(File.Exists(Path.Combine(report.SafetyBackupDirectory, "winstate.exe")));
            Assert.False(File.Exists(Path.Combine(report.SafetyBackupDirectory, "profiles", "keep.yaml")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeGitClient : IGitConfigurationClient
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSupported => true;

        public Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task WriteGlobalAsync(string key, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveGlobalAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModuleClient : IPowerShellModuleClient
    {
        public bool IsSupported => true;

        public Task<string?> ReadInstalledVersionAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task InstallAsync(
            string name,
            string? minimumVersion,
            string repository,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
