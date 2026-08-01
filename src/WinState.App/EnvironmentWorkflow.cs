using System.Text.Json;
using WinState.Core.Planning;
using WinState.Core.Profiles;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Infrastructure.Configuration;
using WinState.Providers.EnvironmentVariables;
using WinState.Storage;

namespace WinState.App;

public sealed record EnvironmentStatusReport(
    bool IsSupported,
    int UserVariables,
    int MachineVariables,
    int UserPathEntries,
    int MachinePathEntries,
    IReadOnlyCollection<ProviderDiagnostic> Diagnostics);

public sealed record EnvironmentPlanReport(
    LoadedProfile Loaded,
    ProfileValidationResult Validation,
    IReadOnlyList<PlannedAction> Actions,
    PlanSummary Summary,
    IReadOnlyCollection<ProviderDiagnostic> Diagnostics,
    bool IsSupported);

public sealed record EnvironmentActionReport(
    string ActionId,
    ActionStatus Status,
    string Message,
    string? BackupReference);

public sealed record EnvironmentExecutionReport(
    string TransactionId,
    string ProfileName,
    bool Succeeded,
    bool Verified,
    bool RolledBack,
    string? CheckpointManifest,
    IReadOnlyList<EnvironmentActionReport> Actions,
    string Message);

public sealed record EnvironmentCheckpointEntry(
    string TransactionId,
    string ProfileName,
    DateTimeOffset CreatedAt,
    string ManifestPath,
    int ActionCount,
    string Status);

public sealed record EnvironmentCheckpointAction(
    string ActionId,
    string ProviderId,
    string BackupReference);

public sealed record EnvironmentCheckpointManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string TransactionId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public string ProfilePath { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string Status { get; init; } = "prepared";
    public IReadOnlyList<EnvironmentCheckpointAction> Actions { get; init; }
        = Array.Empty<EnvironmentCheckpointAction>();
}

/// <summary>Оркестрирует plan, checkpoint, apply, verify, SQLite history и rollback.</summary>
public sealed class EnvironmentWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly WinStateOptions _options;
    private readonly ProfileEngine _profileEngine;
    private readonly ProfileValidator _validator;
    private readonly DependencyGraph _dependencyGraph;
    private readonly EnvironmentStateProvider _provider;
    private readonly TransactionHistoryStore _history;

    public EnvironmentWorkflow(
        WinStateOptions options,
        ProfileEngine profileEngine,
        ProfileValidator validator,
        DependencyGraph dependencyGraph,
        EnvironmentStateProvider provider,
        TransactionHistoryStore history)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profileEngine = profileEngine ?? throw new ArgumentNullException(nameof(profileEngine));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _dependencyGraph = dependencyGraph ?? throw new ArgumentNullException(nameof(dependencyGraph));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public bool IsSupported => _provider.IsSupported;

    public async Task<EnvironmentStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!_provider.IsSupported)
        {
            return new EnvironmentStatusReport(
                false,
                0,
                0,
                0,
                0,
                [new ProviderDiagnostic(
                    "environment.platform.unsupported",
                    "Environment Provider доступен только в Windows.",
                    true)]);
        }

        var discovery = await _provider.DiscoverAsync(
            new ProviderContext("status", false, Environment.CurrentDirectory),
            cancellationToken);
        var userVariables = Count(discovery, EnvironmentProfileMapper.VariableResourceType, "user");
        var machineVariables = Count(discovery, EnvironmentProfileMapper.VariableResourceType, "machine");
        var userPath = Count(discovery, EnvironmentProfileMapper.PathResourceType, "user");
        var machinePath = Count(discovery, EnvironmentProfileMapper.PathResourceType, "machine");
        return new EnvironmentStatusReport(
            true,
            userVariables,
            machineVariables,
            userPath,
            machinePath,
            discovery.Diagnostics);
    }

    public async Task<EnvironmentPlanReport> PlanAsync(
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
        if (!validation.IsValid || !_provider.IsSupported)
        {
            return new EnvironmentPlanReport(
                loaded,
                validation,
                Array.Empty<PlannedAction>(),
                PlanSummary.From(Array.Empty<PlannedAction>()),
                _provider.IsSupported
                    ? Array.Empty<ProviderDiagnostic>()
                    : [new ProviderDiagnostic(
                        "environment.platform.unsupported",
                        "Environment Provider доступен только в Windows.",
                        true)],
                _provider.IsSupported);
        }

        var discovery = await _provider.DiscoverAsync(
            new ProviderContext(
                loaded.Profile.Metadata.Name,
                false,
                Path.GetDirectoryName(profilePath) ?? Environment.CurrentDirectory),
            cancellationToken);
        var planned = await _provider.PlanAsync(
            EnvironmentProfileMapper.CreateDesiredState(loaded.Profile),
            new CurrentProviderState(discovery.Resources),
            new PlanningContext(
                loaded.Profile.Settings.StrictMode,
                false,
                loaded.Profile.Metadata.Name),
            cancellationToken);
        var ordered = _dependencyGraph.Sort(planned);
        return new EnvironmentPlanReport(
            loaded,
            validation,
            ordered,
            PlanSummary.From(ordered),
            discovery.Diagnostics,
            true);
    }

    public async Task<EnvironmentExecutionReport> ApplyAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, string?> environment,
        bool allowMachineScope,
        bool automaticRollback,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var plan = await PlanAsync(profilePath, variables, environment, cancellationToken);
        if (!plan.Validation.IsValid)
        {
            throw new InvalidDataException("Профиль содержит ошибки и не может быть применён.");
        }

        if (!plan.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Environment Provider изменяет User/Machine environment только в Windows.");
        }

        if (!allowMachineScope && plan.Actions.Any(action => action.RequiresAdministrator))
        {
            throw new InvalidOperationException(
                "План содержит Machine scope. Требуется отдельное подтверждение и запуск с правами администратора.");
        }

        if (plan.Actions.Count == 0)
        {
            return new EnvironmentExecutionReport(
                "no-op",
                plan.Loaded.Profile.Metadata.Name,
                true,
                true,
                false,
                null,
                Array.Empty<EnvironmentActionReport>(),
                "Система уже соответствует environment-секции профиля.");
        }

        var transactionId = $"env-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(
            _options.HomeDirectory,
            "backups",
            EnvironmentProfileMapper.ProviderId,
            transactionId);
        Directory.CreateDirectory(backupDirectory);
        var executionContext = new ProviderExecutionContext(
            transactionId,
            false,
            backupDirectory);
        var prepared = new List<EnvironmentCheckpointAction>();
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = await _provider.PrepareRollbackAsync(
                action,
                executionContext,
                cancellationToken);
            if (!checkpoint.IsSupported || string.IsNullOrWhiteSpace(checkpoint.BackupReference))
            {
                throw new InvalidDataException(
                    $"Не удалось создать checkpoint для действия {action.Id}: {checkpoint.Message}");
            }

            prepared.Add(new EnvironmentCheckpointAction(
                action.Id,
                action.ProviderId,
                checkpoint.BackupReference));
        }

        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        var manifest = new EnvironmentCheckpointManifest
        {
            TransactionId = transactionId,
            ProfileName = plan.Loaded.Profile.Metadata.Name,
            ProfilePath = Path.GetFullPath(profilePath),
            CreatedAt = startedAt,
            Actions = prepared
        };
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);

        var reports = new List<EnvironmentActionReport>();
        var applied = new List<EnvironmentCheckpointAction>();
        var succeeded = true;
        var verified = true;
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backup = prepared.Single(item => item.ActionId == action.Id);
            var appliedResult = await _provider.ApplyAsync(action, executionContext, cancellationToken);
            if (appliedResult.Status != ActionStatus.Succeeded)
            {
                reports.Add(new EnvironmentActionReport(
                    action.Id,
                    appliedResult.Status,
                    appliedResult.Message,
                    backup.BackupReference));
                succeeded = false;
                verified = false;
                break;
            }

            applied.Add(backup);
            var verification = await _provider.VerifyAsync(
                action,
                executionContext,
                cancellationToken);
            if (!verification.IsMatch)
            {
                reports.Add(new EnvironmentActionReport(
                    action.Id,
                    ActionStatus.VerificationFailed,
                    verification.Message,
                    backup.BackupReference));
                succeeded = false;
                verified = false;
                break;
            }

            reports.Add(new EnvironmentActionReport(
                action.Id,
                ActionStatus.Succeeded,
                verification.Message,
                backup.BackupReference));
        }

        var rolledBack = false;
        if (!succeeded && automaticRollback)
        {
            rolledBack = await RollbackActionsAsync(
                applied,
                executionContext,
                reports,
                cancellationToken);
        }

        var finalStatus = succeeded
            ? "succeeded"
            : rolledBack
                ? "rolled-back"
                : "failed";
        manifest = manifest with { Status = finalStatus };
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        await RecordHistoryAsync(
            transactionId,
            plan.Loaded.Profile.Metadata.Name,
            startedAt,
            finalStatus,
            "apply",
            reports,
            cancellationToken);

        return new EnvironmentExecutionReport(
            transactionId,
            plan.Loaded.Profile.Metadata.Name,
            succeeded,
            verified,
            rolledBack,
            manifestPath,
            reports,
            succeeded
                ? "Environment plan применён и проверен."
                : rolledBack
                    ? "Применение завершилось ошибкой; выполнен автоматический rollback."
                    : "Применение завершилось ошибкой; rollback выполнен не полностью.");
    }

    public async Task<IReadOnlyList<EnvironmentCheckpointEntry>> ListCheckpointsAsync(
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            _options.HomeDirectory,
            "backups",
            EnvironmentProfileMapper.ProviderId);
        if (!Directory.Exists(root))
        {
            return Array.Empty<EnvironmentCheckpointEntry>();
        }

        var result = new List<EnvironmentCheckpointEntry>();
        foreach (var manifestPath in Directory.EnumerateFiles(
            root,
            "manifest.json",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
                result.Add(new EnvironmentCheckpointEntry(
                    manifest.TransactionId,
                    manifest.ProfileName,
                    manifest.CreatedAt,
                    manifestPath,
                    manifest.Actions.Count,
                    manifest.Status));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                _ = exception;
            }
        }

        return result
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.TransactionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<EnvironmentExecutionReport> RollbackAsync(
        string checkpointPath,
        CancellationToken cancellationToken)
    {
        if (!_provider.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Environment Provider изменяет User/Machine environment только в Windows.");
        }

        var manifestPath = Directory.Exists(checkpointPath)
            ? Path.Combine(checkpointPath, "manifest.json")
            : checkpointPath;
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var executionContext = new ProviderExecutionContext(
            $"rollback-{manifest.TransactionId}",
            false,
            Path.GetDirectoryName(manifestPath) ?? _options.HomeDirectory);
        var reports = new List<EnvironmentActionReport>();
        var succeeded = true;
        foreach (var item in manifest.Actions.Reverse())
        {
            var result = await _provider.RollbackAsync(
                new RollbackAction(item.ActionId, item.ProviderId, item.BackupReference),
                executionContext,
                cancellationToken);
            reports.Add(new EnvironmentActionReport(
                item.ActionId,
                result.Succeeded ? ActionStatus.RolledBack : ActionStatus.RollbackFailed,
                result.Message,
                item.BackupReference));
            succeeded &= result.Succeeded;
        }

        var status = succeeded ? "rolled-back" : "rollback-failed";
        manifest = manifest with { Status = status };
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        await RecordHistoryAsync(
            executionContext.TransactionId,
            manifest.ProfileName,
            startedAt,
            status,
            "rollback",
            reports,
            cancellationToken);
        return new EnvironmentExecutionReport(
            executionContext.TransactionId,
            manifest.ProfileName,
            succeeded,
            succeeded,
            succeeded,
            manifestPath,
            reports,
            succeeded
                ? "Checkpoint успешно восстановлен."
                : "Часть действий checkpoint не удалось восстановить.");
    }

    private async Task<bool> RollbackActionsAsync(
        IReadOnlyCollection<EnvironmentCheckpointAction> applied,
        ProviderExecutionContext context,
        ICollection<EnvironmentActionReport> reports,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var item in applied.Reverse())
        {
            var result = await _provider.RollbackAsync(
                new RollbackAction(item.ActionId, item.ProviderId, item.BackupReference),
                context,
                cancellationToken);
            reports.Add(new EnvironmentActionReport(
                item.ActionId,
                result.Succeeded ? ActionStatus.RolledBack : ActionStatus.RollbackFailed,
                result.Message,
                item.BackupReference));
            succeeded &= result.Succeeded;
        }

        return succeeded;
    }

    private async Task RecordHistoryAsync(
        string transactionId,
        string profileName,
        DateTimeOffset startedAt,
        string status,
        string mode,
        IReadOnlyCollection<EnvironmentActionReport> reports,
        CancellationToken cancellationToken)
    {
        var actions = reports.Select(report => new StoredTransactionAction(
            report.ActionId,
            EnvironmentProfileMapper.ProviderId,
            report.Status.ToString(),
            JsonSerializer.Serialize(new { report.Message }, JsonOptions),
            report.BackupReference)).ToArray();
        await _history.RecordAsync(
            new StoredTransaction(
                transactionId,
                profileName,
                startedAt,
                DateTimeOffset.UtcNow,
                status,
                mode,
                false,
                actions),
            cancellationToken);
    }

    private static int Count(
        ProviderDiscoveryResult discovery,
        string resourceType,
        string scope)
        => discovery.Resources.Count(resource =>
            resource.ResourceType == resourceType
            && resource.Properties.TryGetValue("scope", out var value)
            && string.Equals(value.Value, scope, StringComparison.OrdinalIgnoreCase));

    private static async Task WriteManifestAsync(
        string path,
        EnvironmentCheckpointManifest manifest,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);
    }

    private static async Task<EnvironmentCheckpointManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Checkpoint manifest не найден.", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<EnvironmentCheckpointManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Checkpoint manifest повреждён.");
    }
}
