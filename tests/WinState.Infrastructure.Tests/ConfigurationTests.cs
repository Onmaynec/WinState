using Microsoft.Extensions.Logging;
using WinState.Infrastructure.Configuration;
using Xunit;

namespace WinState.Infrastructure.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Load_applies_home_override_and_environment_precedence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "winstate.json"), """
                {
                  "storage": { "database": "db/custom.db" },
                  "profiles": { "directory": "profiles-from-json" },
                  "logging": { "minimumLevel": "Warning" }
                }
                """);
            var home = Path.Combine(root, "home");
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["WINSTATE_PROFILES"] = "profiles-from-env",
                ["WINSTATE_LOG_LEVEL"] = "Debug"
            };

            var options = WinStateSettingsLoader.Load(home, root, environment);

            Assert.Equal(Path.GetFullPath(home), options.HomeDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(home), "profiles-from-env"), options.ProfilesDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(home), "db", "custom.db"), options.DatabasePath);
            Assert.Equal(LogLevel.Debug, options.MinimumLogLevel);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winstate-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
