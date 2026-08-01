using System.Text.RegularExpressions;
using WinState.Domain.Profiles;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WinState.Core.Profiles;

public sealed record ProfileLoadOptions(
    IReadOnlyDictionary<string, string>? Variables = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public sealed record LoadedProfile(
    WinStateProfile Profile,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>Загружает, объединяет и нормализует декларативные YAML-профили WinState.</summary>
public sealed class ProfileEngine
{
    private static readonly Regex VariablePattern = new(
        @"\{\{\s*(?<braced>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}|\$\{(?<shell>[A-Za-z_][A-Za-z0-9_.-]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsRootedPath = new(
        @"^(?:[A-Za-z]:[\\/]|\\\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<LoadedProfile> LoadAsync(
        string path,
        ProfileLoadOptions? options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь к профилю не указан.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Профиль WinState не найден.", fullPath);
        }

        var sourceFiles = new List<string>();
        var activeStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var document = await LoadRecursiveAsync(fullPath, activeStack, sourceFiles, cancellationToken);
        var variables = BuildVariables(document.Variables, options, fullPath);
        var profile = Map(document, variables, fullPath);
        return new LoadedProfile(profile, sourceFiles.AsReadOnly(), variables);
    }

    public Task<LoadedProfile> LoadAsync(string path, CancellationToken cancellationToken)
        => LoadAsync(path, null, cancellationToken);

    private async Task<ProfileDocument> LoadRecursiveAsync(
        string path,
        ISet<string> activeStack,
        ICollection<string> sourceFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!activeStack.Add(fullPath))
        {
            throw new InvalidDataException($"Обнаружен цикл includes/extends: {fullPath}");
        }

        try
        {
            ProfileDocument local;
            try
            {
                var yaml = await File.ReadAllTextAsync(fullPath, cancellationToken);
                local = _deserializer.Deserialize<ProfileDocument>(yaml) ?? new ProfileDocument();
            }
            catch (YamlException exception)
            {
                throw new InvalidDataException($"Некорректный YAML в '{fullPath}': {exception.Message}", exception);
            }

            sourceFiles.Add(fullPath);
            var merged = new ProfileDocument();
            foreach (var reference in local.Extends.Concat(local.Includes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                var referencedPath = ResolveReference(fullPath, reference);
                if (!File.Exists(referencedPath))
                {
                    throw new FileNotFoundException(
                        $"Связанный профиль '{reference}' не найден для '{fullPath}'.",
                        referencedPath);
                }

                var inherited = await LoadRecursiveAsync(referencedPath, activeStack, sourceFiles, cancellationToken);
                merged = Merge(merged, inherited);
            }

            return Merge(merged, local);
        }
        finally
        {
            _ = activeStack.Remove(fullPath);
        }
    }

    private static string ResolveReference(string ownerPath, string reference)
    {
        var expanded = Environment.ExpandEnvironmentVariables(reference.Trim());
        return Path.GetFullPath(
            Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(Path.GetDirectoryName(ownerPath) ?? Environment.CurrentDirectory, expanded));
    }

    private static IReadOnlyDictionary<string, string> BuildVariables(
        IReadOnlyDictionary<string, string> defaults,
        ProfileLoadOptions? options,
        string profilePath)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in defaults)
        {
            variables[pair.Key] = pair.Value;
        }

        variables["profileFile"] = profilePath;
        variables["profileDirectory"] = Path.GetDirectoryName(profilePath) ?? Environment.CurrentDirectory;

        if (options?.Environment is not null)
        {
            foreach (var pair in options.Environment)
            {
                const string prefix = "WINSTATE_VAR_";
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
                {
                    variables[pair.Key[prefix.Length..]] = pair.Value;
                }
            }
        }

        if (options?.Variables is not null)
        {
            foreach (var pair in options.Variables)
            {
                variables[pair.Key] = pair.Value;
            }
        }

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var changed = false;
            foreach (var key in variables.Keys.ToArray())
            {
                var resolved = ReplaceVariables(variables[key], variables, throwOnMissing: false);
                changed |= !string.Equals(resolved, variables[key], StringComparison.Ordinal);
                variables[key] = resolved;
            }

            if (!changed)
            {
                break;
            }
        }

        return new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
    }

    private static WinStateProfile Map(
        ProfileDocument document,
        IReadOnlyDictionary<string, string> variables,
        string profilePath)
    {
        var root = Path.GetDirectoryName(profilePath) ?? Environment.CurrentDirectory;
        var user = NormalizeVariables(document.Environment.User, variables);
        var machine = NormalizeVariables(document.Environment.Machine, variables);
        var userPath = NormalizePaths(document.Environment.UserPath, variables, root);
        var machinePath = NormalizePaths(document.Environment.MachinePath, variables, root);

        return new WinStateProfile
        {
            SchemaVersion = document.SchemaVersion,
            Metadata = new ProfileMetadata
            {
                Name = Resolve(document.Metadata.Name, variables),
                Description = ResolveNullable(document.Metadata.Description, variables),
                Author = ResolveNullable(document.Metadata.Author, variables),
                ProfileVersion = document.Metadata.ProfileVersion ?? 1
            },
            Settings = new ProfileSettings
            {
                StrictMode = document.Settings.StrictMode ?? false,
                RemoveUnmanagedPackages = document.Settings.RemoveUnmanagedPackages ?? false,
                AllowReboot = document.Settings.AllowReboot ?? false
            },
            Environment = new EnvironmentProfileSection
            {
                User = user,
                Machine = machine,
                UserPath = userPath,
                MachinePath = machinePath
            },
            Includes = document.Includes.ToArray(),
            Extends = document.Extends.ToArray()
        };
    }

    private static IReadOnlyDictionary<string, string> NormalizeVariables(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> variables)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var key = pair.Key.Trim();
            result[key] = Resolve(pair.Value, variables);
        }

        return result;
    }

    private static IReadOnlyCollection<PathEntryProfile> NormalizePaths(
        IReadOnlyCollection<PathEntryDocument> source,
        IReadOnlyDictionary<string, string> variables,
        string root)
    {
        var result = new List<PathEntryProfile>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source)
        {
            var resolved = Resolve(entry.Path, variables);
            var normalized = NormalizePath(resolved, root);
            if (!identities.Add(normalized))
            {
                continue;
            }

            result.Add(new PathEntryProfile
            {
                Path = normalized,
                State = string.IsNullOrWhiteSpace(entry.State) ? "present" : entry.State.Trim().ToLowerInvariant(),
                Position = string.IsNullOrWhiteSpace(entry.Position) ? "append" : entry.Position.Trim().ToLowerInvariant()
            });
        }

        return result;
    }

    private static string NormalizePath(string value, string root)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (WindowsRootedPath.IsMatch(expanded))
        {
            return expanded.Replace('/', '\\').TrimEnd('\\');
        }

        var absolute = Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
    }

    private static string Resolve(string? value, IReadOnlyDictionary<string, string> variables)
        => ReplaceVariables(value ?? string.Empty, variables, throwOnMissing: true).Trim();

    private static string? ResolveNullable(string? value, IReadOnlyDictionary<string, string> variables)
        => value is null ? null : Resolve(value, variables);

    private static string ReplaceVariables(
        string value,
        IReadOnlyDictionary<string, string> variables,
        bool throwOnMissing)
    {
        return VariablePattern.Replace(value, match =>
        {
            var name = match.Groups["braced"].Success
                ? match.Groups["braced"].Value
                : match.Groups["shell"].Value;
            if (variables.TryGetValue(name, out var replacement))
            {
                return replacement;
            }

            if (throwOnMissing)
            {
                throw new InvalidDataException($"Не задана переменная профиля '{name}'.");
            }

            return match.Value;
        });
    }

    private static ProfileDocument Merge(ProfileDocument baseline, ProfileDocument overlay)
    {
        return new ProfileDocument
        {
            SchemaVersion = overlay.SchemaVersion != 0 ? overlay.SchemaVersion : baseline.SchemaVersion,
            Metadata = new MetadataDocument
            {
                Name = Choose(overlay.Metadata.Name, baseline.Metadata.Name),
                Description = Choose(overlay.Metadata.Description, baseline.Metadata.Description),
                Author = Choose(overlay.Metadata.Author, baseline.Metadata.Author),
                ProfileVersion = overlay.Metadata.ProfileVersion ?? baseline.Metadata.ProfileVersion
            },
            Settings = new SettingsDocument
            {
                StrictMode = overlay.Settings.StrictMode ?? baseline.Settings.StrictMode,
                RemoveUnmanagedPackages = overlay.Settings.RemoveUnmanagedPackages ?? baseline.Settings.RemoveUnmanagedPackages,
                AllowReboot = overlay.Settings.AllowReboot ?? baseline.Settings.AllowReboot
            },
            Variables = MergeDictionary(baseline.Variables, overlay.Variables),
            Environment = new EnvironmentDocument
            {
                User = MergeDictionary(baseline.Environment.User, overlay.Environment.User),
                Machine = MergeDictionary(baseline.Environment.Machine, overlay.Environment.Machine),
                UserPath = baseline.Environment.UserPath.Concat(overlay.Environment.UserPath).ToList(),
                MachinePath = baseline.Environment.MachinePath.Concat(overlay.Environment.MachinePath).ToList()
            },
            Includes = overlay.Includes.ToList(),
            Extends = overlay.Extends.ToList()
        };
    }

    private static Dictionary<string, string> MergeDictionary(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> overlay)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in baseline)
        {
            result[pair.Key] = pair.Value;
        }

        foreach (var pair in overlay)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static string? Choose(string? overlay, string? baseline)
        => string.IsNullOrWhiteSpace(overlay) ? baseline : overlay;

    private sealed class ProfileDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public MetadataDocument Metadata { get; set; } = new();
        public SettingsDocument Settings { get; set; } = new();
        public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public EnvironmentDocument Environment { get; set; } = new();
        public List<string> Includes { get; set; } = [];
        public List<string> Extends { get; set; } = [];
    }

    private sealed class MetadataDocument
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public int? ProfileVersion { get; set; }
    }

    private sealed class SettingsDocument
    {
        public bool? StrictMode { get; set; }
        public bool? RemoveUnmanagedPackages { get; set; }
        public bool? AllowReboot { get; set; }
    }

    private sealed class EnvironmentDocument
    {
        public Dictionary<string, string> User { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Machine { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PathEntryDocument> UserPath { get; set; } = [];
        public List<PathEntryDocument> MachinePath { get; set; } = [];
    }

    private sealed class PathEntryDocument
    {
        public string Path { get; set; } = string.Empty;
        public string State { get; set; } = "present";
        public string Position { get; set; } = "append";
    }
}
