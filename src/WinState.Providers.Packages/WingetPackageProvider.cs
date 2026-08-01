using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Providers;
using WinState.Domain.Resources;

namespace WinState.Providers.Packages;

public sealed record WingetInstalledPackage(
    string Id,
    string Version,
    string? AvailableVersion,
    string Source);

public sealed record WingetOperationResult(
    bool Succeeded,
    bool RebootRequired,
    string Message);

public interface IWingetClient
{
    bool IsSupported { get; }

    Task<IReadOnlyList<WingetInstalledPackage>> ListInstalledAsync(
        CancellationToken cancellationToken);

    Task<WingetInstalledPackage?> GetInstalledAsync(
        string id,
        string source,
        CancellationToken cancellationToken);

    Task<WingetOperationResult> InstallAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken);

    Task<WingetOperationResult> UpgradeAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken);

    Task<WingetOperationResult> UninstallAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken);
}

/// <summary>Запускает официальный winget CLI без shell-интерпретации аргументов.</summary>
public sealed class ProcessWingetClient : IWingetClient
{
    private static readonly Regex ColumnSeparator = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^-{3,}", RegexOptions.Compiled);

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<WingetInstalledPackage>> ListInstalledAsync(
        CancellationToken cancellationToken)
    {
        EnsureSupported();
        var result = await RunAsync(
            ["list", "--accept-source-agreements", "--disable-interactivity"],
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"winget list завершился ошибкой: {result.Message}");
        }

        return ParsePackages(result.Output);
    }

    public async Task<WingetInstalledPackage?> GetInstalledAsync(
        string id,
        string source,
        CancellationToken cancellationToken)
    {
        var packages = await ListInstalledAsync(cancellationToken);
        return packages.FirstOrDefault(package =>
            package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(source)
                || package.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<WingetOperationResult> InstallAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken)
        => RunPackageCommandAsync("install", package, cancellationToken);

    public Task<WingetOperationResult> UpgradeAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken)
        => RunPackageCommandAsync("upgrade", package, cancellationToken);

    public Task<WingetOperationResult> UninstallAsync(
        WingetPackageProfile package,
        CancellationToken cancellationToken)
        => RunPackageCommandAsync("uninstall", package, cancellationToken);

    private async Task<WingetOperationResult> RunPackageCommandAsync(
        string command,
        WingetPackageProfile package,
        CancellationToken cancellationToken)
    {
        EnsureSupported();
        var arguments = new List<string>
        {
            command,
            "--id", package.Id,
            "--exact",
            "--silent",
            "--disable-interactivity"
        };
        if (command is "install" or "upgrade")
        {
            arguments.Add("--accept-package-agreements");
            arguments.Add("--accept-source-agreements");
        }

        if (!string.IsNullOrWhiteSpace(package.Source))
        {
            arguments.Add("--source");
            arguments.Add(package.Source);
        }

        if (command is "install" or "upgrade")
        {
            arguments.Add("--scope");
            arguments.Add(package.Scope);
            if (!package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("--version");
                arguments.Add(package.Version);
            }
        }

        var result = await RunAsync(arguments, cancellationToken);
        return new WingetOperationResult(result.Succeeded, result.RebootRequired, result.Message);
    }

    private static async Task<CommandResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "winget.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["WINGET_DISABLE_INTERACTIVITY"] = "1";
        try
        {
            if (!process.Start())
            {
                return new CommandResult(false, false, string.Empty, "winget.exe не запущен.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "winget.exe не найден. Установите или обновите App Installer.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var reboot = process.ExitCode == 3010;
        var succeeded = process.ExitCode == 0 || reboot;
        var message = Compact(string.IsNullOrWhiteSpace(error) ? output : error);
        return new CommandResult(succeeded, reboot, output, message);
    }

    private static IReadOnlyList<WingetInstalledPackage> ParsePackages(string output)
    {
        var result = new List<WingetInstalledPackage>();
        var tableStarted = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!tableStarted)
            {
                tableStarted = TableSeparator.IsMatch(line);
                continue;
            }

            var columns = ColumnSeparator.Split(line)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (columns.Length < 3)
            {
                continue;
            }

            string id;
            string version;
            string? available = null;
            string source;
            if (columns.Length >= 5)
            {
                id = columns[^4];
                version = columns[^3];
                available = columns[^2];
                source = columns[^1];
            }
            else if (columns.Length == 4)
            {
                id = columns[^3];
                version = columns[^2];
                source = columns[^1];
            }
            else
            {
                id = columns[^2];
                version = columns[^1];
                source = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
            {
                result.Add(new WingetInstalledPackage(id, version, available, source));
            }
        }

        return result
            .GroupBy(package => $"{package.Source}|{package.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Compact(string value)
    {
        var compact = string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 1200 ? compact : compact[^1200..];
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("WinGet Provider доступен только в Windows.");
        }
    }

    private sealed record CommandResult(
        bool Succeeded,
        bool RebootRequired,
        string Output,
        string Message);
}

public static class WingetProfileMapper
{
    public const string ProviderId = "packages.winget";
    public const string ResourceType = "package.winget";

    public static DesiredProviderState CreateDesiredState(WinStateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new DesiredProviderState(profile.Packages.Select(CreateResource).ToArray());
    }

    public static StateResource CreateResource(WingetPackageProfile package)
    {
        var state = package.State.Equals("absent", StringComparison.OrdinalIgnoreCase)
            ? DesiredState.Absent
            : DesiredState.Present;
        return new StateResource
        {
            ProviderId = ProviderId,
            ResourceType = ResourceType,
            Identity = Identity(package.Id, package.Source),
            State = state,
            Properties = Properties(
                ("id", package.Id),
                ("version", package.Version),
                ("source", package.Source),
                ("scope", package.Scope),
                ("allowUpgrade", package.AllowUpgrade.ToString()),
                ("mayRequireReboot", package.MayRequireReboot.ToString()))
        };
    }

    public static WingetPackageProfile FromResource(StateResource resource)
        => new()
        {
            Id = Required(resource, "id"),
            Version = Optional(resource, "version", "latest"),
            Source = Optional(resource, "source", "winget"),
            Scope = Optional(resource, "scope", "user"),
            State = resource.State == DesiredState.Absent ? "absent" : "present",
            AllowUpgrade = ParseBoolean(resource, "allowUpgrade", true),
            MayRequireReboot = ParseBoolean(resource, "mayRequireReboot", false)
        };

    public static string Identity(string id, string source)
        => $"winget:{StableToken(source)}:{StableToken(id)}";

    public static string Required(StateResource resource, string property)
    {
        if (!resource.Properties.TryGetValue(property, out var value)
            || string.IsNullOrWhiteSpace(value.Value))
        {
            throw new InvalidDataException(
                $"Ресурс '{resource.Identity}' не содержит свойство '{property}'.");
        }

        return value.Value!;
    }

    public static string Optional(StateResource resource, string property, string fallback)
        => resource.Properties.TryGetValue(property, out var value)
            && !string.IsNullOrWhiteSpace(value.Value)
                ? value.Value!
                : fallback;

    private static bool ParseBoolean(StateResource resource, string property, bool fallback)
        => resource.Properties.TryGetValue(property, out var value)
            && bool.TryParse(value.Value, out var parsed)
                ? parsed
                : fallback;

    private static IReadOnlyDictionary<string, StateValue> Properties(
        params (string Name, string Value)[] values)
        => values.ToDictionary(
            value => value.Name,
            value => StateValue.From(value.Value),
            StringComparer.OrdinalIgnoreCase);

    private static string StableToken(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant();
    }
}

/// <summary>Декларативный provider установленных WinGet packages.</summary>
public sealed class WingetPackageProvider : IStateProvider, IRollbackProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IWingetClient _client;

    public WingetPackageProvider(IWingetClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Id => WingetProfileMapper.ProviderId;
    public string DisplayName => "WinGet Packages";
    public bool IsSupported => _client.IsSupported;
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Capture
        | ProviderCapabilities.Apply
        | ProviderCapabilities.Remove
        | ProviderCapabilities.Rollback
        | ProviderCapabilities.MayRequireAdministrator
        | ProviderCapabilities.MayRequireReboot;

    public async Task<ProviderDiscoveryResult> DiscoverAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (!IsSupported)
        {
            return new ProviderDiscoveryResult(
                Array.Empty<StateResource>(),
                [new ProviderDiagnostic("winget.platform.unsupported", "WinGet Provider доступен только в Windows.", true)]);
        }

        try
        {
            var packages = await _client.ListInstalledAsync(cancellationToken);
            return new ProviderDiscoveryResult(
                packages.Select(package => new StateResource
                {
                    ProviderId = Id,
                    ResourceType = WingetProfileMapper.ResourceType,
                    Identity = WingetProfileMapper.Identity(package.Id, package.Source),
                    State = DesiredState.Present,
                    Properties = Properties(
                        ("id", package.Id),
                        ("version", package.Version),
                        ("availableVersion", package.AvailableVersion ?? string.Empty),
                        ("source", package.Source),
                        ("scope", "unknown"))
                }).ToArray(),
                Array.Empty<ProviderDiagnostic>());
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return new ProviderDiscoveryResult(
                Array.Empty<StateResource>(),
                [new ProviderDiagnostic("winget.discovery.failed", exception.Message, true)]);
        }
    }

    public Task<IReadOnlyCollection<PlannedAction>> PlanAsync(
        DesiredProviderState desiredState,
        CurrentProviderState currentState,
        PlanningContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredState);
        ArgumentNullException.ThrowIfNull(currentState);
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        var current = currentState.Resources
            .Where(resource => resource.ProviderId.Equals(Id, StringComparison.OrdinalIgnoreCase))
            .GroupBy(resource => resource.NormalizedIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var actions = new List<PlannedAction>();
        foreach (var desired in desiredState.Resources
            .Where(resource => resource.ProviderId.Equals(Id, StringComparison.OrdinalIgnoreCase)))
        {
            _ = current.TryGetValue(desired.NormalizedIdentity, out var installed);
            var action = PlanPackage(desired, installed);
            if (action is not null)
            {
                actions.Add(action);
            }
        }

        return Task.FromResult<IReadOnlyCollection<PlannedAction>>(actions);
    }

    public async Task<ActionExecutionResult> ApplyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = context;
        var package = WingetProfileMapper.FromResource(action.Resource);
        WingetOperationResult result = action.Operation switch
        {
            ActionType.Install => await _client.InstallAsync(package, cancellationToken),
            ActionType.Update => await _client.UpgradeAsync(package, cancellationToken),
            ActionType.Uninstall => await _client.UninstallAsync(package, cancellationToken),
            _ => new WingetOperationResult(false, false, $"Операция {action.Operation} не поддерживается WinGet Provider.")
        };
        return new ActionExecutionResult(
            result.Succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
            result.Message,
            result.RebootRequired
                ? [new ProviderDiagnostic("winget.reboot.required", "WinGet сообщил о необходимости перезагрузки.", true)]
                : Array.Empty<ProviderDiagnostic>());
    }

    public async Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var package = WingetProfileMapper.FromResource(action.Resource);
        var installed = await _client.GetInstalledAsync(package.Id, package.Source, cancellationToken);
        if (package.State.Equals("absent", StringComparison.OrdinalIgnoreCase))
        {
            return new VerificationResult(
                installed is null,
                installed is null
                    ? $"Package {package.Id} отсутствует."
                    : $"Package {package.Id} всё ещё установлен.");
        }

        if (installed is null)
        {
            return new VerificationResult(false, $"Package {package.Id} не найден после применения.");
        }

        var exact = !package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase);
        var matches = !exact || installed.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase);
        return new VerificationResult(
            matches,
            matches
                ? $"Package {package.Id} {installed.Version} подтверждён."
                : $"Package {package.Id}: ожидалась версия {package.Version}, найдена {installed.Version}.");
    }

    public async Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (action.Operation != ActionType.Install)
        {
            return new RollbackPreparationResult(false, null, "Upgrade и uninstall не имеют гарантированного rollback.");
        }

        Directory.CreateDirectory(context.BackupDirectory);
        var package = WingetProfileMapper.FromResource(action.Resource);
        var existing = await _client.GetInstalledAsync(package.Id, package.Source, cancellationToken);
        var payload = new WingetRollbackPayload
        {
            ActionId = action.Id,
            Package = package,
            WasInstalled = existing is not null,
            PreviousVersion = existing?.Version
        };
        var path = Path.Combine(context.BackupDirectory, $"{action.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
        return new RollbackPreparationResult(true, path, "Checkpoint установки WinGet создан.");
    }

    public async Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (!File.Exists(action.BackupReference))
        {
            return new RollbackExecutionResult(false, "Checkpoint WinGet не найден.");
        }

        try
        {
            var json = await File.ReadAllTextAsync(action.BackupReference, cancellationToken);
            var payload = JsonSerializer.Deserialize<WingetRollbackPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Checkpoint WinGet повреждён.");
            if (payload.WasInstalled)
            {
                return new RollbackExecutionResult(true, "Package существовал до транзакции; удаление не требуется.");
            }

            var result = await _client.UninstallAsync(payload.Package, cancellationToken);
            return new RollbackExecutionResult(
                result.Succeeded,
                result.Succeeded
                    ? $"Установленный транзакцией package {payload.Package.Id} удалён."
                    : result.Message);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or JsonException
            or InvalidOperationException)
        {
            return new RollbackExecutionResult(false, exception.Message);
        }
    }

    private static PlannedAction? PlanPackage(StateResource desired, StateResource? current)
    {
        var package = WingetProfileMapper.FromResource(desired);
        var requiresAdministrator = package.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase);
        if (desired.State == DesiredState.Absent)
        {
            return current is null
                ? null
                : CreateAction(
                    desired,
                    ActionType.Uninstall,
                    RiskLevel.High,
                    requiresAdministrator,
                    false,
                    $"Удалить WinGet package '{package.Id}'. Точное автоматическое восстановление не гарантируется.");
        }

        if (current is null)
        {
            return CreateAction(
                desired,
                ActionType.Install,
                requiresAdministrator ? RiskLevel.Medium : RiskLevel.Low,
                requiresAdministrator,
                true,
                $"Установить WinGet package '{package.Id}' ({package.Version}).");
        }

        var installedVersion = Property(current, "version");
        var availableVersion = Property(current, "availableVersion");
        var exactUpgrade = !package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            && !installedVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase);
        var latestUpgrade = package.Version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            && package.AllowUpgrade
            && !string.IsNullOrWhiteSpace(availableVersion)
            && !installedVersion.Equals(availableVersion, StringComparison.OrdinalIgnoreCase);
        if (!exactUpgrade && !latestUpgrade)
        {
            return null;
        }

        return CreateAction(
            desired,
            ActionType.Update,
            requiresAdministrator ? RiskLevel.High : RiskLevel.Medium,
            requiresAdministrator,
            false,
            $"Обновить WinGet package '{package.Id}' с {installedVersion} до {(exactUpgrade ? package.Version : availableVersion)}. Rollback версии не гарантируется.");
    }

    private static PlannedAction CreateAction(
        StateResource resource,
        ActionType operation,
        RiskLevel risk,
        bool requiresAdministrator,
        bool supportsRollback,
        string explanation)
        => new()
        {
            Id = ActionId(operation, resource.NormalizedIdentity),
            ProviderId = WingetProfileMapper.ProviderId,
            Resource = resource,
            Operation = operation,
            Risk = risk,
            RequiresAdministrator = requiresAdministrator,
            MayRequireReboot = bool.TryParse(Property(resource, "mayRequireReboot"), out var reboot) && reboot,
            SupportsRollback = supportsRollback,
            Explanation = explanation
        };

    private static string ActionId(ActionType operation, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{identity}"));
        return $"pkg-{operation.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    private static string Property(StateResource resource, string name)
        => resource.Properties.TryGetValue(name, out var value) ? value.Value ?? string.Empty : string.Empty;

    private static IReadOnlyDictionary<string, StateValue> Properties(
        params (string Name, string Value)[] values)
        => values.ToDictionary(
            value => value.Name,
            value => StateValue.From(value.Value),
            StringComparer.OrdinalIgnoreCase);

    private sealed record WingetRollbackPayload
    {
        public string ActionId { get; init; } = string.Empty;
        public WingetPackageProfile Package { get; init; } = new() { Id = string.Empty };
        public bool WasInstalled { get; init; }
        public string? PreviousVersion { get; init; }
    }
}
