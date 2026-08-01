using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Providers;
using WinState.Domain.Resources;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WinState.Providers.SystemControl;

public sealed record RegistryValueProfile
{
    public required string Hive { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string State { get; init; } = "present";
    public string Type { get; init; } = "string";
    public string? Value { get; init; }
    public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
}

public sealed record WindowsServiceProfile
{
    public required string Name { get; init; }
    public string State { get; init; } = "running";
    public string StartMode { get; init; } = "unchanged";
    public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
}

public sealed record StartupEntryProfile
{
    public required string Name { get; init; }
    public string Scope { get; init; } = "user";
    public string State { get; init; } = "present";
    public string? Command { get; init; }
    public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
}

public sealed record ScheduledTaskProfile
{
    public required string Name { get; init; }
    public string State { get; init; } = "present";
    public string Schedule { get; init; } = "logon";
    public string? Time { get; init; }
    public string RunLevel { get; init; } = "limited";
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
}

public sealed record WindowsSystemProfile(
    IReadOnlyCollection<RegistryValueProfile> Registry,
    IReadOnlyCollection<WindowsServiceProfile> Services,
    IReadOnlyCollection<StartupEntryProfile> Startup,
    IReadOnlyCollection<ScheduledTaskProfile> Tasks)
{
    public bool HasResources => Registry.Count + Services.Count + Startup.Count + Tasks.Count > 0;
}

/// <summary>Загружает system-control секции из основного YAML-профиля, включая includes/extends и variables.</summary>
public sealed class WindowsSystemProfileLoader
{
    private static readonly Regex VariablePattern = new(
        @"\{\{\s*(?<braced>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}|\$\{(?<shell>[A-Za-z_][A-Za-z0-9_.-]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<WindowsSystemProfile> LoadAsync(
        string path,
        IReadOnlyDictionary<string, string>? overrides,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var document = await LoadRecursiveAsync(
            fullPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        var variables = BuildVariables(document.Variables, overrides, environment, fullPath);
        var profile = new WindowsSystemProfile(
            NormalizeRegistry(document.Registry, variables),
            NormalizeServices(document.Services, variables),
            NormalizeStartup(document.Startup, variables),
            NormalizeTasks(document.Tasks, variables));
        Validate(profile);
        return profile;
    }

    private async Task<SystemDocument> LoadRecursiveAsync(
        string path,
        ISet<string> active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!active.Add(fullPath))
        {
            throw new InvalidDataException($"Обнаружен цикл system-control includes/extends: {fullPath}");
        }

        try
        {
            SystemDocument local;
            try
            {
                var yaml = await File.ReadAllTextAsync(fullPath, cancellationToken);
                local = _deserializer.Deserialize<SystemDocument>(yaml) ?? new SystemDocument();
            }
            catch (YamlException exception)
            {
                throw new InvalidDataException($"Некорректный YAML в '{fullPath}': {exception.Message}", exception);
            }

            var merged = new SystemDocument();
            foreach (var reference in local.Extends.Concat(local.Includes))
            {
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                var expanded = Environment.ExpandEnvironmentVariables(reference.Trim());
                var referenced = Path.GetFullPath(
                    Path.IsPathRooted(expanded)
                        ? expanded
                        : Path.Combine(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory, expanded));
                if (!File.Exists(referenced))
                {
                    throw new FileNotFoundException($"Связанный system-control профиль '{reference}' не найден.", referenced);
                }

                merged = Merge(merged, await LoadRecursiveAsync(referenced, active, cancellationToken));
            }

            return Merge(merged, local);
        }
        finally
        {
            _ = active.Remove(fullPath);
        }
    }

    private static SystemDocument Merge(SystemDocument baseline, SystemDocument overlay)
        => new()
        {
            Variables = MergeDictionary(baseline.Variables, overlay.Variables),
            Registry = baseline.Registry.Concat(overlay.Registry).ToList(),
            Services = baseline.Services.Concat(overlay.Services).ToList(),
            Startup = baseline.Startup.Concat(overlay.Startup).ToList(),
            Tasks = baseline.Tasks.Concat(overlay.Tasks).ToList(),
            Includes = overlay.Includes.ToList(),
            Extends = overlay.Extends.ToList()
        };

    private static Dictionary<string, string> MergeDictionary(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> overlay)
    {
        var result = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overlay)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildVariables(
        IReadOnlyDictionary<string, string> defaults,
        IReadOnlyDictionary<string, string>? overrides,
        IReadOnlyDictionary<string, string?> environment,
        string profilePath)
    {
        var values = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["profileFile"] = profilePath,
            ["profileDirectory"] = Path.GetDirectoryName(profilePath) ?? Environment.CurrentDirectory
        };
        foreach (var pair in environment)
        {
            const string prefix = "WINSTATE_VAR_";
            if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
            {
                values[pair.Key[prefix.Length..]] = pair.Value;
            }
        }

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return values;
    }

    private static IReadOnlyCollection<RegistryValueProfile> NormalizeRegistry(
        IReadOnlyCollection<RegistryDocument> source,
        IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, RegistryValueProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var hive = Token(Resolve(item.Hive, variables), "HKCU").ToUpperInvariant();
            var path = Resolve(item.Path, variables).Trim().Trim('\\');
            var name = Resolve(item.Name, variables);
            result[$"{hive}|{path}|{name}"] = new RegistryValueProfile
            {
                Hive = hive,
                Path = path,
                Name = name,
                State = Token(item.State, "present"),
                Type = Token(item.Type, "string"),
                Value = item.Value is null ? null : Resolve(item.Value, variables),
                DependsOn = ResolveList(item.DependsOn, variables)
            };
        }

        return result.Values.OrderBy(value => value.Hive).ThenBy(value => value.Path).ThenBy(value => value.Name).ToArray();
    }

    private static IReadOnlyCollection<WindowsServiceProfile> NormalizeServices(
        IReadOnlyCollection<ServiceDocument> source,
        IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, WindowsServiceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var name = Resolve(item.Name, variables);
            result[name] = new WindowsServiceProfile
            {
                Name = name,
                State = Token(item.State, "running"),
                StartMode = Token(item.StartMode, "unchanged"),
                DependsOn = ResolveList(item.DependsOn, variables)
            };
        }

        return result.Values.OrderBy(value => value.Name).ToArray();
    }

    private static IReadOnlyCollection<StartupEntryProfile> NormalizeStartup(
        IReadOnlyCollection<StartupDocument> source,
        IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, StartupEntryProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var name = Resolve(item.Name, variables);
            var scope = Token(item.Scope, "user");
            result[$"{scope}|{name}"] = new StartupEntryProfile
            {
                Name = name,
                Scope = scope,
                State = Token(item.State, "present"),
                Command = item.Command is null ? null : Resolve(item.Command, variables),
                DependsOn = ResolveList(item.DependsOn, variables)
            };
        }

        return result.Values.OrderBy(value => value.Scope).ThenBy(value => value.Name).ToArray();
    }

    private static IReadOnlyCollection<ScheduledTaskProfile> NormalizeTasks(
        IReadOnlyCollection<TaskDocument> source,
        IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, ScheduledTaskProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var name = Resolve(item.Name, variables);
            result[name] = new ScheduledTaskProfile
            {
                Name = name,
                State = Token(item.State, "present"),
                Schedule = Token(item.Schedule, "logon"),
                Time = item.Time is null ? null : Resolve(item.Time, variables),
                RunLevel = Token(item.RunLevel, "limited"),
                Command = item.Command is null ? null : Resolve(item.Command, variables),
                Arguments = item.Arguments is null ? null : Resolve(item.Arguments, variables),
                DependsOn = ResolveList(item.DependsOn, variables)
            };
        }

        return result.Values.OrderBy(value => value.Name).ToArray();
    }

    private static IReadOnlyCollection<string> ResolveList(
        IReadOnlyCollection<string> values,
        IReadOnlyDictionary<string, string> variables)
        => values.Select(value => Resolve(value, variables))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Resolve(string? value, IReadOnlyDictionary<string, string> variables)
        => VariablePattern.Replace(value ?? string.Empty, match =>
        {
            var name = match.Groups["braced"].Success
                ? match.Groups["braced"].Value
                : match.Groups["shell"].Value;
            return variables.TryGetValue(name, out var replacement)
                ? replacement
                : throw new InvalidDataException($"Не задана переменная system-control профиля '{name}'.");
        }).Trim();

    private static string Token(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static void Validate(WindowsSystemProfile profile)
    {
        foreach (var value in profile.Registry)
        {
            if (value.Hive is not ("HKCU" or "HKLM"))
            {
                throw new InvalidDataException($"Registry hive '{value.Hive}' не поддерживается. Разрешены HKCU и HKLM.");
            }

            if (string.IsNullOrWhiteSpace(value.Name) || string.IsNullOrWhiteSpace(value.Path))
            {
                throw new InvalidDataException("Registry resource требует path и name.");
            }

            var allowlisted = value.Hive == "HKCU"
                ? value.Path.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) || value.Path.Equals("Software", StringComparison.OrdinalIgnoreCase)
                : value.Path.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase) || value.Path.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase);
            if (!allowlisted)
            {
                throw new InvalidDataException($"Registry path '{value.Hive}\\{value.Path}' не входит в allowlist Software.");
            }

            if (value.State is not ("present" or "absent"))
            {
                throw new InvalidDataException($"Registry state '{value.State}' не поддерживается.");
            }

            if (value.Type is not ("string" or "expandstring" or "dword" or "qword" or "multistring" or "binary"))
            {
                throw new InvalidDataException($"Registry type '{value.Type}' не поддерживается.");
            }

            if (value.State == "present" && value.Value is null)
            {
                throw new InvalidDataException($"Registry value '{value.Name}' требует value.");
            }
        }

        foreach (var service in profile.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Name))
            {
                throw new InvalidDataException("Service resource требует name.");
            }

            if (service.State is not ("running" or "stopped" or "unchanged"))
            {
                throw new InvalidDataException($"Service state '{service.State}' не поддерживается.");
            }

            if (service.StartMode is not ("automatic" or "manual" or "disabled" or "unchanged"))
            {
                throw new InvalidDataException($"Service startMode '{service.StartMode}' не поддерживается.");
            }
        }

        foreach (var entry in profile.Startup)
        {
            if (entry.Scope is not ("user" or "machine") || entry.State is not ("present" or "absent"))
            {
                throw new InvalidDataException($"Startup entry '{entry.Name}' имеет неподдерживаемые scope/state.");
            }

            if (entry.State == "present" && string.IsNullOrWhiteSpace(entry.Command))
            {
                throw new InvalidDataException($"Startup entry '{entry.Name}' требует command.");
            }
        }

        foreach (var task in profile.Tasks)
        {
            if (task.State is not ("present" or "absent")
                || task.Schedule is not ("logon" or "startup" or "daily")
                || task.RunLevel is not ("limited" or "highest"))
            {
                throw new InvalidDataException($"Scheduled task '{task.Name}' имеет неподдерживаемую конфигурацию.");
            }

            if (task.State == "present" && string.IsNullOrWhiteSpace(task.Command))
            {
                throw new InvalidDataException($"Scheduled task '{task.Name}' требует command.");
            }

            if (task.Schedule == "daily"
                && !System.TimeOnly.TryParseExact(task.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                throw new InvalidDataException($"Scheduled task '{task.Name}' требует time в формате HH:mm.");
            }
        }
    }

    private sealed class SystemDocument
    {
        public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RegistryDocument> Registry { get; set; } = [];
        public List<ServiceDocument> Services { get; set; } = [];
        public List<StartupDocument> Startup { get; set; } = [];
        public List<TaskDocument> Tasks { get; set; } = [];
        public List<string> Includes { get; set; } = [];
        public List<string> Extends { get; set; } = [];
    }

    private sealed class RegistryDocument
    {
        public string Hive { get; set; } = "HKCU";
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "present";
        public string Type { get; set; } = "string";
        public string? Value { get; set; }
        public List<string> DependsOn { get; set; } = [];
    }

    private sealed class ServiceDocument
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "running";
        public string StartMode { get; set; } = "unchanged";
        public List<string> DependsOn { get; set; } = [];
    }

    private sealed class StartupDocument
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "user";
        public string State { get; set; } = "present";
        public string? Command { get; set; }
        public List<string> DependsOn { get; set; } = [];
    }

    private sealed class TaskDocument
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "present";
        public string Schedule { get; set; } = "logon";
        public string? Time { get; set; }
        public string RunLevel { get; set; } = "limited";
        public string? Command { get; set; }
        public string? Arguments { get; set; }
        public List<string> DependsOn { get; set; } = [];
    }
}

public sealed record RegistryValueSnapshot(bool Exists, string Type, string? Value);
public sealed record ServiceSnapshot(bool Exists, string State, string StartMode);
public sealed record StartupEntrySnapshot(bool Exists, string? Command);
public sealed record ScheduledTaskSnapshot(bool Exists, string? Xml);
public sealed record WindowsSystemOperationResult(bool Succeeded, string Message);

public interface IWindowsSystemClient
{
    bool IsSupported { get; }
    Task<RegistryValueSnapshot> GetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> SetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> DeleteRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken);
    Task<ServiceSnapshot> GetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> SetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken);
    Task<StartupEntrySnapshot> GetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> SetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> DeleteStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken);
    Task<ScheduledTaskSnapshot> GetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> SetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> DeleteTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken);
    Task<WindowsSystemOperationResult> RestoreTaskAsync(string name, string xml, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemClient : IWindowsSystemClient
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<RegistryValueSnapshot> GetRegistryAsync(
        RegistryValueProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Hive);
        using var key = baseKey.OpenSubKey(profile.Path, false);
        if (key is null || !key.GetValueNames().Contains(profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(new RegistryValueSnapshot(false, profile.Type, null));
        }

        var value = key.GetValue(profile.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = key.GetValueKind(profile.Name);
        return Task.FromResult(new RegistryValueSnapshot(true, KindToken(kind), SerializeRegistryValue(value, kind)));
    }

    public Task<WindowsSystemOperationResult> SetRegistryAsync(
        RegistryValueProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Hive);
        using var key = baseKey.CreateSubKey(profile.Path, true)
            ?? throw new InvalidOperationException($"Не удалось открыть Registry key {profile.Hive}\\{profile.Path}.");
        var kind = ParseKind(profile.Type);
        key.SetValue(profile.Name, ParseRegistryValue(profile.Value, kind), kind);
        return Task.FromResult(new WindowsSystemOperationResult(true, $"Registry value {profile.Hive}\\{profile.Path}\\{profile.Name} записан."));
    }

    public Task<WindowsSystemOperationResult> DeleteRegistryAsync(
        RegistryValueProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Hive);
        using var key = baseKey.OpenSubKey(profile.Path, true);
        key?.DeleteValue(profile.Name, false);
        return Task.FromResult(new WindowsSystemOperationResult(true, $"Registry value {profile.Name} удалён."));
    }

    public async Task<ServiceSnapshot> GetServiceAsync(
        WindowsServiceProfile profile,
        CancellationToken cancellationToken)
    {
        var query = await RunAsync("sc.exe", ["query", profile.Name], cancellationToken);
        if (query.ExitCode != 0)
        {
            return new ServiceSnapshot(false, "missing", "missing");
        }

        var stateMatch = Regex.Match(query.Output, @"STATE\s*:\s*\d+\s+(?<state>[A-Z_]+)", RegexOptions.IgnoreCase);
        var qc = await RunAsync("sc.exe", ["qc", profile.Name], cancellationToken);
        var modeMatch = Regex.Match(qc.Output, @"START_TYPE\s*:\s*\d+\s+(?<mode>[A-Z_]+)", RegexOptions.IgnoreCase);
        var state = stateMatch.Groups["state"].Value.Equals("RUNNING", StringComparison.OrdinalIgnoreCase)
            ? "running"
            : "stopped";
        var mode = modeMatch.Groups["mode"].Value.ToUpperInvariant() switch
        {
            "AUTO_START" => "automatic",
            "DISABLED" => "disabled",
            _ => "manual"
        };
        return new ServiceSnapshot(true, state, mode);
    }

    public async Task<WindowsSystemOperationResult> SetServiceAsync(
        WindowsServiceProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.StartMode != "unchanged")
        {
            var mode = profile.StartMode switch
            {
                "automatic" => "auto",
                "disabled" => "disabled",
                _ => "demand"
            };
            var configure = await RunAsync("sc.exe", ["config", profile.Name, "start=", mode], cancellationToken);
            if (configure.ExitCode != 0)
            {
                return new WindowsSystemOperationResult(false, configure.Message);
            }
        }

        if (profile.State != "unchanged")
        {
            var operation = profile.State == "running" ? "start" : "stop";
            var result = await RunAsync("sc.exe", [operation, profile.Name], cancellationToken);
            if (result.ExitCode != 0)
            {
                return new WindowsSystemOperationResult(false, result.Message);
            }
        }

        return new WindowsSystemOperationResult(true, $"Service {profile.Name} приведён к целевому состоянию.");
    }

    public Task<StartupEntrySnapshot> GetStartupAsync(
        StartupEntryProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Scope == "machine" ? "HKLM" : "HKCU");
        using var key = baseKey.OpenSubKey(StartupKey, false);
        var value = key?.GetValue(profile.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        return Task.FromResult(new StartupEntrySnapshot(value is not null, value));
    }

    public Task<WindowsSystemOperationResult> SetStartupAsync(
        StartupEntryProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Scope == "machine" ? "HKLM" : "HKCU");
        using var key = baseKey.CreateSubKey(StartupKey, true)
            ?? throw new InvalidOperationException("Не удалось открыть Windows Startup registry key.");
        key.SetValue(profile.Name, profile.Command ?? string.Empty, RegistryValueKind.String);
        return Task.FromResult(new WindowsSystemOperationResult(true, $"Startup entry {profile.Name} записан."));
    }

    public Task<WindowsSystemOperationResult> DeleteStartupAsync(
        StartupEntryProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var baseKey = OpenBase(profile.Scope == "machine" ? "HKLM" : "HKCU");
        using var key = baseKey.OpenSubKey(StartupKey, true);
        key?.DeleteValue(profile.Name, false);
        return Task.FromResult(new WindowsSystemOperationResult(true, $"Startup entry {profile.Name} удалён."));
    }

    public async Task<ScheduledTaskSnapshot> GetTaskAsync(
        ScheduledTaskProfile profile,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync("schtasks.exe", ["/Query", "/TN", profile.Name, "/XML"], cancellationToken);
        return result.ExitCode == 0
            ? new ScheduledTaskSnapshot(true, result.Output)
            : new ScheduledTaskSnapshot(false, null);
    }

    public async Task<WindowsSystemOperationResult> SetTaskAsync(
        ScheduledTaskProfile profile,
        CancellationToken cancellationToken)
    {
        var schedule = profile.Schedule switch
        {
            "startup" => "ONSTART",
            "daily" => "DAILY",
            _ => "ONLOGON"
        };
        var command = QuoteCommand(profile.Command ?? string.Empty, profile.Arguments);
        var arguments = new List<string>
        {
            "/Create", "/TN", profile.Name, "/TR", command, "/SC", schedule,
            "/RL", profile.RunLevel == "highest" ? "HIGHEST" : "LIMITED", "/F"
        };
        if (profile.Schedule == "daily")
        {
            arguments.Add("/ST");
            arguments.Add(profile.Time!);
        }

        var result = await RunAsync("schtasks.exe", arguments, cancellationToken);
        return new WindowsSystemOperationResult(result.ExitCode == 0, result.Message);
    }

    public async Task<WindowsSystemOperationResult> DeleteTaskAsync(
        ScheduledTaskProfile profile,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync("schtasks.exe", ["/Delete", "/TN", profile.Name, "/F"], cancellationToken);
        return new WindowsSystemOperationResult(result.ExitCode == 0, result.Message);
    }

    public async Task<WindowsSystemOperationResult> RestoreTaskAsync(
        string name,
        string xml,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"winstate-task-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(path, xml, Encoding.Unicode, cancellationToken);
        try
        {
            var result = await RunAsync("schtasks.exe", ["/Create", "/TN", name, "/XML", path, "/F"], cancellationToken);
            return new WindowsSystemOperationResult(result.ExitCode == 0, result.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RegistryKey OpenBase(string hive)
        => RegistryKey.OpenBaseKey(
            hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                ? RegistryHive.LocalMachine
                : RegistryHive.CurrentUser,
            RegistryView.Default);

    private static RegistryValueKind ParseKind(string type)
        => type.ToLowerInvariant() switch
        {
            "expandstring" => RegistryValueKind.ExpandString,
            "dword" => RegistryValueKind.DWord,
            "qword" => RegistryValueKind.QWord,
            "multistring" => RegistryValueKind.MultiString,
            "binary" => RegistryValueKind.Binary,
            _ => RegistryValueKind.String
        };

    private static string KindToken(RegistryValueKind kind)
        => kind switch
        {
            RegistryValueKind.ExpandString => "expandstring",
            RegistryValueKind.DWord => "dword",
            RegistryValueKind.QWord => "qword",
            RegistryValueKind.MultiString => "multistring",
            RegistryValueKind.Binary => "binary",
            _ => "string"
        };

    private static object ParseRegistryValue(string? value, RegistryValueKind kind)
        => kind switch
        {
            RegistryValueKind.DWord => int.Parse(value ?? "0", CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(value ?? "0", CultureInfo.InvariantCulture),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value ?? "[]") ?? Array.Empty<string>(),
            RegistryValueKind.Binary => Convert.FromBase64String(value ?? string.Empty),
            _ => value ?? string.Empty
        };

    private static string? SerializeRegistryValue(object? value, RegistryValueKind kind)
        => value is null
            ? null
            : kind switch
            {
                RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
                RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };

    private static string QuoteCommand(string command, string? arguments)
    {
        var quoted = command.Contains(' ') ? $"\"{command.Replace("\"", "", StringComparison.Ordinal)}\"" : command;
        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments}";
    }

    private static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
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
                return new CommandResult(-1, string.Empty, $"{fileName} не запущен.");
            }
        }
        catch (Win32Exception exception)
        {
            return new CommandResult(-1, string.Empty, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        var message = string.Join(" ", (string.IsNullOrWhiteSpace(error) ? output : error)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new CommandResult(process.ExitCode, output, message);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Message);
}

public static class WindowsSystemProfileMapper
{
    public const string ProviderId = "windows.system";
    public const string RegistryType = "windows.registry-value";
    public const string ServiceType = "windows.service";
    public const string StartupType = "windows.startup-entry";
    public const string TaskType = "windows.scheduled-task";

    public static DesiredProviderState CreateDesiredState(WindowsSystemProfile profile)
    {
        var resources = profile.Registry.Select(MapRegistry)
            .Concat(profile.Services.Select(MapService))
            .Concat(profile.Startup.Select(MapStartup))
            .Concat(profile.Tasks.Select(MapTask))
            .ToArray();
        return new DesiredProviderState(resources);
    }

    public static StateResource MapRegistry(RegistryValueProfile profile)
        => Resource(RegistryType, $"registry:{Stable(profile.Hive + "|" + profile.Path + "|" + profile.Name)}",
            profile.State == "absent" ? DesiredState.Absent : DesiredState.Present,
            profile.DependsOn,
            ("hive", profile.Hive), ("path", profile.Path), ("name", profile.Name),
            ("type", profile.Type), ("value", profile.Value ?? string.Empty));

    public static StateResource MapService(WindowsServiceProfile profile)
        => Resource(ServiceType, $"service:{Stable(profile.Name)}", DesiredState.Configured, profile.DependsOn,
            ("name", profile.Name), ("state", profile.State), ("startMode", profile.StartMode));

    public static StateResource MapStartup(StartupEntryProfile profile)
        => Resource(StartupType, $"startup:{Stable(profile.Scope + "|" + profile.Name)}",
            profile.State == "absent" ? DesiredState.Absent : DesiredState.Present,
            profile.DependsOn,
            ("name", profile.Name), ("scope", profile.Scope), ("command", profile.Command ?? string.Empty));

    public static StateResource MapTask(ScheduledTaskProfile profile)
        => Resource(TaskType, $"task:{Stable(profile.Name)}",
            profile.State == "absent" ? DesiredState.Absent : DesiredState.Present,
            profile.DependsOn,
            ("name", profile.Name), ("schedule", profile.Schedule), ("time", profile.Time ?? string.Empty),
            ("runLevel", profile.RunLevel), ("command", profile.Command ?? string.Empty),
            ("arguments", profile.Arguments ?? string.Empty));

    public static RegistryValueProfile Registry(StateResource resource)
        => new()
        {
            Hive = Required(resource, "hive"), Path = Required(resource, "path"), Name = Required(resource, "name"),
            State = resource.State == DesiredState.Absent ? "absent" : "present",
            Type = Optional(resource, "type", "string"), Value = Optional(resource, "value", string.Empty),
            DependsOn = resource.Tags
        };

    public static WindowsServiceProfile Service(StateResource resource)
        => new()
        {
            Name = Required(resource, "name"), State = Optional(resource, "state", "unchanged"),
            StartMode = Optional(resource, "startMode", "unchanged"), DependsOn = resource.Tags
        };

    public static StartupEntryProfile Startup(StateResource resource)
        => new()
        {
            Name = Required(resource, "name"), Scope = Optional(resource, "scope", "user"),
            State = resource.State == DesiredState.Absent ? "absent" : "present",
            Command = Optional(resource, "command", string.Empty), DependsOn = resource.Tags
        };

    public static ScheduledTaskProfile Task(StateResource resource)
        => new()
        {
            Name = Required(resource, "name"), State = resource.State == DesiredState.Absent ? "absent" : "present",
            Schedule = Optional(resource, "schedule", "logon"), Time = Optional(resource, "time", string.Empty),
            RunLevel = Optional(resource, "runLevel", "limited"), Command = Optional(resource, "command", string.Empty),
            Arguments = Optional(resource, "arguments", string.Empty), DependsOn = resource.Tags
        };

    private static StateResource Resource(
        string type,
        string identity,
        DesiredState state,
        IReadOnlyCollection<string> dependsOn,
        params (string Name, string Value)[] properties)
        => new()
        {
            ProviderId = ProviderId,
            ResourceType = type,
            Identity = identity,
            State = state,
            Properties = properties.ToDictionary(pair => pair.Name, pair => StateValue.From(pair.Value), StringComparer.OrdinalIgnoreCase),
            Tags = dependsOn
        };

    private static string Required(StateResource resource, string name)
        => resource.Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.Value)
            ? value.Value!
            : throw new InvalidDataException($"Ресурс {resource.Identity} не содержит '{name}'.");

    private static string Optional(StateResource resource, string name, string fallback)
        => resource.Properties.TryGetValue(name, out var value) && value.Value is not null ? value.Value : fallback;

    private static string Stable(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 10)).ToLowerInvariant();
    }
}

/// <summary>Allowlisted Registry, Services, Startup и Scheduled Tasks поверх общего Apply Engine.</summary>
public sealed class WindowsSystemProvider : IStateProvider, IRollbackProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IWindowsSystemClient _client;

    public WindowsSystemProvider(IWindowsSystemClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Id => WindowsSystemProfileMapper.ProviderId;
    public string DisplayName => "Windows System Control";
    public bool IsSupported => _client.IsSupported;
    public ProviderCapabilities Capabilities => ProviderCapabilities.Capture | ProviderCapabilities.Apply
        | ProviderCapabilities.Remove | ProviderCapabilities.Rollback | ProviderCapabilities.MayRequireAdministrator;

    public Task<ProviderDiscoveryResult> DiscoverAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsSupported
            ? new ProviderDiscoveryResult(Array.Empty<StateResource>(), Array.Empty<ProviderDiagnostic>())
            : new ProviderDiscoveryResult(Array.Empty<StateResource>(),
                [new ProviderDiagnostic("windows.system.unsupported", "Windows System Control доступен только в Windows.", true)]));
    }

    public async Task<IReadOnlyCollection<PlannedAction>> PlanAsync(
        DesiredProviderState desiredState,
        CurrentProviderState currentState,
        PlanningContext context,
        CancellationToken cancellationToken)
    {
        _ = currentState;
        _ = context;
        var actions = new List<PlannedAction>();
        foreach (var resource in desiredState.Resources.Where(resource => resource.ProviderId == Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = resource.ResourceType switch
            {
                WindowsSystemProfileMapper.RegistryType => await PlanRegistryAsync(resource, cancellationToken),
                WindowsSystemProfileMapper.ServiceType => await PlanServiceAsync(resource, cancellationToken),
                WindowsSystemProfileMapper.StartupType => await PlanStartupAsync(resource, cancellationToken),
                WindowsSystemProfileMapper.TaskType => await PlanTaskAsync(resource, cancellationToken),
                _ => Unsupported(resource)
            };
            if (action is not null)
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    public async Task<ActionExecutionResult> ApplyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        WindowsSystemOperationResult result = action.Resource.ResourceType switch
        {
            WindowsSystemProfileMapper.RegistryType => action.Resource.State == DesiredState.Absent
                ? await _client.DeleteRegistryAsync(WindowsSystemProfileMapper.Registry(action.Resource), cancellationToken)
                : await _client.SetRegistryAsync(WindowsSystemProfileMapper.Registry(action.Resource), cancellationToken),
            WindowsSystemProfileMapper.ServiceType => await _client.SetServiceAsync(WindowsSystemProfileMapper.Service(action.Resource), cancellationToken),
            WindowsSystemProfileMapper.StartupType => action.Resource.State == DesiredState.Absent
                ? await _client.DeleteStartupAsync(WindowsSystemProfileMapper.Startup(action.Resource), cancellationToken)
                : await _client.SetStartupAsync(WindowsSystemProfileMapper.Startup(action.Resource), cancellationToken),
            WindowsSystemProfileMapper.TaskType => action.Resource.State == DesiredState.Absent
                ? await _client.DeleteTaskAsync(WindowsSystemProfileMapper.Task(action.Resource), cancellationToken)
                : await _client.SetTaskAsync(WindowsSystemProfileMapper.Task(action.Resource), cancellationToken),
            _ => new WindowsSystemOperationResult(false, "Неподдерживаемый system-control resource.")
        };
        return new ActionExecutionResult(
            result.Succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
            result.Message,
            Array.Empty<ProviderDiagnostic>());
    }

    public async Task<VerificationResult> VerifyAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        return action.Resource.ResourceType switch
        {
            WindowsSystemProfileMapper.RegistryType => VerifyRegistry(
                WindowsSystemProfileMapper.Registry(action.Resource),
                await _client.GetRegistryAsync(WindowsSystemProfileMapper.Registry(action.Resource), cancellationToken)),
            WindowsSystemProfileMapper.ServiceType => VerifyService(
                WindowsSystemProfileMapper.Service(action.Resource),
                await _client.GetServiceAsync(WindowsSystemProfileMapper.Service(action.Resource), cancellationToken)),
            WindowsSystemProfileMapper.StartupType => VerifyStartup(
                WindowsSystemProfileMapper.Startup(action.Resource),
                await _client.GetStartupAsync(WindowsSystemProfileMapper.Startup(action.Resource), cancellationToken)),
            WindowsSystemProfileMapper.TaskType => VerifyTask(
                WindowsSystemProfileMapper.Task(action.Resource),
                await _client.GetTaskAsync(WindowsSystemProfileMapper.Task(action.Resource), cancellationToken)),
            _ => new VerificationResult(false, "Неподдерживаемый system-control resource.")
        };
    }

    public async Task<RollbackPreparationResult> PrepareRollbackAsync(
        PlannedAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.BackupDirectory);
        var payload = action.Resource.ResourceType switch
        {
            WindowsSystemProfileMapper.RegistryType => new SystemRollbackPayload
            {
                ResourceType = action.Resource.ResourceType,
                Registry = WindowsSystemProfileMapper.Registry(action.Resource),
                RegistrySnapshot = await _client.GetRegistryAsync(WindowsSystemProfileMapper.Registry(action.Resource), cancellationToken)
            },
            WindowsSystemProfileMapper.ServiceType => new SystemRollbackPayload
            {
                ResourceType = action.Resource.ResourceType,
                Service = WindowsSystemProfileMapper.Service(action.Resource),
                ServiceSnapshot = await _client.GetServiceAsync(WindowsSystemProfileMapper.Service(action.Resource), cancellationToken)
            },
            WindowsSystemProfileMapper.StartupType => new SystemRollbackPayload
            {
                ResourceType = action.Resource.ResourceType,
                Startup = WindowsSystemProfileMapper.Startup(action.Resource),
                StartupSnapshot = await _client.GetStartupAsync(WindowsSystemProfileMapper.Startup(action.Resource), cancellationToken)
            },
            WindowsSystemProfileMapper.TaskType => new SystemRollbackPayload
            {
                ResourceType = action.Resource.ResourceType,
                Task = WindowsSystemProfileMapper.Task(action.Resource),
                TaskSnapshot = await _client.GetTaskAsync(WindowsSystemProfileMapper.Task(action.Resource), cancellationToken)
            },
            _ => null
        };
        if (payload is null || payload.ServiceSnapshot is { Exists: false })
        {
            return new RollbackPreparationResult(false, null, "Rollback checkpoint не может быть создан.");
        }

        var path = Path.Combine(context.BackupDirectory, $"{action.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
        return new RollbackPreparationResult(true, path, "System-control checkpoint создан.");
    }

    public async Task<RollbackExecutionResult> RollbackAsync(
        RollbackAction action,
        ProviderExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        try
        {
            var json = await File.ReadAllTextAsync(action.BackupReference, cancellationToken);
            var payload = JsonSerializer.Deserialize<SystemRollbackPayload>(json, JsonOptions)
                ?? throw new InvalidDataException("System-control checkpoint повреждён.");
            var result = await RestoreAsync(payload, cancellationToken);
            return new RollbackExecutionResult(result.Succeeded, result.Message);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new RollbackExecutionResult(false, exception.Message);
        }
    }

    private async Task<PlannedAction?> PlanRegistryAsync(StateResource resource, CancellationToken cancellationToken)
    {
        var profile = WindowsSystemProfileMapper.Registry(resource);
        var current = await _client.GetRegistryAsync(profile, cancellationToken);
        var absent = profile.State == "absent";
        if ((absent && !current.Exists)
            || (!absent && current.Exists && current.Type == profile.Type && current.Value == profile.Value))
        {
            return null;
        }

        return Action(resource, absent ? ActionType.Remove : current.Exists ? ActionType.Modify : ActionType.Create,
            absent ? RiskLevel.High : profile.Hive == "HKLM" ? RiskLevel.Medium : RiskLevel.Low,
            profile.Hive == "HKLM", $"{(absent ? "Удалить" : "Настроить")} Registry value {profile.Hive}\\{profile.Path}\\{profile.Name}.");
    }

    private async Task<PlannedAction?> PlanServiceAsync(StateResource resource, CancellationToken cancellationToken)
    {
        var profile = WindowsSystemProfileMapper.Service(resource);
        var current = await _client.GetServiceAsync(profile, cancellationToken);
        if (!current.Exists)
        {
            return Unsupported(resource, $"Windows service '{profile.Name}' не найден.");
        }

        var stateMatches = profile.State == "unchanged" || profile.State == current.State;
        var modeMatches = profile.StartMode == "unchanged" || profile.StartMode == current.StartMode;
        if (stateMatches && modeMatches)
        {
            return null;
        }

        var risk = profile.State == "stopped" || profile.StartMode == "disabled" ? RiskLevel.High : RiskLevel.Medium;
        return Action(resource, ActionType.Modify, risk, true,
            $"Настроить service '{profile.Name}': state={profile.State}, startMode={profile.StartMode}.");
    }

    private async Task<PlannedAction?> PlanStartupAsync(StateResource resource, CancellationToken cancellationToken)
    {
        var profile = WindowsSystemProfileMapper.Startup(resource);
        var current = await _client.GetStartupAsync(profile, cancellationToken);
        var absent = profile.State == "absent";
        if ((absent && !current.Exists) || (!absent && current.Exists && current.Command == profile.Command))
        {
            return null;
        }

        return Action(resource, absent ? ActionType.Remove : current.Exists ? ActionType.Modify : ActionType.Create,
            absent ? RiskLevel.High : RiskLevel.Medium, profile.Scope == "machine",
            $"{(absent ? "Удалить" : "Настроить")} startup entry '{profile.Name}' ({profile.Scope}).");
    }

    private async Task<PlannedAction?> PlanTaskAsync(StateResource resource, CancellationToken cancellationToken)
    {
        var profile = WindowsSystemProfileMapper.Task(resource);
        var current = await _client.GetTaskAsync(profile, cancellationToken);
        var absent = profile.State == "absent";
        if ((absent && !current.Exists) || (!absent && current.Exists && TaskMatches(current.Xml, profile)))
        {
            return null;
        }

        return Action(resource, absent ? ActionType.Remove : current.Exists ? ActionType.Update : ActionType.Create,
            absent ? RiskLevel.High : profile.RunLevel == "highest" ? RiskLevel.High : RiskLevel.Medium,
            profile.RunLevel == "highest" || profile.Schedule == "startup",
            $"{(absent ? "Удалить" : "Настроить")} scheduled task '{profile.Name}'.");
    }

    private static PlannedAction Action(
        StateResource resource,
        ActionType operation,
        RiskLevel risk,
        bool requiresAdministrator,
        string explanation)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{resource.NormalizedIdentity}"));
        return new PlannedAction
        {
            Id = $"system-{operation.ToString().ToLowerInvariant()}-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}",
            ProviderId = WindowsSystemProfileMapper.ProviderId,
            Resource = resource,
            Operation = operation,
            Risk = risk,
            RequiresAdministrator = requiresAdministrator,
            MayRequireReboot = false,
            SupportsRollback = true,
            DependsOn = resource.Tags,
            Explanation = explanation
        };
    }

    private static PlannedAction Unsupported(StateResource resource, string? message = null)
        => new()
        {
            Id = $"system-unsupported-{resource.Identity.Replace(':', '-')}",
            ProviderId = WindowsSystemProfileMapper.ProviderId,
            Resource = resource,
            Operation = ActionType.Unsupported,
            Risk = RiskLevel.High,
            RequiresAdministrator = false,
            MayRequireReboot = false,
            SupportsRollback = false,
            Explanation = message ?? $"Resource type '{resource.ResourceType}' не поддерживается."
        };

    private static VerificationResult VerifyRegistry(RegistryValueProfile profile, RegistryValueSnapshot current)
    {
        var match = profile.State == "absent"
            ? !current.Exists
            : current.Exists && current.Type == profile.Type && current.Value == profile.Value;
        return new VerificationResult(match, match ? "Registry value подтверждён." : "Registry value не совпадает с профилем.");
    }

    private static VerificationResult VerifyService(WindowsServiceProfile profile, ServiceSnapshot current)
    {
        var match = current.Exists
            && (profile.State == "unchanged" || profile.State == current.State)
            && (profile.StartMode == "unchanged" || profile.StartMode == current.StartMode);
        return new VerificationResult(match, match ? "Service state подтверждён." : "Service state не совпадает с профилем.");
    }

    private static VerificationResult VerifyStartup(StartupEntryProfile profile, StartupEntrySnapshot current)
    {
        var match = profile.State == "absent" ? !current.Exists : current.Exists && current.Command == profile.Command;
        return new VerificationResult(match, match ? "Startup entry подтверждён." : "Startup entry не совпадает с профилем.");
    }

    private static VerificationResult VerifyTask(ScheduledTaskProfile profile, ScheduledTaskSnapshot current)
    {
        var match = profile.State == "absent" ? !current.Exists : current.Exists && TaskMatches(current.Xml, profile);
        return new VerificationResult(match, match ? "Scheduled task подтверждён." : "Scheduled task не совпадает с профилем.");
    }

    private static bool TaskMatches(string? xml, ScheduledTaskProfile profile)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var command = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Command")?.Value;
            var arguments = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Arguments")?.Value ?? string.Empty;
            var trigger = profile.Schedule switch
            {
                "startup" => "BootTrigger",
                "daily" => "CalendarTrigger",
                _ => "LogonTrigger"
            };
            return string.Equals(command, profile.Command, StringComparison.OrdinalIgnoreCase)
                && string.Equals(arguments, profile.Arguments ?? string.Empty, StringComparison.Ordinal)
                && document.Descendants().Any(element => element.Name.LocalName == trigger);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private async Task<WindowsSystemOperationResult> RestoreAsync(
        SystemRollbackPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Registry is not null && payload.RegistrySnapshot is not null)
        {
            return payload.RegistrySnapshot.Exists
                ? await _client.SetRegistryAsync(payload.Registry with
                    {
                        State = "present",
                        Type = payload.RegistrySnapshot.Type,
                        Value = payload.RegistrySnapshot.Value
                    }, cancellationToken)
                : await _client.DeleteRegistryAsync(payload.Registry, cancellationToken);
        }

        if (payload.Service is not null && payload.ServiceSnapshot is { Exists: true } service)
        {
            return await _client.SetServiceAsync(payload.Service with
            {
                State = service.State,
                StartMode = service.StartMode
            }, cancellationToken);
        }

        if (payload.Startup is not null && payload.StartupSnapshot is not null)
        {
            return payload.StartupSnapshot.Exists
                ? await _client.SetStartupAsync(payload.Startup with
                    {
                        State = "present",
                        Command = payload.StartupSnapshot.Command
                    }, cancellationToken)
                : await _client.DeleteStartupAsync(payload.Startup, cancellationToken);
        }

        if (payload.Task is not null && payload.TaskSnapshot is not null)
        {
            return payload.TaskSnapshot.Exists && payload.TaskSnapshot.Xml is not null
                ? await _client.RestoreTaskAsync(payload.Task.Name, payload.TaskSnapshot.Xml, cancellationToken)
                : await _client.DeleteTaskAsync(payload.Task, cancellationToken);
        }

        return new WindowsSystemOperationResult(false, "Checkpoint не содержит восстанавливаемый resource.");
    }

    private sealed record SystemRollbackPayload
    {
        public string ResourceType { get; init; } = string.Empty;
        public RegistryValueProfile? Registry { get; init; }
        public RegistryValueSnapshot? RegistrySnapshot { get; init; }
        public WindowsServiceProfile? Service { get; init; }
        public ServiceSnapshot? ServiceSnapshot { get; init; }
        public StartupEntryProfile? Startup { get; init; }
        public StartupEntrySnapshot? StartupSnapshot { get; init; }
        public ScheduledTaskProfile? Task { get; init; }
        public ScheduledTaskSnapshot? TaskSnapshot { get; init; }
    }
}