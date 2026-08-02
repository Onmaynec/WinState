using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinState.Domain.Planning;
using WinState.Providers.EnvironmentVariables;
using WinState.Providers.Features;
using WinState.Providers.Packages;

namespace WinState.App;

public sealed record CaptureCounts(
    int UserVariables,
    int MachineVariables,
    int UserPathEntries,
    int MachinePathEntries,
    int Packages,
    int EnabledFeatures,
    int SkippedSensitiveValues);

public sealed record CaptureReport(
    string ProfilePath,
    string ManifestPath,
    string ProfileName,
    DateTimeOffset CapturedAt,
    string Sha256,
    CaptureCounts Counts,
    IReadOnlyList<string> Diagnostics);

public sealed record DriftAction(
    string ActionId,
    string ProviderId,
    string Operation,
    string Risk,
    string Resource,
    bool RequiresAdministrator,
    bool SupportsRollback,
    string Explanation);

public sealed record DriftReport(
    string ProfileName,
    string ProfilePath,
    DateTimeOffset CheckedAt,
    bool IsValid,
    bool IsSupported,
    bool HasDrift,
    int Changes,
    int DestructiveChanges,
    string MaximumRisk,
    IReadOnlyList<DriftAction> Actions,
    IReadOnlyList<string> Diagnostics,
    string? ReportPath);

/// <summary>Создаёт безопасный YAML-снимок текущей Windows-конфигурации.</summary>
public sealed class CaptureWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SensitiveMarkers =
    [
        "PASSWORD", "PASSWD", "SECRET", "TOKEN", "APIKEY", "API_KEY",
        "PRIVATE_KEY", "CREDENTIAL", "AUTH", "CONNECTIONSTRING", "CONNECTION_STRING"
    ];

    private readonly IEnvironmentStore _environment;
    private readonly IWingetClient _winget;
    private readonly IWindowsFeatureClient _features;

    public CaptureWorkflow(
        IEnvironmentStore environment,
        IWingetClient winget,
        IWindowsFeatureClient features)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _winget = winget ?? throw new ArgumentNullException(nameof(winget));
        _features = features ?? throw new ArgumentNullException(nameof(features));
    }

    public async Task<CaptureReport> ExportAsync(
        string outputPath,
        string? profileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Не указан путь для YAML-снимка.", nameof(outputPath));
        }

        if (!_environment.IsSupported || !_winget.IsSupported || !_features.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Capture поддерживается в Windows с доступными WinGet и DISM.");
        }

        var fullPath = Path.GetFullPath(outputPath);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fullPath)))
        {
            fullPath += ".yaml";
        }

        var name = string.IsNullOrWhiteSpace(profileName)
            ? $"Снимок {System.Environment.MachineName}"
            : profileName.Trim();
        var diagnostics = new List<string>();
        var skippedSensitive = 0;

        var userVariables = await ReadVariablesAsync(
            EnvironmentScope.User,
            diagnostics,
            cancellationToken);
        var machineVariables = await ReadVariablesAsync(
            EnvironmentScope.Machine,
            diagnostics,
            cancellationToken);
        userVariables = FilterVariables(userVariables, ref skippedSensitive);
        machineVariables = FilterVariables(machineVariables, ref skippedSensitive);

        var userPath = await ReadPathAsync(EnvironmentScope.User, diagnostics, cancellationToken);
        var machinePath = await ReadPathAsync(EnvironmentScope.Machine, diagnostics, cancellationToken);
        var packages = await _winget.ListInstalledAsync(cancellationToken);
        var features = await _features.ListAsync(cancellationToken);
        var enabledFeatures = features.Where(feature => feature.Enabled).ToArray();

        if (packages.Count > 0)
        {
            diagnostics.Add(
                "WinGet inventory не сообщает scope установки; captured packages используют scope: user и требуют проверки перед apply.");
        }

        if (skippedSensitive > 0)
        {
            diagnostics.Add(
                $"Пропущено потенциально секретных переменных: {skippedSensitive}. Значения не записаны в YAML.");
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var yaml = BuildYaml(
            name,
            capturedAt,
            userVariables,
            machineVariables,
            userPath,
            machinePath,
            packages,
            enabledFeatures);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? System.Environment.CurrentDirectory);
        var temporaryPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, yaml, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, fullPath, true);

        var sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fullPath, cancellationToken)))
            .ToLowerInvariant();
        var counts = new CaptureCounts(
            userVariables.Count,
            machineVariables.Count,
            userPath.Count,
            machinePath.Count,
            packages.Count,
            enabledFeatures.Length,
            skippedSensitive);
        var manifestPath = fullPath + ".snapshot.json";
        var manifest = new
        {
            schemaVersion = 1,
            product = "WinState",
            version = WinStateApplication.Version,
            profileName = name,
            profilePath = fullPath,
            capturedAt,
            computerName = System.Environment.MachineName,
            userName = System.Environment.UserName,
            sha256,
            counts,
            diagnostics
        };
        await WriteJsonAtomicallyAsync(manifestPath, manifest, cancellationToken);

        return new CaptureReport(
            fullPath,
            manifestPath,
            name,
            capturedAt,
            sha256,
            counts,
            diagnostics);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ReadVariablesAsync(
        EnvironmentScope scope,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _environment.ReadVariablesAsync(scope, cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            diagnostics.Add($"Не удалось прочитать {EnvironmentProfileMapper.ScopeName(scope)} environment: {exception.Message}");
            return new Dictionary<string, string?>();
        }
    }

    private async Task<IReadOnlyList<string>> ReadPathAsync(
        EnvironmentScope scope,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _environment.ReadPathAsync(scope, cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            diagnostics.Add($"Не удалось прочитать {EnvironmentProfileMapper.ScopeName(scope)} PATH: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyDictionary<string, string?> FilterVariables(
        IReadOnlyDictionary<string, string?> source,
        ref int skippedSensitive)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (pair.Key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = pair.Key.Replace('-', '_').ToUpperInvariant();
            if (SensitiveMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal)))
            {
                skippedSensitive++;
                continue;
            }

            result[pair.Key] = pair.Value ?? string.Empty;
        }

        return result;
    }

    private static string BuildYaml(
        string profileName,
        DateTimeOffset capturedAt,
        IReadOnlyDictionary<string, string?> userVariables,
        IReadOnlyDictionary<string, string?> machineVariables,
        IReadOnlyList<string> userPath,
        IReadOnlyList<string> machinePath,
        IReadOnlyList<WingetInstalledPackage> packages,
        IReadOnlyList<WindowsFeatureState> enabledFeatures)
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("schemaVersion: 1");
        yaml.AppendLine();
        yaml.AppendLine("metadata:");
        yaml.AppendLine($"  name: {Quote(profileName)}");
        yaml.AppendLine($"  description: {Quote($"Снимок состояния, созданный WinState {WinStateApplication.Version} {capturedAt:O}")}");
        yaml.AppendLine("  author: \"WinState Capture\"");
        yaml.AppendLine("  profileVersion: 1");
        yaml.AppendLine();
        yaml.AppendLine("settings:");
        yaml.AppendLine("  strictMode: true");
        yaml.AppendLine("  removeUnmanagedPackages: false");
        yaml.AppendLine("  allowReboot: false");
        yaml.AppendLine();
        yaml.AppendLine("environment:");
        AppendMap(yaml, "user", userVariables);
        AppendMap(yaml, "machine", machineVariables);
        AppendPath(yaml, "userPath", userPath);
        AppendPath(yaml, "machinePath", machinePath);
        yaml.AppendLine();
        yaml.AppendLine("packages:");
        if (packages.Count == 0)
        {
            yaml.AppendLine("  []");
        }
        else
        {
            foreach (var package in packages.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                yaml.AppendLine($"  - id: {Quote(package.Id)}");
                yaml.AppendLine("    state: present");
                yaml.AppendLine($"    version: {Quote(package.Version)}");
                yaml.AppendLine($"    source: {Quote(string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source)}");
                yaml.AppendLine("    scope: user");
                yaml.AppendLine("    allowUpgrade: false");
                yaml.AppendLine("    mayRequireReboot: false");
            }
        }

        yaml.AppendLine();
        yaml.AppendLine("features:");
        if (enabledFeatures.Count == 0)
        {
            yaml.AppendLine("  []");
        }
        else
        {
            foreach (var feature in enabledFeatures.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                yaml.AppendLine($"  - name: {Quote(feature.Name)}");
                yaml.AppendLine("    state: enabled");
                yaml.AppendLine("    includeParents: true");
            }
        }

        return yaml.ToString();
    }

    private static void AppendMap(
        StringBuilder yaml,
        string section,
        IReadOnlyDictionary<string, string?> values)
    {
        if (values.Count == 0)
        {
            yaml.AppendLine($"  {section}: {{}}");
            return;
        }

        yaml.AppendLine($"  {section}:");
        foreach (var pair in values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            yaml.AppendLine($"    {Quote(pair.Key)}: {Quote(pair.Value ?? string.Empty)}");
        }
    }

    private static void AppendPath(StringBuilder yaml, string section, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
        {
            yaml.AppendLine($"  {section}: []");
            return;
        }

        yaml.AppendLine($"  {section}:");
        foreach (var entry in entries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yaml.AppendLine($"    - path: {Quote(entry)}");
            yaml.AppendLine("      state: present");
            yaml.AppendLine("      position: append");
        }
    }

    private static string Quote(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)}\"";

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}

/// <summary>Преобразует Unified Apply Plan в машинно-читаемый отчёт об отклонениях.</summary>
public sealed class DriftWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly UnifiedApplyWorkflow _unified;

    public DriftWorkflow(UnifiedApplyWorkflow unified)
    {
        _unified = unified ?? throw new ArgumentNullException(nameof(unified));
    }

    public async Task<DriftReport> CheckAsync(
        string profilePath,
        IReadOnlyDictionary<string, string>? variables,
        IReadOnlyDictionary<string, string?> environment,
        string? reportPath,
        CancellationToken cancellationToken)
    {
        var plan = await _unified.PlanAsync(profilePath, variables, environment, cancellationToken);
        var actions = plan.Plan.OrderedActions.Select(action => new DriftAction(
            action.Id,
            action.ProviderId,
            action.Operation.ToString(),
            action.Risk.ToString(),
            DisplayResource(action),
            action.RequiresAdministrator,
            action.SupportsRollback,
            action.Explanation)).ToArray();
        var diagnostics = plan.Validation.Issues
            .Select(issue => $"{issue.Path}: {issue.Message} ({issue.Code})")
            .Concat(plan.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))
            .ToArray();
        var destructive = plan.Plan.OrderedActions.Count(action => action.Operation is
            ActionType.Remove or ActionType.Uninstall or ActionType.Disable or ActionType.Stop);
        string? fullReportPath = null;
        var report = new DriftReport(
            plan.Loaded.Profile.Metadata.Name,
            Path.GetFullPath(profilePath),
            DateTimeOffset.UtcNow,
            plan.Validation.IsValid,
            plan.IsSupported,
            plan.Validation.IsValid && plan.IsSupported && actions.Length > 0,
            actions.Length,
            destructive,
            plan.Plan.MaximumRisk.ToString(),
            actions,
            diagnostics,
            null);

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            fullReportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath) ?? System.Environment.CurrentDirectory);
            report = report with { ReportPath = fullReportPath };
            var temporaryPath = fullReportPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(report, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryPath, fullReportPath, true);
        }

        return report;
    }

    private static string DisplayResource(PlannedAction action)
    {
        foreach (var property in new[] { "id", "name", "path", "command" })
        {
            if (action.Resource.Properties.TryGetValue(property, out var value)
                && !string.IsNullOrWhiteSpace(value.Value))
            {
                return value.Value!;
            }
        }

        return action.Resource.Identity;
    }
}
