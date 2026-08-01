using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinState.Apply;
using WinState.App.Diagnostics;
using WinState.Core.Planning;
using WinState.Core.Profiles;
using WinState.Infrastructure.Configuration;
using WinState.Providers.EnvironmentVariables;
using WinState.Providers.Features;
using WinState.Providers.Packages;
using WinState.Storage;

namespace WinState.App;

public sealed record ProfileCatalogEntry(
    string Name,
    string Path,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

/// <summary>Композиционный корень и фасад прикладных сценариев WinState.</summary>
public sealed class WinStateApplication : IAsyncDisposable
{
    public const string Version = "0.7.0-alpha.1";

    private readonly ServiceProvider _services;

    private WinStateApplication(ServiceProvider services, WinStateOptions options)
    {
        _services = services;
        Options = options;
    }

    public WinStateOptions Options { get; }

    public bool IsEnvironmentProviderSupported
        => _services.GetRequiredService<EnvironmentWorkflow>().IsSupported;

    public IReadOnlyCollection<string> RegisteredApplyProviders
        => _services.GetRequiredService<UnifiedApplyWorkflow>().RegisteredProviders;

    public static WinStateApplication Create(
        string? homeOverride = null,
        string? currentDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool quiet = false)
    {
        var options = WinStateSettingsLoader.Load(homeOverride, currentDirectory, environment);
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(options.MinimumLogLevel);
            if (!quiet)
            {
                builder.AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                });
            }
        });
        services.AddSingleton<ProfileEngine>();
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<DependencyGraph>();
        services.AddSingleton<SqliteStateStore>();
        services.AddSingleton<TransactionHistoryStore>();

        services.AddSingleton<IEnvironmentStore, WindowsEnvironmentStore>();
        services.AddSingleton<EnvironmentStateProvider>();
        services.AddSingleton<EnvironmentWorkflow>();

        services.AddSingleton<IWingetClient, ProcessWingetClient>();
        services.AddSingleton<WingetPackageProvider>();
        services.AddSingleton<IWindowsFeatureClient, DismWindowsFeatureClient>();
        services.AddSingleton<WindowsFeatureProvider>();

        services.AddSingleton<IApplyProviderExecutor, EnvironmentApplyExecutor>();
        services.AddSingleton<IApplyProviderExecutor, WingetApplyExecutor>();
        services.AddSingleton<IApplyProviderExecutor, WindowsFeatureApplyExecutor>();
        services.AddSingleton<ApplyEngine>();
        services.AddSingleton<UnifiedApplyWorkflow>();
        services.AddSingleton<DoctorService>();
        return new WinStateApplication(services.BuildServiceProvider(), options);
    }

    public async Task<(
        LoadedProfile Loaded,
        ProfileValidationResult Validation)> ValidateProfileAsync(
        string path,
        IReadOnlyDictionary<string, string>? variables,
        CancellationToken cancellationToken)
    {
        var engine = _services.GetRequiredService<ProfileEngine>();
        var validator = _services.GetRequiredService<ProfileValidator>();
        var loaded = await engine.LoadAsync(
            path,
            new ProfileLoadOptions(variables, ReadEnvironment()),
            cancellationToken);
        return (loaded, validator.Validate(loaded.Profile));
    }

    public Task<(LoadedProfile Loaded, ProfileValidationResult Validation)> ValidateProfileAsync(
        string path,
        CancellationToken cancellationToken)
        => ValidateProfileAsync(path, null, cancellationToken);

    public Task<EnvironmentStatusReport> GetEnvironmentStatusAsync(
        CancellationToken cancellationToken)
        => _services.GetRequiredService<EnvironmentWorkflow>()
            .GetStatusAsync(cancellationToken);

    public Task<EnvironmentPlanReport> PlanEnvironmentAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<EnvironmentWorkflow>()
            .PlanAsync(profilePath, variables, ReadEnvironment(), cancellationToken);

    public Task<EnvironmentExecutionReport> ApplyEnvironmentAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        bool allowMachineScope,
        bool automaticRollback,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<EnvironmentWorkflow>()
            .ApplyAsync(
                profilePath,
                variables,
                ReadEnvironment(),
                allowMachineScope,
                automaticRollback,
                cancellationToken);

    public Task<IReadOnlyList<EnvironmentCheckpointEntry>> ListEnvironmentCheckpointsAsync(
        CancellationToken cancellationToken)
        => _services.GetRequiredService<EnvironmentWorkflow>()
            .ListCheckpointsAsync(cancellationToken);

    public Task<EnvironmentExecutionReport> RollbackEnvironmentAsync(
        string checkpointPath,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<EnvironmentWorkflow>()
            .RollbackAsync(checkpointPath, cancellationToken);

    public Task<UnifiedApplyPlanReport> PlanUnifiedApplyAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .PlanAsync(profilePath, variables, ReadEnvironment(), cancellationToken);

    public Task<ApplyEngineReport> ApplyUnifiedAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        ApplyEngineOptions options,
        bool isElevated,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .ApplyAsync(
                profilePath,
                variables,
                ReadEnvironment(),
                options,
                isElevated,
                cancellationToken);

    public Task<ApplyEngineReport> ResumeUnifiedApplyAsync(
        string manifestPath,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .ResumeAsync(manifestPath, cancellationToken);

    public Task<ApplyEngineReport> RollbackUnifiedApplyAsync(
        string manifestPath,
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .RollbackAsync(manifestPath, cancellationToken);

    public Task<UnifiedApplyStatusReport> GetUnifiedApplyStatusAsync(
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .GetStatusAsync(cancellationToken);

    public Task<SystemProvidersStatusReport> GetSystemProvidersStatusAsync(
        CancellationToken cancellationToken)
        => _services.GetRequiredService<UnifiedApplyWorkflow>()
            .GetProvidersStatusAsync(cancellationToken);

    public Task<DoctorReport> RunDoctorAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<DoctorService>().RunAsync(cancellationToken);

    public Task InitializeStorageAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<SqliteStateStore>().InitializeAsync(cancellationToken);

    public Task<StorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<SqliteStateStore>().GetStatusAsync(cancellationToken);

    public Task<IReadOnlyCollection<string>> GetStorageTablesAsync(CancellationToken cancellationToken)
        => _services.GetRequiredService<SqliteStateStore>().ListTablesAsync(cancellationToken);

    public Task<IReadOnlyList<ProfileCatalogEntry>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(Options.ProfilesDirectory))
        {
            return Task.FromResult<IReadOnlyList<ProfileCatalogEntry>>(Array.Empty<ProfileCatalogEntry>());
        }

        var entries = Directory
            .EnumerateFiles(Options.ProfilesDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                return new ProfileCatalogEntry(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ProfileCatalogEntry>>(entries);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value?.ToString();
            }
        }

        return values;
    }
}
