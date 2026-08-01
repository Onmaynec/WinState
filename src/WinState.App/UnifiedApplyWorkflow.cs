using System.Text.Json;
using WinState.Apply;
using WinState.Core.Profiles;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Infrastructure.Configuration;
using WinState.Providers.EnvironmentVariables;
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

public sealed class EnvironmentApplyExecutor : IApplyProviderExecutor
{
    private readonly EnvironmentStateProvider _provider;

    public EnvironmentApplyExecutor(EnvironmentStateProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderId => EnvironmentProfileMapper.ProviderId;
    public bool IsSupported => _provider.IsSupported;

    public Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
        => _provider.PrepareRollbackAsync(action, context, cancellationToken);

    public Task<ActionExecutionResult> ApplyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
        => _provider.ApplyAsync(action, context, cancellationToken);

    public Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
        => _provider.VerifyAsync(action, context, cancellationToken);

    public Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
        => _provider.RollbackAsync(action, context, cancellationToken);
}

/// <summary>
/// Собирает планы нескольких providers и передаёт их общему Apply Engine.
/// В 0.6 зарегистрирован первый реальный adapter — Environment Provider.
/// </summary>
public sealed class UnifiedApplyWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly WinStateOptions _options;
    private readonly ProfileEngine _profileEngine;
    private readonly ProfileValidator _validator;
    private readonly EnvironmentStateProvider _environmentProvider;
    private readonly ApplyEngine _engine;
    private readonly TransactionHistoryStore _history;

    public UnifiedApplyWorkflow(
        WinStateOptions options,
        ProfileEngine profileEngine,
        ProfileValidator validator,
        EnvironmentStateProvider environmentProvider,
        ApplyEngine engine,
        TransactionHistoryStore history)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profileEngine = profileEngine ?? throw new ArgumentNullException(nameof(profileEngine));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _environmentProvider = environmentProvider
            ?? throw new ArgumentNullException(nameof(environmentProvider));
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
        if (!validation.IsValid || !_environmentProvider.IsSupported)
        {
            return new UnifiedApplyPlanReport(
                loaded,
                validation,
                _engine.BuildPlan(
                    loaded.Profile.Metadata.Name,
                    Array.Empty<PlannedAction>()),
                _environmentProvider.IsSupported
                    ? Array.Empty<ProviderDiagnostic>()
                    : [new ProviderDiagnostic(
                        "apply.environment.unsupported",
                        "Environment adapter доступен только в Windows.",
                        true)],
                _environmentProvider.IsSupported);
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(profilePath))
            ?? Environment.CurrentDirectory;
        var discovery = await _environmentProvider.DiscoverAsync(
            new ProviderContext(
                loaded.Profile.Metadata.Name,
                false,
                workingDirectory),
            cancellationToken);
        var environmentActions = await _environmentProvider.PlanAsync(
            EnvironmentProfileMapper.CreateDesiredState(loaded.Profile),
            new CurrentProviderState(discovery.Resources),
            new PlanningContext(
                loaded.Profile.Settings.StrictMode,
                false,
                loaded.Profile.Metadata.Name),
            cancellationToken);
        return new UnifiedApplyPlanReport(
            loaded,
            validation,
            _engine.BuildPlan(loaded.Profile.Metadata.Name, environmentActions),
            discovery.Diagnostics,
            true);
    }

    public async Task<ApplyEngineReport> ApplyAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, string?> environment,
        ApplyEngineOptions options,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(
            profilePath,
            variables,
            environment,
            cancellationToken);
        if (!plan.Validation.IsValid)
        {
            throw new InvalidDataException("Профиль содержит ошибки и не может быть применён.");
        }

        if (!plan.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Ни один системный provider текущего профиля не поддерживается на этой платформе.");
        }

        var report = await _engine.ExecuteAsync(
            new ApplyEngineRequest(
                plan.Loaded.Profile.Metadata.Name,
                Path.GetDirectoryName(Path.GetFullPath(profilePath))
                    ?? Environment.CurrentDirectory,
                Path.Combine(_options.HomeDirectory, "backups"),
                plan.Plan.OrderedActions,
                options,
                isElevated),
            cancellationToken);
        await RecordHistoryAsync(report, "unified-apply", cancellationToken);
        return report;
    }

    public async Task<ApplyEngineReport> ResumeAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var report = await _engine.ResumeAsync(manifestPath, cancellationToken);
        await RecordHistoryAsync(report, "resume", cancellationToken);
        return report;
    }

    public async Task<ApplyEngineReport> RollbackAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var report = await _engine.RollbackAsync(manifestPath, cancellationToken);
        await RecordHistoryAsync(report, "unified-rollback", cancellationToken);
        return report;
    }

    public async Task<UnifiedApplyStatusReport> GetStatusAsync(
        CancellationToken cancellationToken)
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
