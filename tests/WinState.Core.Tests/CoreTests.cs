using WinState.Core.Planning;
using WinState.Core.Profiles;
using WinState.Domain.Configuration;
using WinState.Domain.Errors;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Resources;
using Xunit;

namespace WinState.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void DependencyGraph_places_dependencies_before_dependents()
    {
        var feature = Action("feature");
        var wsl = Action("wsl", "feature");
        var distro = Action("distro", "wsl");
        var sorted = new DependencyGraph().Sort([distro, feature, wsl]);
        Assert.Equal(new[] { "feature", "wsl", "distro" }, sorted.Select(action => action.Id));
    }

    [Fact]
    public void DependencyGraph_rejects_cycles()
    {
        Assert.Throws<WinStateDomainException>(() => new DependencyGraph().Sort([Action("first", "second"), Action("second", "first")]));
    }

    [Fact]
    public void ProfileValidator_accepts_minimal_profile()
    {
        var profile = new WinStateProfile { SchemaVersion = 1, Metadata = new ProfileMetadata { Name = "Developer Workstation" } };
        Assert.True(new ProfileValidator().Validate(profile).IsValid);
    }

    private static PlannedAction Action(string id, params string[] dependencies) => new()
    {
        Id = id,
        ProviderId = "test",
        Resource = new StateResource { ProviderId = "test", ResourceType = "fixture", Identity = $"fixture://{id}", State = DesiredState.Present },
        Operation = ActionType.Create,
        Risk = RiskLevel.Low,
        Explanation = "Тестовое действие.",
        DependsOn = dependencies
    };
}
