using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinState.Infrastructure.Configuration;
using WinState.Storage;

namespace WinState.App.Diagnostics;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Failed
}

public sealed record DiagnosticCheck(string Name, DiagnosticStatus Status, string Message);

public sealed record DoctorReport(IReadOnlyCollection<DiagnosticCheck> Checks)
{
    public bool IsHealthy => Checks.All(check => check.Status != DiagnosticStatus.Failed);
}

/// <summary>Проверяет платформу, директории, конфигурацию и SQLite-хранилище.</summary>
public sealed class DoctorService
{
    private readonly WinStateOptions _options;
    private readonly SqliteStateStore _storage;
    private readonly ILogger<DoctorService> _logger;

    public DoctorService(
        WinStateOptions options,
        SqliteStateStore storage,
        ILogger<DoctorService> logger)
    {
        _options = options;
        _storage = storage;
        _logger = logger;
    }

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>
        {
            new(
                "Платформа",
                OperatingSystem.IsWindows() ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                OperatingSystem.IsWindows()
                    ? $"Windows {Environment.OSVersion.Version}"
                    : $"{RuntimeInformation.OSDescription}; системные провайдеры Windows будут недоступны."),
            new(
                ".NET",
                Environment.Version.Major >= 8 ? DiagnosticStatus.Ok : DiagnosticStatus.Failed,
                $"Runtime {Environment.Version}"),
            new(
                "Режим",
                DiagnosticStatus.Ok,
                _options.PortableMode ? "Portable" : "User data")
        };

        checks.Add(await CheckWritableDirectoryAsync(cancellationToken));
        checks.Add(await CheckStorageAsync(cancellationToken));
        return new DoctorReport(checks);
    }

    private async Task<DiagnosticCheck> CheckWritableDirectoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_options.HomeDirectory);
            var probe = Path.Combine(_options.HomeDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "winstate", cancellationToken);
            File.Delete(probe);
            return new DiagnosticCheck("Каталог данных", DiagnosticStatus.Ok, _options.HomeDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Каталог WinState недоступен для записи");
            return new DiagnosticCheck("Каталог данных", DiagnosticStatus.Failed, exception.Message);
        }
    }

    private async Task<DiagnosticCheck> CheckStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _storage.InitializeAsync(cancellationToken);
            var status = await _storage.GetStatusAsync(cancellationToken);
            return new DiagnosticCheck(
                "SQLite",
                DiagnosticStatus.Ok,
                $"{status.AppliedMigrations} migration(s), {status.DatabasePath}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "SQLite-хранилище WinState не прошло проверку");
            return new DiagnosticCheck("SQLite", DiagnosticStatus.Failed, exception.Message);
        }
    }
}
