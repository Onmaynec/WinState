using System.Text.Json;
using WinState.Apply;
using WinState.Core.Profiles;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Infrastructure.Configuration;
using WinState.Providers.EnvironmentVariables;
using WinState.Providers.Features;
using WinState.Providers.Packages;
using WinState.Providers.SystemControl;
using WinState.Storage;

namespace WinState.App;

public sealed record UnifiedApplyPlanReport(
    LoadedProfile Loaded,
    ProfileValidationResult Validation,
    UnifiedApplyPlan Plan,
    IReadOnlyCollection<ProviderDiagnostic> Diagnostics,
    bool IsSupported);

public sealed record UnifiedApplyStatusReport(
    IReadOnlyCollection<string> RegisteredProviders,
    int Transactions,
    int ResumableTransactions,
    int RebootPendingTransactions,
    IReadOnlyList<ApplyTransactionManifest> RecentTransactions);

public sealed record SystemProvidersStatusReport(
    bool EnvironmentSupported,
    bool WingetSupported,
    bool FeaturesSupported,
    int InstalledPackages,
    int PackagesWithUpdates,
    int EnabledFeatures,
    int DisabledFeatures,
    IReadOnlyCollection<ProviderDiagnostic> Diagnostics);

public sealed class EnvironmentApplyExecutor : IApplyProviderExecutor
{
    private readonly EnvironmentStateProvider _provider;

    public EnvironmentApplyExecutor(EnvironmentStateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderId => EnvironmentProfileMapper.ProviderId;
    public bool IsSupported => _provider.IsSupported;
    public Task<RollbackPreparationResult> PrepareRollbackAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.PrepareRollbackAsync(action, context, cancellationToken);
    public Task<ActionExecutionResult> ApplyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.ApplyAsync(action, context, cancellationToken);
    public Task<VerificationResult> VerifyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.VerifyAsync(action, context, cancellationToken);
    public Task<RollbackExecutionResult> RollbackAsync(RollbackAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.RollbackAsync(action, context, cancellationToken);
}

public sealed class WingetApplyExecutor : IApplyProviderExecutor
{
    private readonly WingetPackageProvider _provider;

    public WingetApplyExecutor(WingetPackageProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderId => WingetProfileMapper.ProviderId;
    public bool IsSupported => _provider.IsSupported;
    public Task<RollbackPreparationResult> PrepareRollbackAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.PrepareRollbackAsync(action, context, cancellationToken);
    public Task<ActionExecutionResult> ApplyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.ApplyAsync(action, context, cancellationToken);
    public Task<VerificationResult> VerifyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.VerifyAsync(action, context, cancellationToken);
    public Task<RollbackExecutionResult> RollbackAsync(RollbackAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.RollbackAsync(action, context, cancellationToken);
}

public sealed class WindowsFeatureApplyExecutor : IApplyProviderExecutor
{
    private readonly WindowsFeatureProvider _provider;

    public WindowsFeatureApplyExecutor(WindowsFeatureProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderId => WindowsFeatureProfileMapper.ProviderId;
    public bool IsSupported => _provider.IsSupported;
    public Task<RollbackPreparationResult> PrepareRollbackAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.PrepareRollbackAsync(action, context, cancellationToken);
    public Task<ActionExecutionResult> ApplyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.ApplyAsync(action, context, cancellationToken);
    public Task<VerificationResult> VerifyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.VerifyAsync(action, context, cancellationToken);
    public Task<RollbackExecutionResult> RollbackAsync(RollbackAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.RollbackAsync(action, context, cancellationToken);
}

public sealed class WindowsSystemApplyExecutor : IApplyProviderExecutor
{
    private readonly WindowsSystemProvider _provider;

    public WindowsSystemApplyExecutor(WindowsSystemProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderId => WindowsSystemProfileMapper.ProviderId;
    public bool IsSupported => _provider.IsSupported;
    public Task<RollbackPreparationResult> PrepareRollbackAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.PrepareRollbackAsync(action, context, cancellationToken);
    public Task<ActionExecutionResult> ApplyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.ApplyAsync(action, context, cancellationToken);
    public Task<VerificationResult> VerifyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.VerifyAsync(action, context, cancellationToken);
    public Task<RollbackExecutionResult> RollbackAsync(RollbackAction action, ProviderExecutionContext context, CancellationToken cancellationToken) => _provider.RollbackAsync(action, context, cancellationToken);
}

/// <summary>Собирает Environment, WinGet, Optional Features и Windows System Control в одну транзакцию.</summary>
public sealed class UnifiedApplyWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly WinStateOptions _options;
    private readonly ProfileEngine _profileEngine;
    private readonly ProfileValidator _validator;
    private readonly WindowsSystemProfileLoader _systemProfileLoader;
    private readonly EnvironmentStateProvider _environmentProvider;
    private readonly WingetPackageProvider _wingetProvider;
    private readonly WindowsFeatureProvider _featureProvider;
    private readonly WindowsSystemProvider _systemProvider;
    private readonly ApplyEngine _engine;
    private readonly TransactionHistoryStore _history;

    public UnifiedApplyWorkflow(
        WinStateOptions options,
        ProfileEngine profileEngine,
        ProfileValidator validator,
        WindowsSystemProfileLoader systemProfileLoader,
        EnvironmentStateProvider environmentProvider,
        WingetPackageProvider wingetProvider,
        WindowsFeatureProvider featureProvider,
        WindowsSystemProvider systemProvider,
        ApplyEngine engine,
        TransactionHistoryStore history)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profileEngine = profileEngine ?? throw new ArgumentNullException(nameof(profileEngine));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _systemProfileLoader = systemProfileLoader ?? throw new ArgumentNullException(nameof(systemProfileLoader));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _wingetProvider = wingetProvider ?? throw new ArgumentNullException(nameof(wingetProvider));
        _featureProvider = featureProvider ?? throw new ArgumentNullException(nameof(featureProvider));
        _systemProvider = systemProvider ?? throw new ArgumentNullException(nameof(systemProvider));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public IReadOnlyCollection<string> RegisteredProviders => _engine.RegisteredProviders;

    public async Task<UnifiedApplyPlanReport> PlanAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var loaded = await _profileEngine.LoadAsync(
            profilePath,
            new ProfileLoadOptions(variables, environment),
            cancellationToken);
        var validation = _validator.Validate(loaded.Profile);
        if (!validation.IsValid)
        {
            return new UnifiedApplyPlanReport(
                loaded,
                validation,
                _engine.BuildPlan(loaded.Profile.Metadata.Name, Array.Empty<PlannedAction>()),
                Array.Empty<ProviderDiagnostic>(),
                true);
        }

        var systemProfile = await _systemProfileLoader.LoadAsync(
            profilePath,
            variables,
            environment,
            cancellationToken);
        var actions = new List<PlannedAction>();
        var diagnostics = new List<ProviderDiagnostic>();
        var supported = true;
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(profilePath))
            ?? Environment.CurrentDirectory;
        var providerContext = new ProviderContext(
            loaded.Profile.Metadata.Name,
            false,
            workingDirectory);
        var planningContext = new PlanningContext(
            loaded.Profile.Settings.StrictMode,
            false,
            loaded.Profile.Metadata.Name);

        if (UsesEnvironment(loaded.Profile))
        {
            supported &= await AppendPlanAsync(
                _environmentProvider,
                EnvironmentProfileMapper.CreateDesiredState(loaded.Profile),
                "Environment Provider доступен только в Windows.",
                providerContext,
                planningContext,
                actions,
                diagnostics,
                cancellationToken);
        }

        if (loaded.Profile.Packages.Count > 0)
        {
            supported &= await AppendPlanAsync(
                _wingetProvider,
                WingetProfileMapper.CreateDesiredState(loaded.Profile),
                "WinGet Provider недоступен. Проверьте Windows App Installer.",
                providerContext,
                planningContext,
                actions,
                diagnostics,
                cancellationToken);
        }

        if (loaded.Profile.Features.Count > 0)
        {
            supported &= await AppendPlanAsync(
                _featureProvider,
                WindowsFeatureProfileMapper.CreateDesiredState(loaded.Profile),
                "Windows Features Provider доступен только в Windows.",
                providerContext,
                planningContext,
                actions,
                diagnostics,
                cancellationToken);
        }

        if (systemProfile.HasResources)
        {
            supported &= await AppendPlanAsync(
                _systemProvider,
                WindowsSystemProfileMapper.CreateDesiredState(systemProfile),
                "Windows System Control доступен только в Windows.",
                providerContext,
                planningContext,
                actions,
                diagnostics,
                cancellationToken);
        }

        return new UnifiedApplyPlanReport(
            loaded,
            validation,
            _engine.BuildPlan(loaded.Profile.Metadata.Name, actions),
            diagnostics,
            supported);
    }

    public async Task<ApplyEngineReport> ApplyAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, string?> environment,
        ApplyEngineOptions options,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(profilePath, variables, environment, cancellationToken);
        if (!plan.Validation.IsValid)
        {
            throw new InvalidDataException("Профиль содержит ошибки и не может быть применён.");
        }

        if (!plan.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Один или несколько providers текущего профиля недоступны.");
        }

        var report = await _engine.ExecuteAsync(
            new ApplyEngineRequest(
                plan.Loaded.Profile.Metadata.Name,
                Path.GetDirectoryName(Path.GetFullPath(profilePath)) ?? Environment.CurrentDirectory,
                Path.Combine(_options.HomeDirectory, "backups"),
                plan.Plan.OrderedActions,
                options,
                isElevated),
            cancellationToken);
        await RecordHistoryAsync(report, "unified-apply", cancellationToken);
        return report;
    }

    public async Task<ApplyEngineReport> ResumeAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var report = await _engine.ResumeAsync(manifestPath, cancellationToken);
        await RecordHistoryAsync(report, "resume", cancellationToken);
        return report;
    }

    public async Task<ApplyEngineReport> RollbackAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var report = await _engine.RollbackAsync(manifestPath, cancellationToken);
        await RecordHistoryAsync(report, "unified-rollback", cancellationToken);
        return report;
    }

    public async Task<UnifiedApplyStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var transactions = await _engine.ListTransactionsAsync(
            Path.Combine(_options.HomeDirectory, "backups"),
            cancellationToken);
        var resumable = transactions.Count(transaction =>
            transaction.Status is Domain.Transactions.TransactionStatus.Planned
                or Domain.Transactions.TransactionStatus.Running
                or Domain.Transactions.TransactionStatus.Partial
                or Domain.Transactions.TransactionStatus.Failed
                or Domain.Transactions.TransactionStatus.VerificationFailed
                or Domain.Transactions.TransactionStatus.Cancelled);
        var rebootPending = transactions.Count(transaction =>
            transaction.Status == Domain.Transactions.TransactionStatus.SucceededRebootPending);
        return new UnifiedApplyStatusReport(
            _engine.RegisteredProviders,
            transactions.Count,
            resumable,
            rebootPending,
            transactions.Take(12).ToArray());
    }

    public async Task<SystemProvidersStatusReport> GetProvidersStatusAsync(
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ProviderDiagnostic>();
        var installed = 0;
        var updates = 0;
        var enabled = 0;
        var disabled = 0;
        if (_wingetProvider.IsSupported)
        {
            var discovery = await _wingetProvider.DiscoverAsync(
                new ProviderContext("status", false, Environment.CurrentDirectory),
                cancellationToken);
            installed = discovery.Resources.Count;
            updates = discovery.Resources.Count(resource =>
                resource.Properties.TryGetValue("availableVersion", out var value)
                && !string.IsNullOrWhiteSpace(value.Value));
            diagnostics.AddRange(discovery.Diagnostics);
        }

        if (_featureProvider.IsSupported)
        {
            var discovery = await _featureProvider.DiscoverAsync(
                new ProviderContext("status", false, Environment.CurrentDirectory),
                cancellationToken);
            enabled = discovery.Resources.Count(resource => resource.State == Domain.Configuration.DesiredState.Enabled);
            disabled = discovery.Resources.Count(resource => resource.State == Domain.Configuration.DesiredState.Disabled);
            diagnostics.AddRange(discovery.Diagnostics);
        }

        if (!_systemProvider.IsSupported)
        {
            diagnostics.Add(new ProviderDiagnostic(
                "windows.system.unsupported",
                "Registry, Services, Startup и Scheduled Tasks доступны только в Windows.",
                true));
        }

        return new SystemProvidersStatusReport(
            _environmentProvider.IsSupported,
            _wingetProvider.IsSupported,
            _featureProvider.IsSupported,
            installed,
            updates,
            enabled,
            disabled,
            diagnostics);
    }

    private static async Task<bool> AppendPlanAsync(
        IStateProvider provider,
        DesiredProviderState desiredState,
        string unsupportedMessage,
        ProviderContext providerContext,
        PlanningContext planningContext,
        ICollection<PlannedAction> actions,
        ICollection<ProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var supported = provider switch
        {
            EnvironmentStateProvider environment => environment.IsSupported,
            WingetPackageProvider winget => winget.IsSupported,
            WindowsFeatureProvider features => features.IsSupported,
            WindowsSystemProvider system => system.IsSupported,
            _ => true
        };
        if (!supported)
        {
            diagnostics.Add(new ProviderDiagnostic($"{provider.Id}.unsupported", unsupportedMessage, true));
            return false;
        }

        var discovery = await provider.DiscoverAsync(providerContext, cancellationToken);
        diagnostics.AddRange(discovery.Diagnostics);
        var planned = await provider.PlanAsync(
            desiredState,
            new CurrentProviderState(discovery.Resources),
            planningContext,
            cancellationToken);
        foreach (var action in planned)
        {
            actions.Add(action);
        }

        return !discovery.Diagnostics.Any(diagnostic => !diagnostic.IsWarning);
    }

    private static bool UsesEnvironment(Domain.Profiles.WinStateProfile profile)
        => profile.Environment.User.Count > 0
            || profile.Environment.Machine.Count > 0
            || profile.Environment.UserPath.Count > 0
            || profile.Environment.MachinePath.Count > 0;

    private Task RecordHistoryAsync(
        ApplyEngineReport report,
        string mode,
        CancellationToken cancellationToken)
    {
        if (report.TransactionId == "no-op")
        {
            return Task.CompletedTask;
        }

        var actions = report.Results.Select(result => new StoredTransactionAction(
            result.ActionId,
            result.ProviderId,
            result.Status.ToString(),
            JsonSerializer.Serialize(new
            {
                result.Message,
                result.StartedAt,
                result.CompletedAt
            }, JsonOptions),
            result.BackupReference)).ToArray();
        return _history.RecordAsync(
            new StoredTransaction(
                report.TransactionId,
                report.ProfileId,
                report.StartedAt,
                report.CompletedAt,
                report.Status.ToString(),
                mode,
                report.RebootRequired,
                actions),
            cancellationToken);
    }
}