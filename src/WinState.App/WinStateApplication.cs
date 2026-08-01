using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinState.App.Diagnostics;
using WinState.Core.Planning;
using WinState.Core.Profiles;
using WinState.Domain.Profiles;
using WinState.Infrastructure.Configuration;
using WinState.Storage;

namespace WinState.App;

/// <summary>Композиционный корень и фасад прикладных сценариев WinState.</summary>
public sealed class WinStateApplication : IAsyncDisposable
{
    public const string Version = "0.2.0-alpha.1";

    private readonly ServiceProvider _services;

    private WinStateApplication(ServiceProvider services, WinStateOptions options)
    {
        _services = services;
        Options = options;
    }

    public WinStateOptions Options { get; }

    public static WinStateApplication Create(
        string? homeOverride = null,
        string? currentDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var options = WinStateSettingsLoader.Load(homeOverride, currentDirectory, environment);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(options.MinimumLogLevel);
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
        });
        services.AddSingleton<BootstrapYamlProfileReader>();
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<DependencyGraph>();
        services.AddSingleton<SqliteStateStore>();
        services.AddSingleton<DoctorService>();
        return new WinStateApplication(services.BuildServiceProvider(), options);
    }

    public async Task<(WinStateProfile Profile, ProfileValidationResult Validation)> ValidateProfileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var reader = _services.GetRequiredService<BootstrapYamlProfileReader>();
        var validator = _services.GetRequiredService<ProfileValidator>();
        var profile = await reader.LoadAsync(path, cancellationToken);
        return (profile, validator.Validate(profile));
    }

    public Task<DoctorReport> RunDoctorAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<DoctorService>().RunAsync(cancellationToken);

    public Task InitializeStorageAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<SqliteStateStore>().InitializeAsync(cancellationToken);

    public Task<StorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<SqliteStateStore>().GetStatusAsync(cancellationToken);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
