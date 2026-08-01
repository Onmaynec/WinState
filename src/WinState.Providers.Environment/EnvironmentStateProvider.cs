using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Profiles;
using WinState.Domain.Providers;
using WinState.Domain.Resources;

namespace WinState.Providers.EnvironmentVariables;

public enum EnvironmentScope
{
    User,
    Machine
}

public interface IEnvironmentStore
{
    bool IsSupported { get; }

    Task<IReadOnlyDictionary<string, string?>> ReadVariablesAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken);

    Task<string?> ReadVariableAsync(
        EnvironmentScope scope,
        string name,
        CancellationToken cancellationToken);

    Task WriteVariableAsync(
        EnvironmentScope scope,
        string name,
        string? value,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ReadPathAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken);

    Task WritePathAsync(
        EnvironmentScope scope,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken);
}

/// <summary>Реальное хранилище пользовательских и машинных переменных Windows.</summary>
public sealed class WindowsEnvironmentStore : IEnvironmentStore
{
    private static readonly IntPtr BroadcastWindow = new(0xffff);
    private const uint SettingChangeMessage = 0x001A;
    private const uint AbortIfHung = 0x0002;

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<IReadOnlyDictionary<string, string?>> ReadVariablesAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        IDictionary source = System.Environment.GetEnvironmentVariables(ToTarget(scope));
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value?.ToString();
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string?>>(result);
    }

    public Task<string?> ReadVariableAsync(
        EnvironmentScope scope,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        ValidateName(name);
        return Task.FromResult(System.Environment.GetEnvironmentVariable(name, ToTarget(scope)));
    }

    public Task WriteVariableAsync(
        EnvironmentScope scope,
        string name,
        string? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSupported();
        ValidateName(name);
        System.Environment.SetEnvironmentVariable(name, value, ToTarget(scope));
        BroadcastEnvironmentChanged();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ReadPathAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken)
    {
        var value = await ReadVariableAsync(scope, "Path", cancellationToken);
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task WritePathAsync(
        EnvironmentScope scope,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var value = string.Join(';', entries.Where(item => !string.IsNullOrWhiteSpace(item)));
        await WriteVariableAsync(scope, "Path", value, cancellationToken);
    }

    private static EnvironmentVariableTarget ToTarget(EnvironmentScope scope)
        => scope == EnvironmentScope.Machine
            ? EnvironmentVariableTarget.Machine
            : EnvironmentVariableTarget.User;

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Environment Provider изменяет User/Machine environment только в Windows.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('='))
        {
            throw new ArgumentException("Некорректное имя переменной окружения.", nameof(name));
        }
    }

    private static void BroadcastEnvironmentChanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = SendMessageTimeout(
            BroadcastWindow,
            SettingChangeMessage,
            UIntPtr.Zero,
            "Environment",
            AbortIfHung,
            1000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}

/// <summary>Детерминированное in-memory хранилище для unit-тестов.</summary>
public sealed class InMemoryEnvironmentStore : IEnvironmentStore
{
    private readonly Dictionary<EnvironmentScope, Dictionary<string, string?>> _variables;
    private readonly Dictionary<EnvironmentScope, List<string>> _paths;

    public InMemoryEnvironmentStore(
        IReadOnlyDictionary<string, string?>? userVariables = null,
        IReadOnlyDictionary<string, string?>? machineVariables = null,
        IReadOnlyList<string>? userPath = null,
        IReadOnlyList<string>? machinePath = null)
    {
        _variables = new Dictionary<EnvironmentScope, Dictionary<string, string?>>
        {
            [EnvironmentScope.User] = CopyVariables(userVariables),
            [EnvironmentScope.Machine] = CopyVariables(machineVariables)
        };
        _paths = new Dictionary<EnvironmentScope, List<string>>
        {
            [EnvironmentScope.User] = userPath?.ToList() ?? [],
            [EnvironmentScope.Machine] = machinePath?.ToList() ?? []
        };
    }

    public bool IsSupported => true;

    public Task<IReadOnlyDictionary<string, string?>> ReadVariablesAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = new Dictionary<string, string?>(_variables[scope], StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = string.Join(';', _paths[scope])
        };
        return Task.FromResult<IReadOnlyDictionary<string, string?>>(copy);
    }

    public Task<string?> ReadVariableAsync(
        EnvironmentScope scope,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<string?>(string.Join(';', _paths[scope]));
        }

        _ = _variables[scope].TryGetValue(name, out var value);
        return Task.FromResult(value);
    }

    public Task WriteVariableAsync(
        EnvironmentScope scope,
        string name,
        string? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            _paths[scope] = string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        else if (value is null)
        {
            _ = _variables[scope].Remove(name);
        }
        else
        {
            _variables[scope][name] = value;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ReadPathAsync(
        EnvironmentScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(_paths[scope].ToArray());
    }

    public Task WritePathAsync(
        EnvironmentScope scope,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths[scope] = entries.ToList();
        return Task.CompletedTask;
    }

    private static Dictionary<string, string?> CopyVariables(
        IReadOnlyDictionary<string, string?>? source)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var pair in source)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }
}

public static class EnvironmentProfileMapper
{
    public const string ProviderId = "environment";
    public const string VariableResourceType = "environment.variable";
    public const string PathResourceType = "environment.path-entry";

    public static DesiredProviderState CreateDesiredState(WinStateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var resources = new List<StateResource>();
        AddVariables(resources, EnvironmentScope.User, profile.Environment.User);
        AddVariables(resources, EnvironmentScope.Machine, profile.Environment.Machine);
        AddPaths(resources, EnvironmentScope.User, profile.Environment.UserPath);
        AddPaths(resources, EnvironmentScope.Machine, profile.Environment.MachinePath);
        return new DesiredProviderState(resources);
    }

    public static string VariableIdentity(EnvironmentScope scope, string name)
        => $"environment://{ScopeName(scope)}/variable/{StableToken(name)}";

    public static string PathIdentity(EnvironmentScope scope, string path)
        => $"environment://{ScopeName(scope)}/path/{StableToken(NormalizePathIdentity(path))}";

    public static string NormalizePathIdentity(string path)
    {
        var value = path.Trim().Trim('"').Replace('/', '\\');
        if (value.Length > 3)
        {
            value = value.TrimEnd('\\');
        }

        return value;
    }

    public static EnvironmentScope ParseScope(StateResource resource)
    {
        var value = Required(resource, "scope");
        return value.Equals("machine", StringComparison.OrdinalIgnoreCase)
            ? EnvironmentScope.Machine
            : EnvironmentScope.User;
    }

    public static string Required(StateResource resource, string property)
    {
        if (!resource.Properties.TryGetValue(property, out var value)
            || string.IsNullOrWhiteSpace(value.Value))
        {
            throw new InvalidDataException(
                $"Ресурс '{resource.Identity}' не содержит обязательное свойство '{property}'.");
        }

        return value.Value!;
    }

    public static string ScopeName(EnvironmentScope scope)
        => scope == EnvironmentScope.Machine ? "machine" : "user";

    private static void AddVariables(
        ICollection<StateResource> resources,
        EnvironmentScope scope,
        IReadOnlyDictionary<string, string> variables)
    {
        foreach (var pair in variables.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            resources.Add(new StateResource
            {
                ProviderId = ProviderId,
                ResourceType = VariableResourceType,
                Identity = VariableIdentity(scope, pair.Key),
                State = DesiredState.Configured,
                Properties = Properties(
                    ("scope", ScopeName(scope)),
                    ("name", pair.Key),
                    ("value", pair.Value))
            });
        }
    }

    private static void AddPaths(
        ICollection<StateResource> resources,
        EnvironmentScope scope,
        IReadOnlyCollection<PathEntryProfile> paths)
    {
        foreach (var item in paths)
        {
            resources.Add(new StateResource
            {
                ProviderId = ProviderId,
                ResourceType = PathResourceType,
                Identity = PathIdentity(scope, item.Path),
                State = item.State.Equals("absent", StringComparison.OrdinalIgnoreCase)
                    ? DesiredState.Absent
                    : DesiredState.Present,
                Properties = Properties(
                    ("scope", ScopeName(scope)),
                    ("path", item.Path),
                    ("position", item.Position))
            });
        }
    }

    private static IReadOnlyDictionary<string, StateValue> Properties(
        params (string Name, string Value)[] values)
    {
        var result = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            result[value.Name] = StateValue.From(value.Value);
        }

        return result;
    }

    private static string StableToken(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }
}

/// <summary>Первый полный системный провайдер WinState.</summary>
public sealed class EnvironmentStateProvider : IStateProvider, IRollbackProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IEnvironmentStore _store;

    public EnvironmentStateProvider(IEnvironmentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Id => EnvironmentProfileMapper.ProviderId;
    public string DisplayName => "Windows Environment";
    public bool IsSupported => _store.IsSupported;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Capture
        | ProviderCapabilities.Apply
        | ProviderCapabilities.Rollback
        | ProviderCapabilities.Remove
        | ProviderCapabilities.MayRequireAdministrator;

    public async Task<ProviderDiscoveryResult> DiscoverAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (!_store.IsSupported)
        {
            return new ProviderDiscoveryResult(
                Array.Empty<StateResource>(),
                [new ProviderDiagnostic(
                    "environment.platform.unsupported",
                    "Environment Provider доступен только в Windows.",
                    true)]);
        }

        var resources = new List<StateResource>();
        var diagnostics = new List<ProviderDiagnostic>();
        foreach (var scope in new[] { EnvironmentScope.User, EnvironmentScope.Machine })
        {
            try
            {
                var variables = await _store.ReadVariablesAsync(scope, cancellationToken);
                foreach (var pair in variables
                    .Where(item => !item.Key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    resources.Add(new StateResource
                    {
                        ProviderId = Id,
                        ResourceType = EnvironmentProfileMapper.VariableResourceType,
                        Identity = EnvironmentProfileMapper.VariableIdentity(scope, pair.Key),
                        State = DesiredState.Configured,
                        Properties = Properties(
                            ("scope", EnvironmentProfileMapper.ScopeName(scope)),
                            ("name", pair.Key),
                            ("value", pair.Value ?? string.Empty),
                            ("exists", "true"))
                    });
                }

                var paths = await _store.ReadPathAsync(scope, cancellationToken);
                for (var index = 0; index < paths.Count; index++)
                {
                    var path = paths[index];
                    resources.Add(new StateResource
                    {
                        ProviderId = Id,
                        ResourceType = EnvironmentProfileMapper.PathResourceType,
                        Identity = EnvironmentProfileMapper.PathIdentity(scope, path),
                        State = DesiredState.Present,
                        Properties = Properties(
                            ("scope", EnvironmentProfileMapper.ScopeName(scope)),
                            ("path", path),
                            ("index", index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            ("count", paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    });
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
            {
                diagnostics.Add(new ProviderDiagnostic(
                    $"environment.discovery.{EnvironmentProfileMapper.ScopeName(scope)}.failed",
                    exception.Message,
                    true));
            }
        }

        return new ProviderDiscoveryResult(resources, diagnostics);
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
            .GroupBy(item => item.NormalizedIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var actions = new List<PlannedAction>();
        foreach (var desired in desiredState.Resources
            .Where(item => item.ProviderId.Equals(Id, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = current.TryGetValue(desired.NormalizedIdentity, out var existing);
            var action = desired.ResourceType switch
            {
                EnvironmentProfileMapper.VariableResourceType => PlanVariable(desired, existing),
                EnvironmentProfileMapper.PathResourceType => PlanPath(desired, existing),
                _ => null
            };
            if (action is not null)
            {
                actions.Add(action);
            }
        }

        ChainPathActions(actions);
        return Task.FromResult<IReadOnlyCollection<PlannedAction>>(actions);
    }

    public async Task<ActionExecutionResult> ApplyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = context;
        try
        {
            EnsureSupported();
            var scope = EnvironmentProfileMapper.ParseScope(action.Resource);
            if (action.Resource.ResourceType == EnvironmentProfileMapper.VariableResourceType)
            {
                await _store.WriteVariableAsync(
                    scope,
                    EnvironmentProfileMapper.Required(action.Resource, "name"),
                    EnvironmentProfileMapper.Required(action.Resource, "value"),
                    cancellationToken);
            }
            else if (action.Resource.ResourceType == EnvironmentProfileMapper.PathResourceType)
            {
                await ApplyPathAsync(action.Resource, scope, cancellationToken);
            }
            else
            {
                return new ActionExecutionResult(
                    ActionStatus.ManualActionRequired,
                    $"Тип ресурса '{action.Resource.ResourceType}' не поддерживается Environment Provider.",
                    Array.Empty<ProviderDiagnostic>());
            }

            return new ActionExecutionResult(
                ActionStatus.Succeeded,
                action.Explanation,
                Array.Empty<ProviderDiagnostic>());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or InvalidDataException
            or PlatformNotSupportedException)
        {
            return new ActionExecutionResult(
                ActionStatus.Failed,
                exception.Message,
                [new ProviderDiagnostic("environment.apply.failed", exception.Message)]);
        }
    }

    public async Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = context;
        EnsureSupported();
        var scope = EnvironmentProfileMapper.ParseScope(action.Resource);
        if (action.Resource.ResourceType == EnvironmentProfileMapper.VariableResourceType)
        {
            var name = EnvironmentProfileMapper.Required(action.Resource, "name");
            var desired = EnvironmentProfileMapper.Required(action.Resource, "value");
            var actual = await _store.ReadVariableAsync(scope, name, cancellationToken);
            var matches = string.Equals(actual, desired, StringComparison.Ordinal);
            return new VerificationResult(
                matches,
                matches
                    ? $"Переменная {name} подтверждена."
                    : $"Переменная {name} отличается после применения.");
        }

        if (action.Resource.ResourceType == EnvironmentProfileMapper.PathResourceType)
        {
            var desiredPath = EnvironmentProfileMapper.Required(action.Resource, "path");
            var identity = EnvironmentProfileMapper.NormalizePathIdentity(desiredPath);
            var entries = await _store.ReadPathAsync(scope, cancellationToken);
            var index = entries
                .Select((value, position) => new { value, position })
                .FirstOrDefault(item => EnvironmentProfileMapper.NormalizePathIdentity(item.value)
                    .Equals(identity, StringComparison.OrdinalIgnoreCase))?.position ?? -1;
            var position = action.Resource.Properties.TryGetValue("position", out var value)
                ? value.Value
                : "append";
            var positionMatches = index < 0
                || (position != "prepend" && position != "append")
                || (position == "prepend" && index == 0)
                || (position == "append" && index == entries.Count - 1);
            var shouldExist = action.Resource.State != DesiredState.Absent;
            var matches = shouldExist ? index >= 0 && positionMatches : index < 0;
            return new VerificationResult(
                matches,
                matches
                    ? $"PATH entry '{desiredPath}' подтверждён."
                    : $"PATH entry '{desiredPath}' не соответствует плану.");
        }

        return new VerificationResult(false, "Неизвестный тип ресурса.");
    }

    public async Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        EnsureSupported();
        Directory.CreateDirectory(context.BackupDirectory);
        var scope = EnvironmentProfileMapper.ParseScope(action.Resource);
        EnvironmentRollbackPayload payload;
        if (action.Resource.ResourceType == EnvironmentProfileMapper.VariableResourceType)
        {
            var name = EnvironmentProfileMapper.Required(action.Resource, "name");
            var variables = await _store.ReadVariablesAsync(scope, cancellationToken);
            var existed = variables.ContainsKey(name);
            _ = variables.TryGetValue(name, out var value);
            payload = new EnvironmentRollbackPayload
            {
                ActionId = action.Id,
                ResourceType = action.Resource.ResourceType,
                Scope = scope,
                Name = name,
                VariableExisted = existed,
                VariableValue = value
            };
        }
        else if (action.Resource.ResourceType == EnvironmentProfileMapper.PathResourceType)
        {
            payload = new EnvironmentRollbackPayload
            {
                ActionId = action.Id,
                ResourceType = action.Resource.ResourceType,
                Scope = scope,
                PathEntries = await _store.ReadPathAsync(scope, cancellationToken)
            };
        }
        else
        {
            return new RollbackPreparationResult(false, null, "Тип ресурса не поддерживает rollback.");
        }

        var path = Path.Combine(context.BackupDirectory, $"{action.Id}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
        return new RollbackPreparationResult(true, path, "Checkpoint действия создан.");
    }

    public async Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = context;
        EnsureSupported();
        if (!File.Exists(action.BackupReference))
        {
            return new RollbackExecutionResult(false, $"Checkpoint не найден: {action.BackupReference}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(action.BackupReference, cancellationToken);
            var payload = JsonSerializer.Deserialize<EnvironmentRollbackPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("Checkpoint Environment Provider повреждён.");
            if (payload.ResourceType == EnvironmentProfileMapper.VariableResourceType)
            {
                if (string.IsNullOrWhiteSpace(payload.Name))
                {
                    throw new InvalidDataException("Checkpoint не содержит имя переменной.");
                }

                await _store.WriteVariableAsync(
                    payload.Scope,
                    payload.Name,
                    payload.VariableExisted ? payload.VariableValue : null,
                    cancellationToken);
            }
            else if (payload.ResourceType == EnvironmentProfileMapper.PathResourceType)
            {
                await _store.WritePathAsync(
                    payload.Scope,
                    payload.PathEntries ?? Array.Empty<string>(),
                    cancellationToken);
            }
            else
            {
                throw new InvalidDataException("Checkpoint содержит неизвестный тип ресурса.");
            }

            return new RollbackExecutionResult(true, $"Действие {action.ActionId} откатено.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or PlatformNotSupportedException)
        {
            return new RollbackExecutionResult(false, exception.Message);
        }
    }

    private PlannedAction? PlanVariable(StateResource desired, StateResource? current)
    {
        var desiredValue = EnvironmentProfileMapper.Required(desired, "value");
        var currentValue = current?.Properties.TryGetValue("value", out var value) == true
            ? value.Value
            : null;
        if (current is not null && string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
        {
            return null;
        }

        var scope = EnvironmentProfileMapper.ParseScope(desired);
        var name = EnvironmentProfileMapper.Required(desired, "name");
        return CreateAction(
            desired,
            current is null ? ActionType.Create : ActionType.Modify,
            scope,
            current?.Properties ?? EmptyProperties(),
            desired.Properties,
            current is null
                ? $"Создать {EnvironmentProfileMapper.ScopeName(scope)} переменную '{name}'."
                : $"Изменить {EnvironmentProfileMapper.ScopeName(scope)} переменную '{name}'.");
    }

    private PlannedAction? PlanPath(StateResource desired, StateResource? current)
    {
        var scope = EnvironmentProfileMapper.ParseScope(desired);
        var path = EnvironmentProfileMapper.Required(desired, "path");
        if (desired.State == DesiredState.Absent)
        {
            return current is null
                ? null
                : CreateAction(
                    desired,
                    ActionType.Remove,
                    scope,
                    current.Properties,
                    desired.Properties,
                    $"Удалить PATH entry '{path}' из {EnvironmentProfileMapper.ScopeName(scope)} scope.");
        }

        if (current is null)
        {
            return CreateAction(
                desired,
                ActionType.Create,
                scope,
                EmptyProperties(),
                desired.Properties,
                $"Добавить PATH entry '{path}' в {EnvironmentProfileMapper.ScopeName(scope)} scope.");
        }

        var position = desired.Properties.TryGetValue("position", out var value)
            ? value.Value
            : "append";
        var index = ParseInteger(current, "index");
        var count = ParseInteger(current, "count");
        var needsReorder = position == "prepend" && index != 0
            || position == "append" && index != count - 1;
        return needsReorder
            ? CreateAction(
                desired,
                ActionType.Reorder,
                scope,
                current.Properties,
                desired.Properties,
                $"Переместить PATH entry '{path}' в позицию {position}.")
            : null;
    }

    private static PlannedAction CreateAction(
        StateResource desired,
        ActionType operation,
        EnvironmentScope scope,
        IReadOnlyDictionary<string, StateValue> current,
        IReadOnlyDictionary<string, StateValue> target,
        string explanation)
    {
        return new PlannedAction
        {
            Id = ActionId(operation, desired.NormalizedIdentity),
            ProviderId = EnvironmentProfileMapper.ProviderId,
            Resource = desired,
            Operation = operation,
            Risk = scope == EnvironmentScope.Machine ? RiskLevel.Medium : RiskLevel.Low,
            CurrentProperties = current,
            DesiredProperties = target,
            RequiresAdministrator = scope == EnvironmentScope.Machine,
            SupportsRollback = true,
            Explanation = explanation
        };
    }

    private static void ChainPathActions(IList<PlannedAction> actions)
    {
        foreach (var scope in new[] { "user", "machine" })
        {
            string? previous = null;
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                if (action.Resource.ResourceType != EnvironmentProfileMapper.PathResourceType
                    || !action.Resource.Properties.TryGetValue("scope", out var value)
                    || !string.Equals(value.Value, scope, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                actions[index] = action with
                {
                    DependsOn = previous is null ? Array.Empty<string>() : [previous]
                };
                previous = action.Id;
            }
        }
    }

    private async Task ApplyPathAsync(
        StateResource resource,
        EnvironmentScope scope,
        CancellationToken cancellationToken)
    {
        var desiredPath = EnvironmentProfileMapper.Required(resource, "path");
        var identity = EnvironmentProfileMapper.NormalizePathIdentity(desiredPath);
        var entries = (await _store.ReadPathAsync(scope, cancellationToken)).ToList();
        entries.RemoveAll(item => EnvironmentProfileMapper.NormalizePathIdentity(item)
            .Equals(identity, StringComparison.OrdinalIgnoreCase));
        if (resource.State != DesiredState.Absent)
        {
            var position = resource.Properties.TryGetValue("position", out var value)
                ? value.Value
                : "append";
            if (position == "prepend")
            {
                entries.Insert(0, desiredPath);
            }
            else
            {
                entries.Add(desiredPath);
            }
        }

        await _store.WritePathAsync(scope, entries, cancellationToken);
    }

    private void EnsureSupported()
    {
        if (!_store.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Environment Provider изменяет User/Machine environment только в Windows.");
        }
    }

    private static int ParseInteger(StateResource resource, string property)
    {
        return resource.Properties.TryGetValue(property, out var value)
            && int.TryParse(
                value.Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result)
            ? result
            : -1;
    }

    private static string ActionId(ActionType operation, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{identity}"));
        return $"env-{operation.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }

    private static IReadOnlyDictionary<string, StateValue> Properties(
        params (string Name, string Value)[] values)
    {
        var result = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            result[value.Name] = StateValue.From(value.Value);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, StateValue> EmptyProperties()
        => new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);

    private sealed record EnvironmentRollbackPayload
    {
        public string ActionId { get; init; } = string.Empty;
        public string ResourceType { get; init; } = string.Empty;
        public EnvironmentScope Scope { get; init; }
        public string? Name { get; init; }
        public bool VariableExisted { get; init; }
        public string? VariableValue { get; init; }
        public IReadOnlyList<string>? PathEntries { get; init; }
    }
}
