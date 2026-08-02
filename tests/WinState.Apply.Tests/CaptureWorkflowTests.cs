using WinState.App;
using WinState.Core.Profiles;
using WinState.Providers.EnvironmentVariables;
using WinState.Providers.Features;
using WinState.Providers.Packages;
using Xunit;

namespace WinState.Apply.Tests;

public sealed class CaptureWorkflowTests
{
    [Fact]
    public async Task ExportAsync_WritesValidProfileAndSkipsSensitiveVariables()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "snapshot.yaml");
        try
        {
            var environment = new InMemoryEnvironmentStore(
                new Dictionary<string, string?>
                {
                    ["EDITOR"] = "code",
                    ["MY_API_TOKEN"] = "must-not-leak"
                },
                new Dictionary<string, string?>
                {
                    ["DOTNET_ENVIRONMENT"] = "Production"
                },
                [@"C:\Users\demo\bin"],
                [@"C:\Program Files\Git\cmd"]);
            var workflow = new CaptureWorkflow(
                environment,
                new FakeWingetClient(),
                new FakeFeatureClient());

            var report = await workflow.ExportAsync(
                output,
                "Тестовый снимок",
                CancellationToken.None);

            Assert.True(File.Exists(report.ProfilePath));
            Assert.True(File.Exists(report.ManifestPath));
            Assert.Equal(1, report.Counts.SkippedSensitiveValues);
            var yaml = await File.ReadAllTextAsync(report.ProfilePath);
            Assert.Contains("EDITOR", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-leak", yaml, StringComparison.Ordinal);
            Assert.Contains("position: preserve", yaml, StringComparison.Ordinal);
            Assert.Contains("Git.Git", yaml, StringComparison.Ordinal);
            Assert.Contains("Microsoft-Windows-Subsystem-Linux", yaml, StringComparison.Ordinal);

            var loaded = await new ProfileEngine().LoadAsync(report.ProfilePath, CancellationToken.None);
            var validation = new ProfileValidator().Validate(loaded.Profile);
            Assert.True(validation.IsValid);
            Assert.Equal("Тестовый снимок", loaded.Profile.Metadata.Name);
            Assert.Single(loaded.Profile.Packages);
            Assert.Single(loaded.Profile.Features);
            Assert.All(
                loaded.Profile.Environment.UserPath.Concat(loaded.Profile.Environment.MachinePath),
                entry => Assert.Equal("preserve", entry.Position));
            Assert.Equal("code", loaded.Profile.Environment.User["EDITOR"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExportAsync_DropsAmbiguousWingetRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workflow = new CaptureWorkflow(
                new InMemoryEnvironmentStore(),
                new AmbiguousWingetClient(),
                new FakeFeatureClient());
            var report = await workflow.ExportAsync(
                Path.Combine(root, "snapshot.yaml"),
                "Проверка WinGet",
                CancellationToken.None);
            var yaml = await File.ReadAllTextAsync(report.ProfilePath);

            Assert.Contains("Git.Git", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("19.4.1.0", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("ARP\\Machine", yaml, StringComparison.Ordinal);
            Assert.Contains(
                report.Diagnostics,
                diagnostic => diagnostic.Contains("неоднозначных строк", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeWingetClient : IWingetClient
    {
        public bool IsSupported => true;

        public Task<IReadOnlyList<WingetInstalledPackage>> ListInstalledAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WingetInstalledPackage>>(
                [new WingetInstalledPackage("Git.Git", "2.50.0", null, "winget")]);

        public Task<WingetInstalledPackage?> GetInstalledAsync(
            string id,
            string source,
            CancellationToken cancellationToken)
            => Task.FromResult<WingetInstalledPackage?>(null);

        public Task<WingetOperationResult> InstallAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WingetOperationResult> UpgradeAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WingetOperationResult> UninstallAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class AmbiguousWingetClient : IWingetClient
    {
        public bool IsSupported => true;

        public Task<IReadOnlyList<WingetInstalledPackage>> ListInstalledAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WingetInstalledPackage>>(
            [
                new WingetInstalledPackage("Git.Git", "2.50.0", null, "winget"),
                new WingetInstalledPackage("19.4.1.0", "winget", null, "winget"),
                new WingetInstalledPackage("ARP\\Machine\\X64\\Mozilla Firefox", "152.0.5", null, "152.0.5"),
                new WingetInstalledPackage("py314_26.5.3-1", "winget", null, "py314_26.5.3-2 winget")
            ]);

        public Task<WingetInstalledPackage?> GetInstalledAsync(
            string id,
            string source,
            CancellationToken cancellationToken)
            => Task.FromResult<WingetInstalledPackage?>(null);

        public Task<WingetOperationResult> InstallAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WingetOperationResult> UpgradeAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WingetOperationResult> UninstallAsync(
            WinState.Domain.Profiles.WingetPackageProfile package,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeFeatureClient : IWindowsFeatureClient
    {
        public bool IsSupported => true;

        public Task<IReadOnlyList<WindowsFeatureState>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WindowsFeatureState>>(
            [
                new WindowsFeatureState("Microsoft-Windows-Subsystem-Linux", true, "Enabled"),
                new WindowsFeatureState("TelnetClient", false, "Disabled")
            ]);

        public Task<WindowsFeatureState?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult<WindowsFeatureState?>(null);

        public Task<WindowsFeatureOperationResult> EnableAsync(
            string name,
            bool includeParents,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WindowsFeatureOperationResult> DisableAsync(
            string name,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
