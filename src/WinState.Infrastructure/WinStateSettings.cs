using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinState.Infrastructure.Configuration;

/// <summary>Вычисленные и нормализованные пути и настройки WinState.</summary>
public sealed record WinStateOptions
{
    public required string HomeDirectory { get; init; }
    public required string ProfilesDirectory { get; init; }
    public required string DatabasePath { get; init; }
    public required string LogsDirectory { get; init; }
    public required string ConfigPath { get; init; }
    public bool PortableMode { get; init; }
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
}

/// <summary>Загружает winstate.json и применяет безопасные переменные окружения.</summary>
public static class WinStateSettingsLoader
{
    private const string ConfigFileName = "winstate.json";

    public static WinStateOptions Load(
        string? homeOverride = null,
        string? currentDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var workingDirectory = Path.GetFullPath(currentDirectory ?? Directory.GetCurrentDirectory());
        var executableDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var values = environment ?? ReadEnvironment();
        var configPath = ResolveConfigPath(workingDirectory, executableDirectory);
        var document = ReadDocument(configPath);

        var portable = ParseBoolean(Get(values, "WINSTATE_PORTABLE"))
            ?? document?.Portable
            ?? File.Exists(Path.Combine(executableDirectory, "winstate.portable"));

        var configDirectory = Path.GetDirectoryName(configPath) ?? workingDirectory;
        var configuredHome = FirstNotEmpty(homeOverride, Get(values, "WINSTATE_HOME"), document?.Storage?.Home);
        var home = configuredHome is null
            ? GetDefaultHome(portable, executableDirectory, values)
            : ResolvePath(configuredHome, configDirectory);

        var profiles = ResolvePath(
            FirstNotEmpty(Get(values, "WINSTATE_PROFILES"), document?.Profiles?.Directory) ?? "profiles",
            home);
        var database = ResolvePath(
            FirstNotEmpty(Get(values, "WINSTATE_DATABASE"), document?.Storage?.Database) ?? Path.Combine("state", "winstate.db"),
            home);
        var logs = ResolvePath(
            FirstNotEmpty(Get(values, "WINSTATE_LOGS"), document?.Logging?.Directory) ?? "logs",
            home);
        var logLevel = ParseLogLevel(FirstNotEmpty(Get(values, "WINSTATE_LOG_LEVEL"), document?.Logging?.MinimumLevel));

        return new WinStateOptions
        {
            HomeDirectory = home,
            ProfilesDirectory = profiles,
            DatabasePath = database,
            LogsDirectory = logs,
            ConfigPath = configPath,
            PortableMode = portable,
            MinimumLogLevel = logLevel
        };
    }

    private static string ResolveConfigPath(string workingDirectory, string executableDirectory)
    {
        var workingCandidate = Path.Combine(workingDirectory, ConfigFileName);
        if (File.Exists(workingCandidate))
        {
            return workingCandidate;
        }

        var executableCandidate = Path.Combine(executableDirectory, ConfigFileName);
        return File.Exists(executableCandidate) ? executableCandidate : workingCandidate;
    }

    private static SettingsDocument? ReadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Файл конфигурации '{path}' содержит некорректный JSON.", exception);
        }
    }

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
        {
            "WINSTATE_HOME", "WINSTATE_PROFILES", "WINSTATE_DATABASE", "WINSTATE_LOGS",
            "WINSTATE_LOG_LEVEL", "WINSTATE_PORTABLE", "XDG_DATA_HOME"
        })
        {
            result[name] = Environment.GetEnvironmentVariable(name);
        }

        return result;
    }

    private static string GetDefaultHome(
        bool portable,
        string executableDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (portable)
        {
            return Path.Combine(executableDirectory, ".winstate");
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "WinState");
            }
        }

        var xdg = Get(environment, "XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(Path.GetFullPath(xdg), "winstate");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            userProfile = Directory.GetCurrentDirectory();
        }

        return Path.Combine(userProfile, ".local", "share", "winstate");
    }

    private static string ResolvePath(string value, string baseDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDirectory, expanded));
    }

    private static LogLevel ParseLogLevel(string? value)
        => Enum.TryParse<LogLevel>(value, true, out var result) ? result : LogLevel.Information;

    private static bool? ParseBoolean(string? value)
        => bool.TryParse(value, out var result) ? result : null;

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) ? value : null;

    private sealed record SettingsDocument
    {
        public bool? Portable { get; init; }
        public StorageSettings? Storage { get; init; }
        public ProfilesSettings? Profiles { get; init; }
        public LoggingSettings? Logging { get; init; }
    }

    private sealed record StorageSettings
    {
        public string? Home { get; init; }
        public string? Database { get; init; }
    }

    private sealed record ProfilesSettings
    {
        public string? Directory { get; init; }
    }

    private sealed record LoggingSettings
    {
        public string? Directory { get; init; }
        public string? MinimumLevel { get; init; }
    }
}
