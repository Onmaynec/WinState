using System.Text.Json;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Domain.Transactions;

namespace WinState.Apply;

public interface IApplyProviderExecutor
{
    string ProviderId { get; }
    bool IsSupported { get; }

    Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken);

    Task<ActionExecutionResult> ApplyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken);

    Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken);

    Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record ApplyEngineOptions
{
    public bool AutomaticRollback { get; init; } = true;
    public bool AllowAdministrator { get; init; }
    public bool AllowCritical { get; init; }
    public bool AllowIrreversible { get; init; }
    public bool AllowReboot { get; init; }
}

public sealed record ApplyEngineRequest(
    string ProfileId,
    string WorkingDirectory,
    string BackupRoot,
    IReadOnlyCollection<PlannedAction> Actions,
    ApplyEngineOptions Options,
    bool IsElevated = false);

public sealed record ApplyRiskGroup(
    RiskLevel Risk,
    int Actions,
    int AdministratorActions,
    int IrreversibleActions,
    int RebootActions);

public sealed record UnifiedApplyPlan(
    string ProfileId,
    IReadOnlyList<PlannedAction> OrderedActions,
    IReadOnlyList<ApplyRiskGroup> RiskGroups,
    IReadOnlyList<string> Providers,
    bool RequiresAdministrator,
    bool RequiresReboot,
    bool ContainsIrreversible,
    RiskLevel MaximumRisk);

public sealed record ApplyCheckpoint(
    string ActionId,
    string ProviderId,
    string BackupReference);

public sealed record ApplyEngineActionResult(
    string ActionId,
    string ProviderId,
    ActionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Message,
    string? BackupReference);

public sealed record ApplyEngineReport(
    string TransactionId,
    string ProfileId,
    TransactionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    bool Verified,
    bool RolledBack,
    bool RebootRequired,
    string ManifestPath,
    IReadOnlyList<ApplyEngineActionResult> Results,
    string Message);

public sealed record ApplyTransactionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string TransactionId { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public TransactionStatus Status { get; init; } = TransactionStatus.Planned;
    public bool IsElevated { get; init; }
    public bool RebootRequired { get; init; }
    public ApplyEngineOptions Options { get; init; } = new();
    public IReadOnlyList<PlannedAction> Plan { get; init; } = Array.Empty<PlannedAction>();
    public IReadOnlyList<ApplyCheckpoint> Checkpoints { get; init; } = Array.Empty<ApplyCheckpoint>();
    public IReadOnlyList<ApplyEngineActionResult> Results { get; init; } = Array.Empty<ApplyEngineActionResult>();
}

/// <summary>
/// Общий transaction engine: execution graph, checkpoints, apply, verification,
/// resume, reboot-pending state и cross-provider rollback.
/// </summary>
public sealed class ApplyEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, IApplyProviderExecutor> _executors;

    public ApplyEngine(IEnumerable<IApplyProviderExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors.ToDictionary(
            executor => executor.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> RegisteredProviders
        => _executors.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    public UnifiedApplyPlan BuildPlan(string profileId, IEnumerable<PlannedAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(actions);

        var ordered = Sort(actions);
        var providers = ordered
            .Select(action => action.ProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = ordered
            .GroupBy(action => action.Risk)
            .OrderBy(group => group.Key)
            .Select(group => new ApplyRiskGroup(
                group.Key,
                group.Count(),
                group.Count(action => action.RequiresAdministrator),
                group.Count(action => !action.SupportsRollback),
                group.Count(action => action.MayRequireReboot)))
            .ToArray();

        return new UnifiedApplyPlan(
            profileId,
            ordered,
            groups,
            providers,
            ordered.Any(action => action.RequiresAdministrator),
            ordered.Any(action => action.MayRequireReboot),
            ordered.Any(action => !action.SupportsRollback),
            ordered.Count == 0 ? RiskLevel.None : ordered.Max(action => action.Risk));
    }

    public async Task<ApplyEngineReport> ExecuteAsync(
        ApplyEngineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = BuildPlan(request.ProfileId, request.Actions);
        ValidatePolicy(plan, request.Options);
        ValidateProviders(plan.OrderedActions);

        if (plan.OrderedActions.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            return new ApplyEngineReport(
                "no-op",
                request.ProfileId,
                TransactionStatus.Succeeded,
                now,
                now,
                true,
                true,
                false,
                false,
                string.Empty,
                Array.Empty<ApplyEngineActionResult>(),
                "Execution graph пуст: система уже соответствует профилю.");
        }

        var transactionId = $"txn-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var transactionDirectory = Path.Combine(request.BackupRoot, "transactions", transactionId);
        Directory.CreateDirectory(transactionDirectory);
        var manifestPath = Path.Combine(transactionDirectory, "transaction.json");
        var startedAt = DateTimeOffset.UtcNow;
        var checkpoints = await PrepareCheckpointsAsync(
            transactionId,
            transactionDirectory,
            plan.OrderedActions,
            request.IsElevated,
            cancellationToken);

        var manifest = new ApplyTransactionManifest
        {
            TransactionId = transactionId,
            ProfileId = request.ProfileId,
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            StartedAt = startedAt,
            Status = TransactionStatus.Planned,
            IsElevated = request.IsElevated,
            Options = request.Options,
            Plan = plan.OrderedActions,
            Checkpoints = checkpoints
        };
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        return await ContinueAsync(manifestPath, manifest, cancellationToken);
    }

    public async Task<ApplyEngineReport> ResumeAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        if (manifest.Status is TransactionStatus.Succeeded
            or TransactionStatus.SucceededRebootPending
            or TransactionStatus.RolledBack
            or TransactionStatus.RollbackFailed)
        {
            throw new InvalidOperationException(
                $"Транзакция {manifest.TransactionId} уже завершена со статусом {manifest.Status}.");
        }

        ValidatePolicy(BuildPlan(manifest.ProfileId, manifest.Plan), manifest.Options);
        ValidateProviders(manifest.Plan);
        return await ContinueAsync(manifestPath, manifest, cancellationToken);
    }

    public async Task<ApplyEngineReport> RollbackAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        ValidateProviders(manifest.Plan);
        var applied = manifest.Results
            .Where(result => result.Status == ActionStatus.Succeeded)
            .Select(result => result.ActionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = manifest.Results.ToList();
        var rolledBack = await RollbackAppliedAsync(
            manifest,
            applied,
            results,
            cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        var status = rolledBack
            ? TransactionStatus.RolledBack
            : TransactionStatus.RollbackFailed;
        manifest = manifest with
        {
            Status = status,
            CompletedAt = completedAt,
            Results = results
        };
        await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
        return CreateReport(
            manifestPath,
            manifest,
            rolledBack,
            rolledBack,
            rolledBack,
            rolledBack
                ? "Cross-provider rollback завершён."
                : "Cross-provider rollback выполнен не полностью.");
    }

    public async Task<IReadOnlyList<ApplyTransactionManifest>> ListTransactionsAsync(
        string backupRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(backupRoot, "transactions");
        if (!Directory.Exists(root))
        {
            return Array.Empty<ApplyTransactionManifest>();
        }

        var transactions = new List<ApplyTransactionManifest>();
        foreach (var path in Directory.EnumerateFiles(root, "transaction.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                transactions.Add(await ReadManifestAsync(path, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                _ = exception;
            }
        }

        return transactions
            .OrderByDescending(transaction => transaction.StartedAt)
            .ThenBy(transaction => transaction.TransactionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ApplyEngineReport> ContinueAsync(
        string manifestPath,
        ApplyTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        var completed = manifest.Results
            .Where(result => result.Status == ActionStatus.Succeeded)
            .Select(result => result.ActionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = manifest.Results.ToList();
        var succeeded = true;
        var verified = true;
        var rolledBack = false;
        var rebootRequired = manifest.RebootRequired;

        manifest = manifest with { Status = TransactionStatus.Running };
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);

        try
        {
            foreach (var action in manifest.Plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed.Contains(action.Id))
                {
                    continue;
                }

                var checkpoint = manifest.Checkpoints.SingleOrDefault(item =>
                    item.ActionId.Equals(action.Id, StringComparison.OrdinalIgnoreCase));
                var executor = GetExecutor(action.ProviderId);
                var context = CreateContext(
                    manifest.TransactionId,
                    Path.GetDirectoryName(manifestPath)!,
                    action.ProviderId,
                    manifest.IsElevated);
                var actionStartedAt = DateTimeOffset.UtcNow;
                var applyResult = await executor.ApplyAsync(action, context, cancellationToken);
                if (applyResult.Status != ActionStatus.Succeeded)
                {
                    results.Add(new ApplyEngineActionResult(
                        action.Id,
                        action.ProviderId,
                        applyResult.Status,
                        actionStartedAt,
                        DateTimeOffset.UtcNow,
                        applyResult.Message,
                        checkpoint?.BackupReference));
                    succeeded = false;
                    verified = false;
                    break;
                }

                var verification = await executor.VerifyAsync(action, context, cancellationToken);
                if (!verification.IsMatch)
                {
                    results.Add(new ApplyEngineActionResult(
                        action.Id,
                        action.ProviderId,
                        ActionStatus.VerificationFailed,
                        actionStartedAt,
                        DateTimeOffset.UtcNow,
                        verification.Message,
                        checkpoint?.BackupReference));
                    succeeded = false;
                    verified = false;
                    break;
                }

                results.Add(new ApplyEngineActionResult(
                    action.Id,
                    action.ProviderId,
                    ActionStatus.Succeeded,
                    actionStartedAt,
                    DateTimeOffset.UtcNow,
                    verification.Message,
                    checkpoint?.BackupReference));
                completed.Add(action.Id);
                rebootRequired |= action.MayRequireReboot;
                manifest = manifest with
                {
                    Results = results,
                    RebootRequired = rebootRequired
                };
                await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            succeeded = false;
            verified = false;
            if (manifest.Options.AutomaticRollback)
            {
                rolledBack = await RollbackAppliedAsync(
                    manifest with { Results = results },
                    completed,
                    results,
                    CancellationToken.None);
            }

            var cancelledManifest = manifest with
            {
                Status = rolledBack ? TransactionStatus.RolledBack : TransactionStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                Results = results,
                RebootRequired = rebootRequired
            };
            await WriteManifestAsync(manifestPath, cancelledManifest, CancellationToken.None);
            throw;
        }

        if (!succeeded && manifest.Options.AutomaticRollback)
        {
            rolledBack = await RollbackAppliedAsync(
                manifest with { Results = results },
                completed,
                results,
                CancellationToken.None);
        }

        var completedAt = DateTimeOffset.UtcNow;
        var status = succeeded
            ? rebootRequired && !manifest.Options.AllowReboot
                ? TransactionStatus.SucceededRebootPending
                : TransactionStatus.Succeeded
            : rolledBack
                ? TransactionStatus.RolledBack
                : results.Any(result => result.Status == ActionStatus.VerificationFailed)
                    ? TransactionStatus.VerificationFailed
                    : TransactionStatus.Failed;
        manifest = manifest with
        {
            Status = status,
            CompletedAt = completedAt,
            Results = results,
            RebootRequired = rebootRequired
        };
        await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);

        return CreateReport(
            manifestPath,
            manifest,
            succeeded,
            verified,
            rolledBack,
            succeeded
                ? rebootRequired && !manifest.Options.AllowReboot
                    ? "Execution graph применён и проверен; требуется перезагрузка."
                    : "Execution graph применён и проверен."
                : rolledBack
                    ? "Execution graph завершился ошибкой; выполнен cross-provider rollback."
                    : "Execution graph завершился ошибкой; rollback выполнен не полностью.");
    }

    private async Task<IReadOnlyList<ApplyCheckpoint>> PrepareCheckpointsAsync(
        string transactionId,
        string transactionDirectory,
        IReadOnlyCollection<PlannedAction> actions,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        var checkpoints = new List<ApplyCheckpoint>();
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!action.SupportsRollback)
            {
                continue;
            }

            var executor = GetExecutor(action.ProviderId);
            var context = CreateContext(
                transactionId,
                transactionDirectory,
                action.ProviderId,
                isElevated);
            var preparation = await executor.PrepareRollbackAsync(action, context, cancellationToken);
            if (!preparation.IsSupported || string.IsNullOrWhiteSpace(preparation.BackupReference))
            {
                throw new InvalidDataException(
                    $"Provider {action.ProviderId} не создал checkpoint для {action.Id}: {preparation.Message}");
            }

            checkpoints.Add(new ApplyCheckpoint(
                action.Id,
                action.ProviderId,
                preparation.BackupReference));
        }

        return checkpoints;
    }

    private async Task<bool> RollbackAppliedAsync(
        ApplyTransactionManifest manifest,
        IReadOnlySet<string> applied,
        ICollection<ApplyEngineActionResult> results,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var action in manifest.Plan.Reverse())
        {
            if (!applied.Contains(action.Id))
            {
                continue;
            }

            var checkpoint = manifest.Checkpoints.SingleOrDefault(item =>
                item.ActionId.Equals(action.Id, StringComparison.OrdinalIgnoreCase));
            if (checkpoint is null)
            {
                succeeded = false;
                results.Add(new ApplyEngineActionResult(
                    action.Id,
                    action.ProviderId,
                    ActionStatus.RollbackFailed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    "Checkpoint отсутствует.",
                    null));
                continue;
            }

            var startedAt = DateTimeOffset.UtcNow;
            var context = CreateContext(
                manifest.TransactionId,
                Path.GetDirectoryName(Path.GetFullPath(checkpoint.BackupReference))
                    ?? manifest.WorkingDirectory,
                action.ProviderId,
                manifest.IsElevated);
            var rollback = await GetExecutor(action.ProviderId).RollbackAsync(
                new RollbackAction(action.Id, action.ProviderId, checkpoint.BackupReference),
                context,
                cancellationToken);
            results.Add(new ApplyEngineActionResult(
                action.Id,
                action.ProviderId,
                rollback.Succeeded ? ActionStatus.RolledBack : ActionStatus.RollbackFailed,
                startedAt,
                DateTimeOffset.UtcNow,
                rollback.Message,
                checkpoint.BackupReference));
            succeeded &= rollback.Succeeded;
        }

        return succeeded;
    }

    private static ProviderExecutionContext CreateContext(
        string transactionId,
        string transactionDirectory,
        string providerId,
        bool isElevated)
    {
        var safeProviderId = string.Concat(providerId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var providerDirectory = Path.Combine(transactionDirectory, "providers", safeProviderId);
        Directory.CreateDirectory(providerDirectory);
        return new ProviderExecutionContext(
            transactionId,
            isElevated,
            providerDirectory);
    }

    private IApplyProviderExecutor GetExecutor(string providerId)
        => _executors.TryGetValue(providerId, out var executor)
            ? executor
            : throw new InvalidOperationException($"Provider executor не зарегистрирован: {providerId}.");

    private void ValidateProviders(IEnumerable<PlannedAction> actions)
    {
        foreach (var providerId in actions
            .Select(action => action.ProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var executor = GetExecutor(providerId);
            if (!executor.IsSupported)
            {
                throw new PlatformNotSupportedException(
                    $"Provider {providerId} недоступен на текущей платформе.");
            }
        }
    }

    private static void ValidatePolicy(UnifiedApplyPlan plan, ApplyEngineOptions options)
    {
        if (plan.RequiresAdministrator && !options.AllowAdministrator)
        {
            throw new InvalidOperationException(
                "Execution graph содержит elevated actions. Требуется отдельное подтверждение.");
        }

        if (plan.MaximumRisk >= RiskLevel.Critical && !options.AllowCritical)
        {
            throw new InvalidOperationException(
                "Execution graph содержит Critical actions. Требуется отдельное разрешение.");
        }

        if (plan.ContainsIrreversible && !options.AllowIrreversible)
        {
            throw new InvalidOperationException(
                "Execution graph содержит действия без rollback. Требуется отдельное разрешение.");
        }
    }

    private static IReadOnlyList<PlannedAction> Sort(IEnumerable<PlannedAction> actions)
    {
        var items = actions.ToArray();
        var byId = new Dictionary<string, PlannedAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in items)
        {
            if (!byId.TryAdd(action.Id, action))
            {
                throw new InvalidDataException($"Дублирующийся action id: {action.Id}.");
            }
        }

        foreach (var action in items)
        {
            foreach (var dependency in action.DependsOn)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw new InvalidDataException(
                        $"Action {action.Id} зависит от отсутствующего action {dependency}.");
                }
            }
        }

        var inDegree = items.ToDictionary(
            action => action.Id,
            action => action.DependsOn.Count,
            StringComparer.OrdinalIgnoreCase);
        var dependents = items.ToDictionary(
            action => action.Id,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var action in items)
        {
            foreach (var dependency in action.DependsOn)
            {
                dependents[dependency].Add(action.Id);
            }
        }

        var ready = new SortedSet<string>(
            inDegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PlannedAction>(items.Length);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (var dependent in dependents[id].OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != items.Length)
        {
            var cycle = inDegree
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            throw new InvalidDataException(
                $"Execution graph содержит цикл: {string.Join(" → ", cycle)}.");
        }

        return ordered;
    }

    private static ApplyEngineReport CreateReport(
        string manifestPath,
        ApplyTransactionManifest manifest,
        bool succeeded,
        bool verified,
        bool rolledBack,
        string message)
        => new(
            manifest.TransactionId,
            manifest.ProfileId,
            manifest.Status,
            manifest.StartedAt,
            manifest.CompletedAt ?? DateTimeOffset.UtcNow,
            succeeded,
            verified,
            rolledBack,
            manifest.RebootRequired,
            manifestPath,
            manifest.Results,
            message);

    private static async Task WriteManifestAsync(
        string path,
        ApplyTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("Manifest path не содержит каталог.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static async Task<ApplyTransactionManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Transaction manifest не найден.", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<ApplyTransactionManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Transaction manifest повреждён.");
    }
}
