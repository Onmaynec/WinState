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

namespace WinState.Providers.Features;

public sealed record WindowsFeatureState(string Name, bool Enabled, string RawState);

public sealed record WindowsFeatureOperationResult(
    bool Succeeded,
    bool RebootRequired,
    string Message);

public interface IWindowsFeatureClient
{
    bool IsSupported { get; }

    Task<IReadOnlyList<WindowsFeatureState>> ListAsync(
        CancellationToken cancellationToken);

    Task<WindowsFeatureState?> GetAsync(
        string name,
        CancellationToken cancellationToken);

    Task<WindowsFeatureOperationResult> EnableAsync(
        string name,
        bool includeParents,
        CancellationToken cancellationToken);

    Task<WindowsFeatureOperationResult> DisableAsync(
        string name,
        CancellationToken cancellationToken);
}

/// <summary>Использует DISM /English /NoRestart для Windows Optional Features.</summary>
public sealed class DismWindowsFeatureClient : IWindowsFeatureClient
{
    private static readonly Regex FeatureRow = new(
        @"^(?<name>[^|]+?)\s*\|\s*(?<state>Enabled|Disabled|Enable Pending|Disable Pending)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<WindowsFeatureState>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureSupported();
        var result = await RunAsync(
            ["/Online", "/Get-Features", "/Format:Table", "/English"],
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"DISM discovery завершился ошибкой: {result.Message}");
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => FeatureRow.Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match =>
            {
                var raw = match.Groups["state"].Value.Trim();
                return new WindowsFeatureState(
                    match.Groups["name"].Value.Trim(),
                    raw.StartsWith("Enable", StringComparison.OrdinalIgnoreCase),
                    raw);
            })
            .OrderBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WindowsFeatureState?> GetAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var features = await ListAsync(cancellationToken);
        return features.FirstOrDefault(feature =>
            feature.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<WindowsFeatureOperationResult> EnableAsync(
        string name,
        bool includeParents,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "/Online",
            "/Enable-Feature",
            $"/FeatureName:{name}",
            "/NoRestart",
            "/Quiet",
            "/English"
        };
        if (includeParents)
        {
            arguments.Add("/All");
        }

        var result = await RunAsync(arguments, cancellationToken);
        return new WindowsFeatureOperationResult(result.Succeeded, result.RebootRequired, result.Message);
    }

    public async Task<WindowsFeatureOperationResult> DisableAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                "/Online",
                "/Disable-Feature",
                $"/FeatureName:{name}",
                "/NoRestart",
                "/Quiet",
                "/English"
            ],
            cancellationToken);
        return new WindowsFeatureOperationResult(result.Succeeded, result.RebootRequired, result.Message);
    }

    private static async Task<CommandResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dism.exe",
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

        try
        {
            if (!process.Start())
            {
                return new CommandResult(false, false, string.Empty, "dism.exe не запущен.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("dism.exe не найден.", exception);
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
            throw new PlatformNotSupportedException("Windows Features Provider доступен только в Windows.");
        }
    }

    private sealed record CommandResult(
        bool Succeeded,
        bool RebootRequired,
        string Output,
        string Message);
}

public static class WindowsFeatureProfileMapper
{
    public const string ProviderId = "windows.features";
    public const string ResourceType = "windows.optional-feature";

    public static DesiredProviderState CreateDesiredState(WinStateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new DesiredProviderState(profile.Features.Select(CreateResource).ToArray());
    }

    public static StateResource CreateResource(WindowsFeatureProfile feature)
        => new()
        {
            ProviderId = ProviderId,
            ResourceType = ResourceType,
            Identity = Identity(feature.Name),
            State = feature.State.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? DesiredState.Disabled
                : DesiredState.Enabled,
            Properties = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = StateValue.From(feature.Name),
                ["includeParents"] = StateValue.From(feature.IncludeParents.ToString())
            }
        };

    public static WindowsFeatureProfile FromResource(StateResource resource)
        => new()
        {
            Name = Required(resource, "name"),
            State = resource.State == DesiredState.Disabled ? "disabled" : "enabled",
            IncludeParents = resource.Properties.TryGetValue("includeParents", out var value)
                && bool.TryParse(value.Value, out var parsed)
                && parsed
        };

    public static string Identity(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToUpperInvariant()));
        return $"feature:{Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant()}";
    }

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
}

/// <summary>Управляет Windows Optional Features через DISM и общий Apply Engine.</summary>
public sealed class WindowsFeatureProvider : IStateProvider, IRollbackProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IWindowsFeatureClient _client;

    public WindowsFeatureProvider(IWindowsFeatureClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Id => WindowsFeatureProfileMapper.ProviderId;
    public string DisplayName => "Windows Optional Features";
    public bool IsSupported => _client.IsSupported;
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Capture
        | ProviderCapabilities.Apply
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
                [new ProviderDiagnostic("features.platform.unsupported", "Windows Features Provider доступен только в Windows.", true)]);
        }

        try
        {
            var features = await _client.ListAsync(cancellationToken);
            return new ProviderDiscoveryResult(
                features.Select(feature => new StateResource
                {
                    ProviderId = Id,
                    ResourceType = WindowsFeatureProfileMapper.ResourceType,
                    Identity = WindowsFeatureProfileMapper.Identity(feature.Name),
                    State = feature.Enabled ? DesiredState.Enabled : DesiredState.Disabled,
                    Properties = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["name"] = StateValue.From(feature.Name),
                        ["rawState"] = StateValue.From(feature.RawState)
                    }
                }).ToArray(),
                Array.Empty<ProviderDiagnostic>());
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return new ProviderDiscoveryResult(
                Array.Empty<StateResource>(),
                [new ProviderDiagnostic("features.discovery.failed", exception.Message, true)]);
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
            _ = current.TryGetValue(desired.NormalizedIdentity, out var actual);
            var action = PlanFeature(desired, actual);
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
        _ = context;
        var feature = WindowsFeatureProfileMapper.FromResource(action.Resource);
        WindowsFeatureOperationResult result = action.Operation switch
        {
            ActionType.Enable => await _client.EnableAsync(feature.Name, feature.IncludeParents, cancellationToken),
            ActionType.Disable => await _client.DisableAsync(feature.Name, cancellationToken),
            _ => new WindowsFeatureOperationResult(false, false, $"Операция {action.Operation} не поддерживается Features Provider.")
        };
        return new ActionExecutionResult(
            result.Succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
            result.Message,
            result.RebootRequired
                ? [new ProviderDiagnostic("features.reboot.required", "DISM сообщил о необходимости перезагрузки.", true)]
                : Array.Empty<ProviderDiagnostic>());
    }

    public async Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var feature = WindowsFeatureProfileMapper.FromResource(action.Resource);
        var actual = await _client.GetAsync(feature.Name, cancellationToken);
        if (actual is null)
        {
            return new VerificationResult(false, $"Optional Feature '{feature.Name}' не найден.");
        }

        var expected = feature.State.Equals("enabled", StringComparison.OrdinalIgnoreCase);
        var matches = actual.Enabled == expected;
        return new VerificationResult(
            matches,
            matches
                ? $"Optional Feature '{feature.Name}' подтверждён: {actual.RawState}."
                : $"Optional Feature '{feature.Name}' имеет состояние {actual.RawState}.");
    }

    public async Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.BackupDirectory);
        var feature = WindowsFeatureProfileMapper.FromResource(action.Resource);
        var actual = await _client.GetAsync(feature.Name, cancellationToken);
        if (actual is null)
        {
            return new RollbackPreparationResult(false, null, $"Feature '{feature.Name}' не найден для checkpoint.");
        }

        var payload = new FeatureRollbackPayload
        {
            ActionId = action.Id,
            Name = feature.Name,
            WasEnabled = actual.Enabled,
            IncludeParents = feature.IncludeParents
        };
        var path = Path.Combine(context.BackupDirectory, $"{action.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
        return new RollbackPreparationResult(true, path, "Checkpoint Optional Feature создан.");
    }

    public async Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (!File.Exists(action.BackupReference))
        {
            return new RollbackExecutionResult(false, "Checkpoint Optional Feature не найден.");
        }

        try
        {
            var json = await File.ReadAllTextAsync(action.BackupReference, cancellationToken);
            var payload = JsonSerializer.Deserialize<FeatureRollbackPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Checkpoint Optional Feature повреждён.");
            var result = payload.WasEnabled
                ? await _client.EnableAsync(payload.Name, payload.IncludeParents, cancellationToken)
                : await _client.DisableAsync(payload.Name, cancellationToken);
            return new RollbackExecutionResult(
                result.Succeeded,
                result.Succeeded
                    ? $"Optional Feature '{payload.Name}' восстановлен."
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

    private static PlannedAction? PlanFeature(StateResource desired, StateResource? current)
    {
        var feature = WindowsFeatureProfileMapper.FromResource(desired);
        if (current is null)
        {
            return new PlannedAction
            {
                Id = ActionId(ActionType.Unsupported, desired.NormalizedIdentity),
                ProviderId = WindowsFeatureProfileMapper.ProviderId,
                Resource = desired,
                Operation = ActionType.Unsupported,
                Risk = RiskLevel.High,
                RequiresAdministrator = true,
                MayRequireReboot = false,
                SupportsRollback = false,
                Explanation = $"Optional Feature '{feature.Name}' не найден в DISM inventory."
            };
        }

        var shouldEnable = desired.State == DesiredState.Enabled;
        var isEnabled = current.State == DesiredState.Enabled;
        if (shouldEnable == isEnabled)
        {
            return null;
        }

        var operation = shouldEnable ? ActionType.Enable : ActionType.Disable;
        return new PlannedAction
        {
            Id = ActionId(operation, desired.NormalizedIdentity),
            ProviderId = WindowsFeatureProfileMapper.ProviderId,
            Resource = desired,
            Operation = operation,
            Risk = shouldEnable ? RiskLevel.Medium : RiskLevel.High,
            CurrentProperties = current.Properties,
            DesiredProperties = desired.Properties,
            RequiresAdministrator = true,
            MayRequireReboot = true,
            SupportsRollback = true,
            Explanation = $"{(shouldEnable ? "Включить" : "Отключить")} Optional Feature '{feature.Name}'."
        };
    }

    private static string ActionId(ActionType operation, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{identity}"));
        return $"feature-{operation.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    private sealed record FeatureRollbackPayload
    {
        public string ActionId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool WasEnabled { get; init; }
        public bool IncludeParents { get; init; }
    }
}
