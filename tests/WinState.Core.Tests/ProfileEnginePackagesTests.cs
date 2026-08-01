using WinState.Core.Profiles;
using Xunit;

namespace WinState.Core.Tests;

public sealed class ProfileEnginePackagesTests
{
    [Fact]
    public async Task LoadAsync_NormalizesPackagesFeaturesAndOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winstate-profile-v07-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var basePath = Path.Combine(root, "base.yaml");
            var childPath = Path.Combine(root, "child.yaml");
            await File.WriteAllTextAsync(basePath, """
                schemaVersion: 1
                metadata:
                  name: Base
                variables:
                  packageId: Git.Git
                packages:
                  - id: "{{packageId}}"
                    version: 2.40.0
                    scope: user
                features:
                  - name: Microsoft-Windows-Subsystem-Linux
                    state: disabled
                """);
            await File.WriteAllTextAsync(childPath, """
                schemaVersion: 1
                extends: [base.yaml]
                metadata:
                  name: Developer
                packages:
                  - id: Git.Git
                    version: latest
                    scope: machine
                    allowUpgrade: true
                features:
                  - name: Microsoft-Windows-Subsystem-Linux
                    state: enabled
                    includeParents: true
                """);

            var loaded = await new ProfileEngine().LoadAsync(childPath, CancellationToken.None);

            var package = Assert.Single(loaded.Profile.Packages);
            Assert.Equal("Git.Git", package.Id);
            Assert.Equal("latest", package.Version);
            Assert.Equal("machine", package.Scope);
            var feature = Assert.Single(loaded.Profile.Features);
            Assert.Equal("enabled", feature.State);
            Assert.True(feature.IncludeParents);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ValidatorRejectsUnsupportedPackageAndFeatureStates()
    {
        var profile = new WinState.Domain.Profiles.WinStateProfile
        {
            Metadata = new WinState.Domain.Profiles.ProfileMetadata { Name = "Invalid" },
            Packages =
            [
                new WinState.Domain.Profiles.WingetPackageProfile
                {
                    Id = "Git.Git",
                    State = "unknown",
                    Scope = "global"
                }
            ],
            Features =
            [
                new WinState.Domain.Profiles.WindowsFeatureProfile
                {
                    Name = "Feature",
                    State = "present"
                }
            ]
        };

        var validation = new ProfileValidator().Validate(profile);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "packages.state.unsupported");
        Assert.Contains(validation.Issues, issue => issue.Code == "packages.scope.unsupported");
        Assert.Contains(validation.Issues, issue => issue.Code == "features.state.unsupported");
    }
}
