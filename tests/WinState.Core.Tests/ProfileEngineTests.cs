using WinState.Core.Profiles;
using Xunit;

namespace WinState.Core.Tests;

public sealed class ProfileEngineTests
{
    [Fact]
    public async Task LoadAsync_merges_extends_and_resolves_variables()
    {
        using var directory = new TemporaryDirectory();
        var baseProfile = Path.Combine(directory.Path, "base.yaml");
        var workstation = Path.Combine(directory.Path, "workstation.yaml");
        await File.WriteAllTextAsync(baseProfile, """
            schemaVersion: 1
            metadata:
              name: Base
            variables:
              toolsRoot: ./tools
            environment:
              user:
                BASE_MODE: enabled
              userPath:
                - path: "{{toolsRoot}}/bin"
                  position: prepend
            """);
        await File.WriteAllTextAsync(workstation, """
            schemaVersion: 1
            extends:
              - base.yaml
            metadata:
              name: "{{developer}} Workstation"
            environment:
              user:
                DEV_MODE: "${mode}"
            """);

        var loaded = await new ProfileEngine().LoadAsync(
            workstation,
            new ProfileLoadOptions(new Dictionary<string, string>
            {
                ["developer"] = "Alex",
                ["mode"] = "true"
            }),
            CancellationToken.None);

        Assert.Equal("Alex Workstation", loaded.Profile.Metadata.Name);
        Assert.Equal("enabled", loaded.Profile.Environment.User["BASE_MODE"]);
        Assert.Equal("true", loaded.Profile.Environment.User["DEV_MODE"]);
        Assert.Single(loaded.Profile.Environment.UserPath);
        Assert.Equal(2, loaded.SourceFiles.Count);
    }

    [Fact]
    public async Task LoadAsync_uses_winstate_environment_variables_before_cli_overrides()
    {
        using var directory = new TemporaryDirectory();
        var profilePath = Path.Combine(directory.Path, "profile.yaml");
        await File.WriteAllTextAsync(profilePath, """
            schemaVersion: 1
            metadata:
              name: "{{name}}"
            """);

        var loaded = await new ProfileEngine().LoadAsync(
            profilePath,
            new ProfileLoadOptions(
                new Dictionary<string, string> { ["name"] = "CLI" },
                new Dictionary<string, string?> { ["WINSTATE_VAR_name"] = "ENV" }),
            CancellationToken.None);

        Assert.Equal("CLI", loaded.Profile.Metadata.Name);
    }

    [Fact]
    public async Task LoadAsync_rejects_reference_cycles()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.yaml");
        var second = Path.Combine(directory.Path, "second.yaml");
        await File.WriteAllTextAsync(first, """
            schemaVersion: 1
            includes: [second.yaml]
            metadata: { name: First }
            """);
        await File.WriteAllTextAsync(second, """
            schemaVersion: 1
            includes: [first.yaml]
            metadata: { name: Second }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ProfileEngine().LoadAsync(first, CancellationToken.None));
        Assert.Contains("цикл", exception.Message.ToLowerInvariant());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winstate-profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
