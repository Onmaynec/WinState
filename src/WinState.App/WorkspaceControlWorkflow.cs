using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinState.App;

/// <summary>
/// Управляет пользовательским developer workspace: Git config, PowerShell modules,
/// файлами и каталогами. Удаления разрешены только для ресурсов из ownership ledger.
/// </summary>
public sealed class WorkspaceControlWorkflow
{
    private const int ManifestSchemaVersion = 1;
    private const int OwnershipSchemaVersion = 1;
    private const int TransactionSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _homeDirectory;
    private readonly string _ownershipPath;
    private readonly IGitConfigurationClient _git;
    private readonly IPowerShellModuleClient _modules;

    public WorkspaceControlWorkflow(
        string homeDirectory,
        IGitConfigurationClient? git = null,
        IPowerShellModuleClient? modules = null)
    {
        _homeDirectory = Path.GetFullPath(homeDirectory ?? throw new ArgumentNullException(nameof(homeDirectory)));
        _ownershipPath = Path.Combine(_homeDirectory, "ownership", "workspace.json");
        _git = git ?? new ProcessGitConfigurationClient();
        _modules = modules ?? new ProcessPowerShellModuleClient();
    }

    public async Task<WorkspaceValidationReport> ValidateAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadManifestAsync(manifestPath, cancellationToken);
        var issues = ValidateManifest(loaded.Manifest, loaded.FullPath);
        return new WorkspaceValidationReport(
            loaded.FullPath,
            loaded.Manifest.Name,
            issues.Count == 0,
            issues);
    }

    public async Task<WorkspacePlanReport> PlanAsync(
        string manifestPath,
        string? reportDirectory,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadManifestAsync(manifestPath, cancellationToken);
        var issues = ValidateManifest(loaded.Manifest, loaded.FullPath);
        var diagnostics = new List<string>(issues);
        var ownership = await LoadOwnershipAsync(cancellationToken);
        var actions = new List<WorkspaceAction>();
        var supported = true;

        if (loaded.Manifest.Git.Count > 0 && !_git.IsSupported)
        {
            supported = false;
            diagnostics.Add("git executable не найден; Git configuration provider недоступен.");
        }

        if (loaded.Manifest.PowerShellModules.Count > 0 && !_modules.IsSupported)
        {
            supported = false;
            diagnostics.Add("PowerShell не найден; PowerShell modules provider недоступен.");
        }

        if (issues.Count == 0)
        {
            await PlanGitAsync(loaded.Manifest, ownership, actions, cancellationToken);
            await PlanModulesAsync(loaded.Manifest, actions, cancellationToken);
            await PlanDirectoriesAsync(loaded, ownership, actions, cancellationToken);
            await PlanFilesAsync(loaded, ownership, actions, cancellationToken);
        }

        var plan = new WorkspacePlanReport(
            loaded.FullPath,
            loaded.Manifest.Name,
            DateTimeOffset.UtcNow,
            issues.Count == 0,
            supported,
            actions.Count,
            actions.Count(action => action.Destructive),
            actions.Count(action => !action.SupportsRollback),
            actions,
            diagnostics,
            null,
            null);
        return await WritePlanReportsAsync(plan, reportDirectory, cancellationToken);
    }

    public async Task<WorkspaceExecutionReport> ApplyAsync(
        string manifestPath,
        bool allowPowerShellModules,
        bool allowDelete,
        string? reportDirectory,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var plan = await PlanAsync(manifestPath, reportDirectory, cancellationToken);
        if (!plan.IsValid)
        {
            throw new InvalidDataException("Workspace manifest не прошёл validation.");
        }

        if (!plan.IsSupported)
        {
            throw new PlatformNotSupportedException("Один или несколько Workspace providers недоступны.");
        }

        if (plan.Actions.Any(action => action.Blocked))
        {
            throw new InvalidOperationException("План содержит заблокированные действия.");
        }

        if (plan.DestructiveChanges > 0 && !allowDelete)
        {
            throw new InvalidOperationException("План содержит удаления. Добавьте --allow-delete.");
        }

        if (plan.Actions.Any(action => action.Provider == "powershell.modules") && !allowPowerShellModules)
        {
            throw new InvalidOperationException("Установка PowerShell modules требует --allow-modules.");
        }

        var loaded = await LoadManifestAsync(manifestPath, cancellationToken);
        var transactionId = $"workspace-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var transactionDirectory = Path.Combine(_homeDirectory, "backups", "workspace", transactionId);
        Directory.CreateDirectory(transactionDirectory);
        var transactionPath = Path.Combine(transactionDirectory, "transaction.json");
        var ownership = await LoadOwnershipAsync(cancellationToken);
        var transaction = new WorkspaceTransaction
        {
            SchemaVersion = TransactionSchemaVersion,
            TransactionId = transactionId,
            ManifestPath = loaded.FullPath,
            Name = loaded.Manifest.Name,
            StartedAt = startedAt,
            Status = "running"
        };
        await SaveTransactionAsync(transactionPath, transaction, cancellationToken);

        var messages = new List<string>();
        var applied = 0;
        var failed = 0;
        var rolledBack = false;
        Exception? failure = null;
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownershipKey = OwnershipKeyForAction(action);
            var entry = new WorkspaceTransactionEntry
            {
                ActionId = action.Id,
                Provider = action.Provider,
                Operation = action.Operation,
                Resource = action.Resource,
                SupportsRollback = action.SupportsRollback,
                WasOwned = ownership.Resources.Contains(ownershipKey, StringComparer.OrdinalIgnoreCase)
            };
            transaction.Actions.Add(entry);
            try
            {
                await ApplyActionAsync(
                    action,
                    loaded,
                    entry,
                    transactionDirectory,
                    ownership,
                    cancellationToken);
                entry.Status = "succeeded";
                entry.Message = action.Explanation;
                applied++;
                messages.Add($"[ГОТОВО] {action.Provider}: {action.Resource}");
                await SaveOwnershipAsync(ownership, cancellationToken);
                await SaveTransactionAsync(transactionPath, transaction, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or PlatformNotSupportedException)
            {
                entry.Status = "failed";
                entry.Message = exception.Message;
                failed++;
                failure = exception;
                messages.Add($"[ОШИБКА] {action.Provider}: {exception.Message}");
                await SaveTransactionAsync(transactionPath, transaction, cancellationToken);
                break;
            }
        }

        if (failure is not null)
        {
            rolledBack = true;
            foreach (var entry in transaction.Actions
                .Where(item => item.Status == "succeeded" && item.SupportsRollback)
                .Reverse())
            {
                try
                {
                    await RollbackEntryAsync(entry, ownership, cancellationToken);
                    entry.Status = "rolled-back";
                    messages.Add($"[ОТКАТ] {entry.Provider}: {entry.Resource}");
                }
                catch (Exception rollbackException)
                {
                    messages.Add($"[ОШИБКА ОТКАТА] {entry.Resource}: {rollbackException.Message}");
                }
            }

            transaction.Status = "failed-rolled-back";
            await SaveOwnershipAsync(ownership, cancellationToken);
        }
        else
        {
            transaction.Status = "succeeded";
        }

        transaction.CompletedAt = DateTimeOffset.UtcNow;
        await SaveTransactionAsync(transactionPath, transaction, cancellationToken);
        var report = new WorkspaceExecutionReport(
            transactionId,
            loaded.FullPath,
            loaded.Manifest.Name,
            startedAt,
            transaction.CompletedAt.Value,
            failure is null,
            rolledBack,
            applied,
            failed,
            transactionPath,
            null,
            null,
            messages);
        return await WriteExecutionReportsAsync(report, reportDirectory, cancellationToken);
    }

    public async Task<WorkspaceRollbackReport> RollbackAsync(
        string transactionPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(transactionPath);
        await using var stream = File.OpenRead(fullPath);
        var transaction = await JsonSerializer.DeserializeAsync<WorkspaceTransaction>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Transaction manifest повреждён.");
        if (transaction.SchemaVersion > TransactionSchemaVersion)
        {
            throw new InvalidDataException("Transaction schema создана более новой версией WinState.");
        }

        var ownership = await LoadOwnershipAsync(cancellationToken);
        var messages = new List<string>();
        var restored = 0;
        var skipped = 0;
        foreach (var entry in transaction.Actions
            .Where(item => item.Status is "succeeded" or "rolled-back")
            .Reverse())
        {
            if (!entry.SupportsRollback)
            {
                skipped++;
                messages.Add($"[ПРОПУЩЕНО] {entry.Resource}: действие необратимо.");
                continue;
            }

            await RollbackEntryAsync(entry, ownership, cancellationToken);
            entry.Status = "rolled-back";
            restored++;
            messages.Add($"[ВОССТАНОВЛЕНО] {entry.Resource}");
        }

        transaction.Status = skipped == 0 ? "rolled-back" : "partially-rolled-back";
        transaction.CompletedAt = DateTimeOffset.UtcNow;
        await SaveOwnershipAsync(ownership, cancellationToken);
        await SaveTransactionAsync(fullPath, transaction, cancellationToken);
        return new WorkspaceRollbackReport(
            transaction.TransactionId,
            skipped == 0,
            restored,
            skipped,
            messages);
    }

    public async Task<WorkspaceStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var ownership = await LoadOwnershipAsync(cancellationToken);
        var transactionRoot = Path.Combine(_homeDirectory, "backups", "workspace");
        var latest = Directory.Exists(transactionRoot)
            ? Directory.EnumerateFiles(transactionRoot, "transaction.json", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName
            : null;
        return new WorkspaceStatusReport(
            _ownershipPath,
            ownership.Resources.Count(item => item.StartsWith("git:", StringComparison.OrdinalIgnoreCase)),
            ownership.Resources.Count(item => item.StartsWith("module:", StringComparison.OrdinalIgnoreCase)),
            ownership.Resources.Count(item => item.StartsWith("file:", StringComparison.OrdinalIgnoreCase)),
            ownership.Resources.Count(item => item.StartsWith("directory:", StringComparison.OrdinalIgnoreCase)),
            latest);
    }

    private async Task PlanGitAsync(
        WorkspaceManifest manifest,
        OwnershipLedger ownership,
        ICollection<WorkspaceAction> actions,
        CancellationToken cancellationToken)
    {
        if (!_git.IsSupported)
        {
            return;
        }

        foreach (var entry in manifest.Git)
        {
            var current = await _git.ReadGlobalAsync(entry.Key, cancellationToken);
            if (IsAbsent(entry.State))
            {
                if (current is null)
                {
                    continue;
                }

                var owned = Owns(ownership, OwnershipKey("git", entry.Key));
                actions.Add(CreateAction(
                    "git.config",
                    owned ? "Удалить" : "Заблокировано",
                    entry.Key,
                    "Средний",
                    true,
                    true,
                    !owned,
                    current,
                    null,
                    owned
                        ? $"Удалить принадлежащую WinState настройку Git '{entry.Key}'."
                        : $"Настройка Git '{entry.Key}' не принадлежит WinState."));
            }
            else if (!string.Equals(current, entry.Value, StringComparison.Ordinal))
            {
                actions.Add(CreateAction(
                    "git.config",
                    "Установить",
                    entry.Key,
                    "Низкий",
                    false,
                    true,
                    false,
                    current,
                    entry.Value,
                    $"Установить глобальную настройку Git '{entry.Key}'."));
            }
        }
    }

    private async Task PlanModulesAsync(
        WorkspaceManifest manifest,
        ICollection<WorkspaceAction> actions,
        CancellationToken cancellationToken)
    {
        if (!_modules.IsSupported)
        {
            return;
        }

        foreach (var module in manifest.PowerShellModules)
        {
            var current = await _modules.ReadInstalledVersionAsync(module.Name, cancellationToken);
            if (IsAbsent(module.State))
            {
                if (current is not null)
                {
                    actions.Add(CreateAction(
                        "powershell.modules",
                        "Заблокировано",
                        module.Name,
                        "Высокий",
                        true,
                        false,
                        true,
                        current,
                        null,
                        "WinState 1.0 не удаляет PowerShell modules автоматически."));
                }
            }
            else if (!VersionSatisfies(current, module.MinimumVersion))
            {
                actions.Add(CreateAction(
                    "powershell.modules",
                    "Установить",
                    module.Name,
                    "Средний",
                    false,
                    false,
                    false,
                    current,
                    module.MinimumVersion ?? "latest",
                    $"Установить PowerShell module '{module.Name}' для CurrentUser."));
            }
        }
    }

    private static Task PlanDirectoriesAsync(
        LoadedWorkspaceManifest loaded,
        OwnershipLedger ownership,
        ICollection<WorkspaceAction> actions,
        CancellationToken cancellationToken)
    {
        foreach (var directory in loaded.Manifest.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveManagedPath(directory.Path, loaded.Directory);
            var exists = Directory.Exists(path);
            if (IsAbsent(directory.State))
            {
                if (!exists)
                {
                    continue;
                }

                var owned = Owns(ownership, OwnershipKey("directory", path));
                var empty = !Directory.EnumerateFileSystemEntries(path).Any();
                var blocked = !owned || !empty;
                actions.Add(CreateAction(
                    "files.managed",
                    blocked ? "Заблокировано" : "Удалить каталог",
                    path,
                    "Высокий",
                    true,
                    true,
                    blocked,
                    "exists",
                    null,
                    !owned
                        ? "Каталог не принадлежит WinState."
                        : !empty
                            ? "WinState удаляет только пустые каталоги."
                            : "Удалить пустой управляемый каталог."));
            }
            else if (!exists)
            {
                actions.Add(CreateAction(
                    "files.managed",
                    "Создать каталог",
                    path,
                    "Низкий",
                    false,
                    true,
                    false,
                    null,
                    "directory",
                    "Создать управляемый каталог."));
            }
        }

        return Task.CompletedTask;
    }

    private static async Task PlanFilesAsync(
        LoadedWorkspaceManifest loaded,
        OwnershipLedger ownership,
        ICollection<WorkspaceAction> actions,
        CancellationToken cancellationToken)
    {
        foreach (var file in loaded.Manifest.Files)
        {
            var path = ResolveManagedPath(file.Path, loaded.Directory);
            var exists = File.Exists(path);
            if (IsAbsent(file.State))
            {
                if (!exists)
                {
                    continue;
                }

                var owned = Owns(ownership, OwnershipKey("file", path));
                actions.Add(CreateAction(
                    "files.managed",
                    owned ? "Удалить файл" : "Заблокировано",
                    path,
                    "Высокий",
                    true,
                    true,
                    !owned,
                    await FileSha256Async(path, cancellationToken),
                    null,
                    owned
                        ? "Удалить управляемый файл после backup."
                        : "Файл не принадлежит WinState."));
                continue;
            }

            var desiredBytes = await ResolveDesiredFileBytesAsync(file, loaded.Directory, cancellationToken);
            var desiredHash = Sha256(desiredBytes);
            var currentHash = exists ? await FileSha256Async(path, cancellationToken) : null;
            if (!string.Equals(currentHash, desiredHash, StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(CreateAction(
                    "files.managed",
                    exists ? "Обновить файл" : "Создать файл",
                    path,
                    exists ? "Средний" : "Низкий",
                    false,
                    true,
                    false,
                    currentHash,
                    desiredHash,
                    exists
                        ? "Обновить управляемый файл после backup."
                        : "Создать управляемый файл атомарной записью."));
            }
        }
    }

    private async Task ApplyActionAsync(
        WorkspaceAction action,
        LoadedWorkspaceManifest loaded,
        WorkspaceTransactionEntry entry,
        string transactionDirectory,
        OwnershipLedger ownership,
        CancellationToken cancellationToken)
    {
        var ownershipKey = OwnershipKeyForAction(action);
        if (action.Provider == "git.config")
        {
            entry.PreviousValue = await _git.ReadGlobalAsync(action.Resource, cancellationToken);
            entry.Existed = entry.PreviousValue is not null;
            var specification = loaded.Manifest.Git.Single(item =>
                item.Key.Equals(action.Resource, StringComparison.OrdinalIgnoreCase));
            if (IsAbsent(specification.State))
            {
                await _git.RemoveGlobalAsync(specification.Key, cancellationToken);
                RemoveOwnership(ownership, ownershipKey);
            }
            else
            {
                await _git.WriteGlobalAsync(
                    specification.Key,
                    specification.Value ?? string.Empty,
                    cancellationToken);
                AddOwnership(ownership, ownershipKey);
            }

            return;
        }

        if (action.Provider == "powershell.modules")
        {
            var module = loaded.Manifest.PowerShellModules.Single(item =>
                item.Name.Equals(action.Resource, StringComparison.OrdinalIgnoreCase));
            entry.PreviousValue = await _modules.ReadInstalledVersionAsync(module.Name, cancellationToken);
            entry.Existed = entry.PreviousValue is not null;
            await _modules.InstallAsync(
                module.Name,
                module.MinimumVersion,
                module.Repository,
                cancellationToken);
            AddOwnership(ownership, ownershipKey);
            return;
        }

        if (action.Operation.Contains("каталог", StringComparison.OrdinalIgnoreCase))
        {
            entry.Existed = Directory.Exists(action.Resource);
            if (action.Operation.StartsWith("Создать", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(action.Resource);
                AddOwnership(ownership, ownershipKey);
            }
            else
            {
                Directory.Delete(action.Resource, false);
                RemoveOwnership(ownership, ownershipKey);
            }

            return;
        }

        entry.Existed = File.Exists(action.Resource);
        if (entry.Existed)
        {
            var backupPath = Path.Combine(
                transactionDirectory,
                "files",
                $"{action.Id}-{Path.GetFileName(action.Resource)}");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(action.Resource, backupPath, true);
            entry.BackupPath = backupPath;
        }

        var file = loaded.Manifest.Files.Single(item =>
            PathEquals(ResolveManagedPath(item.Path, loaded.Directory), action.Resource));
        if (IsAbsent(file.State))
        {
            File.Delete(action.Resource);
            RemoveOwnership(ownership, ownershipKey);
            return;
        }

        var bytes = await ResolveDesiredFileBytesAsync(file, loaded.Directory, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(action.Resource)!);
        var temporaryPath = action.Resource + $".winstate-{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, action.Resource, true);
        AddOwnership(ownership, ownershipKey);
    }

    private async Task RollbackEntryAsync(
        WorkspaceTransactionEntry entry,
        OwnershipLedger ownership,
        CancellationToken cancellationToken)
    {
        var ownershipKey = OwnershipKeyForTransactionEntry(entry);
        if (entry.Provider == "git.config")
        {
            if (entry.Existed)
            {
                await _git.WriteGlobalAsync(entry.Resource, entry.PreviousValue ?? string.Empty, cancellationToken);
            }
            else
            {
                await _git.RemoveGlobalAsync(entry.Resource, cancellationToken);
            }
        }
        else if (entry.Provider == "files.managed"
            && entry.Operation.Contains("каталог", StringComparison.OrdinalIgnoreCase))
        {
            if (entry.Existed)
            {
                Directory.CreateDirectory(entry.Resource);
            }
            else if (Directory.Exists(entry.Resource)
                && !Directory.EnumerateFileSystemEntries(entry.Resource).Any())
            {
                Directory.Delete(entry.Resource, false);
            }
        }
        else if (entry.Provider == "files.managed")
        {
            if (entry.Existed && !string.IsNullOrWhiteSpace(entry.BackupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Resource)!);
                File.Copy(entry.BackupPath, entry.Resource, true);
            }
            else if (!entry.Existed && File.Exists(entry.Resource))
            {
                File.Delete(entry.Resource);
            }
        }

        if (entry.WasOwned)
        {
            AddOwnership(ownership, ownershipKey);
        }
        else
        {
            RemoveOwnership(ownership, ownershipKey);
        }
    }

    private static List<string> ValidateManifest(WorkspaceManifest manifest, string manifestPath)
    {
        var issues = new List<string>();
        if (manifest.SchemaVersion != ManifestSchemaVersion)
        {
            issues.Add($"{manifestPath}: поддерживается только schemaVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            issues.Add("Укажите name для Workspace manifest.");
        }

        foreach (var entry in manifest.Git)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                issues.Add("Git entry содержит пустой key.");
            }

            ValidateState(entry.State, $"Git '{entry.Key}'", issues);
            if (!IsAbsent(entry.State) && entry.Value is null)
            {
                issues.Add($"Git '{entry.Key}': для state=present необходимо value.");
            }
        }

        foreach (var module in manifest.PowerShellModules)
        {
            if (string.IsNullOrWhiteSpace(module.Name))
            {
                issues.Add("PowerShell module содержит пустое имя.");
            }

            ValidateState(module.State, $"Module '{module.Name}'", issues);
            if (!string.IsNullOrWhiteSpace(module.MinimumVersion)
                && !Version.TryParse(module.MinimumVersion, out _))
            {
                issues.Add($"Module '{module.Name}': minimumVersion имеет неверный формат.");
            }
        }

        foreach (var directory in manifest.Directories)
        {
            if (string.IsNullOrWhiteSpace(directory.Path))
            {
                issues.Add("Directory entry содержит пустой path.");
            }

            ValidateState(directory.State, $"Directory '{directory.Path}'", issues);
        }

        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                issues.Add("File entry содержит пустой path.");
            }

            ValidateState(file.State, $"File '{file.Path}'", issues);
            if (!IsAbsent(file.State))
            {
                var sources = (file.Content is not null ? 1 : 0)
                    + (!string.IsNullOrWhiteSpace(file.Source) ? 1 : 0);
                if (sources != 1)
                {
                    issues.Add($"File '{file.Path}': укажите ровно одно из content или source.");
                }

                if (!file.Encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"File '{file.Path}': поддерживается только utf-8.");
                }
            }
        }

        var duplicates = manifest.Files.Select(item => $"file:{item.Path}")
            .Concat(manifest.Directories.Select(item => $"directory:{item.Path}"))
            .Concat(manifest.Git.Select(item => $"git:{item.Key}"))
            .Concat(manifest.PowerShellModules.Select(item => $"module:{item.Name}"))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicate in duplicates)
        {
            issues.Add($"Ресурс объявлен несколько раз: {duplicate}.");
        }

        return issues;
    }

    private static void ValidateState(string state, string resource, ICollection<string> issues)
    {
        if (!state.Equals("present", StringComparison.OrdinalIgnoreCase)
            && !state.Equals("absent", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"{resource}: state должен быть present или absent.");
        }
    }

    private async Task<LoadedWorkspaceManifest> LoadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Не указан Workspace manifest.", nameof(manifestPath));
        }

        var fullPath = Path.GetFullPath(manifestPath);
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<WorkspaceManifest>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Workspace manifest пуст или повреждён.");
        return new LoadedWorkspaceManifest(
            fullPath,
            Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory,
            manifest);
    }

    private async Task<OwnershipLedger> LoadOwnershipAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_ownershipPath))
        {
            return new OwnershipLedger { SchemaVersion = OwnershipSchemaVersion };
        }

        await using var stream = File.OpenRead(_ownershipPath);
        var ledger = await JsonSerializer.DeserializeAsync<OwnershipLedger>(
            stream,
            JsonOptions,
            cancellationToken) ?? new OwnershipLedger();
        if (ledger.SchemaVersion > OwnershipSchemaVersion)
        {
            throw new InvalidDataException("Ownership ledger создан более новой версией WinState.");
        }

        if (ledger.SchemaVersion < OwnershipSchemaVersion)
        {
            ledger.SchemaVersion = OwnershipSchemaVersion;
            ledger.MigratedAt = DateTimeOffset.UtcNow;
        }

        ledger.Resources = ledger.Resources
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ledger;
    }

    private async Task SaveOwnershipAsync(
        OwnershipLedger ownership,
        CancellationToken cancellationToken)
    {
        ownership.SchemaVersion = OwnershipSchemaVersion;
        ownership.UpdatedAt = DateTimeOffset.UtcNow;
        ownership.Resources = ownership.Resources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await WriteJsonAtomicallyAsync(_ownershipPath, ownership, cancellationToken);
    }

    private static Task SaveTransactionAsync(
        string path,
        WorkspaceTransaction transaction,
        CancellationToken cancellationToken)
        => WriteJsonAtomicallyAsync(path, transaction, cancellationToken);

    private async Task<WorkspacePlanReport> WritePlanReportsAsync(
        WorkspacePlanReport report,
        string? reportDirectory,
        CancellationToken cancellationToken)
    {
        var directory = ResolveReportDirectory(reportDirectory);
        Directory.CreateDirectory(directory);
        var stem = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-workspace-plan-{Slug(report.Name)}";
        var jsonPath = Path.Combine(directory, stem + ".json");
        var markdownPath = Path.Combine(directory, stem + ".md");
        var final = report with { JsonReportPath = jsonPath, MarkdownReportPath = markdownPath };
        await WriteJsonAtomicallyAsync(jsonPath, final, cancellationToken);
        await WriteTextAtomicallyAsync(markdownPath, BuildPlanMarkdown(final), cancellationToken);
        return final;
    }

    private async Task<WorkspaceExecutionReport> WriteExecutionReportsAsync(
        WorkspaceExecutionReport report,
        string? reportDirectory,
        CancellationToken cancellationToken)
    {
        var directory = ResolveReportDirectory(reportDirectory);
        Directory.CreateDirectory(directory);
        var stem = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-workspace-apply-{Slug(report.Name)}";
        var jsonPath = Path.Combine(directory, stem + ".json");
        var markdownPath = Path.Combine(directory, stem + ".md");
        var final = report with { JsonReportPath = jsonPath, MarkdownReportPath = markdownPath };
        await WriteJsonAtomicallyAsync(jsonPath, final, cancellationToken);
        await WriteTextAtomicallyAsync(markdownPath, BuildExecutionMarkdown(final), cancellationToken);
        return final;
    }

    private string ResolveReportDirectory(string? reportDirectory)
        => string.IsNullOrWhiteSpace(reportDirectory)
            ? Path.Combine(_homeDirectory, "reports", "workspace")
            : Path.GetFullPath(reportDirectory);

    private static WorkspaceAction CreateAction(
        string provider,
        string operation,
        string resource,
        string risk,
        bool destructive,
        bool supportsRollback,
        bool blocked,
        string? current,
        string? desired,
        string explanation)
        => new(
            StableActionId(provider, operation, resource),
            provider,
            operation,
            resource,
            risk,
            destructive,
            supportsRollback,
            blocked,
            current,
            desired,
            explanation);

    private static string StableActionId(string provider, string operation, string resource)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}|{operation}|{resource}"));
        return $"workspace-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string ResolveManagedPath(string value, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Некорректный managed path.");
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded == "~")
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal)
            || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(baseDirectory, expanded));
    }

    private static async Task<byte[]> ResolveDesiredFileBytesAsync(
        WorkspaceFileEntry file,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        if (file.Content is not null)
        {
            return new UTF8Encoding(false).GetBytes(file.Content);
        }

        if (string.IsNullOrWhiteSpace(file.Source))
        {
            throw new InvalidDataException($"Для файла '{file.Path}' не указан content/source.");
        }

        return await File.ReadAllBytesAsync(
            ResolveManagedPath(file.Source, baseDirectory),
            cancellationToken);
    }

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
        => Sha256(await File.ReadAllBytesAsync(path, cancellationToken));

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool VersionSatisfies(string? current, string? minimum)
    {
        if (current is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(minimum))
        {
            return true;
        }

        return Version.TryParse(current, out var currentVersion)
            && Version.TryParse(minimum, out var minimumVersion)
            && currentVersion.CompareTo(minimumVersion) >= 0;
    }

    private static bool IsAbsent(string state)
        => state.Equals("absent", StringComparison.OrdinalIgnoreCase);

    private static string OwnershipKey(string kind, string resource)
        => $"{kind}:{(OperatingSystem.IsWindows() ? resource.ToUpperInvariant() : resource)}";

    private static bool Owns(OwnershipLedger ownership, string key)
        => ownership.Resources.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static string OwnershipKeyForAction(WorkspaceAction action)
        => action.Provider switch
        {
            "git.config" => OwnershipKey("git", action.Resource),
            "powershell.modules" => OwnershipKey("module", action.Resource),
            _ when action.Operation.Contains("каталог", StringComparison.OrdinalIgnoreCase)
                => OwnershipKey("directory", action.Resource),
            _ => OwnershipKey("file", action.Resource)
        };

    private static string OwnershipKeyForTransactionEntry(WorkspaceTransactionEntry entry)
        => entry.Provider switch
        {
            "git.config" => OwnershipKey("git", entry.Resource),
            "powershell.modules" => OwnershipKey("module", entry.Resource),
            _ when entry.Operation.Contains("каталог", StringComparison.OrdinalIgnoreCase)
                => OwnershipKey("directory", entry.Resource),
            _ => OwnershipKey("file", entry.Resource)
        };

    private static void AddOwnership(OwnershipLedger ownership, string value)
    {
        if (!Owns(ownership, value))
        {
            ownership.Resources.Add(value);
        }
    }

    private static void RemoveOwnership(OwnershipLedger ownership, string value)
        => ownership.Resources.RemoveAll(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static bool PathEquals(string left, string right)
        => left.Equals(
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string BuildPlanMarkdown(WorkspacePlanReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"# План Workspace Control: {report.Name}")
            .AppendLine()
            .AppendLine($"- Создан: `{report.PlannedAt:O}`")
            .AppendLine($"- Manifest: `{report.ManifestPath}`")
            .AppendLine($"- Валидный: **{report.IsValid}**")
            .AppendLine($"- Providers доступны: **{report.IsSupported}**")
            .AppendLine($"- Изменений: **{report.Changes}**")
            .AppendLine($"- Удалений: **{report.DestructiveChanges}**")
            .AppendLine($"- Необратимых действий: **{report.IrreversibleChanges}**")
            .AppendLine()
            .AppendLine("## Действия")
            .AppendLine()
            .AppendLine("| Provider | Операция | Риск | Ресурс | Rollback |")
            .AppendLine("|---|---|---|---|---|");
        foreach (var action in report.Actions)
        {
            text.AppendLine($"| {EscapeMarkdown(action.Provider)} | {EscapeMarkdown(action.Operation)} | {action.Risk} | `{EscapeMarkdown(action.Resource)}` | {(action.SupportsRollback ? "да" : "нет")} |");
        }

        if (report.Diagnostics.Count > 0)
        {
            text.AppendLine().AppendLine("## Диагностика").AppendLine();
            foreach (var diagnostic in report.Diagnostics)
            {
                text.AppendLine($"- {diagnostic}");
            }
        }

        return text.ToString();
    }

    private static string BuildExecutionMarkdown(WorkspaceExecutionReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Результат Workspace Control: {report.Name}")
            .AppendLine()
            .AppendLine($"- Транзакция: `{report.TransactionId}`")
            .AppendLine($"- Успешно: **{report.Succeeded}**")
            .AppendLine($"- Автоматический откат: **{report.RolledBack}**")
            .AppendLine($"- Применено: **{report.AppliedActions}**")
            .AppendLine($"- Ошибок: **{report.FailedActions}**")
            .AppendLine($"- Transaction manifest: `{report.TransactionPath}`")
            .AppendLine()
            .AppendLine("## Журнал")
            .AppendLine();
        foreach (var message in report.Messages)
        {
            text.AppendLine($"- {message}");
        }

        return text.ToString();
    }

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal);

    private static string Slug(string value)
    {
        var normalized = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized)
            ? "workspace"
            : normalized[..Math.Min(normalized.Length, 60)];
    }

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, value, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private sealed record LoadedWorkspaceManifest(
        string FullPath,
        string Directory,
        WorkspaceManifest Manifest);

    private sealed record OwnershipLedger
    {
        public int SchemaVersion { get; set; } = OwnershipSchemaVersion;
        public DateTimeOffset? MigratedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public List<string> Resources { get; set; } = new();
    }

    private sealed record WorkspaceTransaction
    {
        public int SchemaVersion { get; set; } = TransactionSchemaVersion;
        public string TransactionId { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<WorkspaceTransactionEntry> Actions { get; set; } = new();
    }

    private sealed record WorkspaceTransactionEntry
    {
        public string ActionId { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Resource { get; set; } = string.Empty;
        public bool SupportsRollback { get; set; }
        public bool Existed { get; set; }
        public bool WasOwned { get; set; }
        public string? PreviousValue { get; set; }
        public string? BackupPath { get; set; }
        public string Status { get; set; } = "pending";
        public string? Message { get; set; }
    }
}
