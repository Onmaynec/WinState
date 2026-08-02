using System.Diagnostics;
using System.Text;

namespace WinState.App;

/// <summary>Подготавливает безопасное восстановление установки из updater backup.</summary>
public sealed class UpdateBackupRestoreWorkflow
{
    private static readonly string[] PreservedDirectories = { ".winstate", "profiles", "logs" };
    private readonly string _homeDirectory;

    public UpdateBackupRestoreWorkflow(string homeDirectory)
    {
        _homeDirectory = Path.GetFullPath(homeDirectory ?? throw new ArgumentNullException(nameof(homeDirectory)));
    }

    public async Task<UpdateRestorePreparationReport> PrepareAsync(
        string backupDirectory,
        string? installDirectory,
        bool launch,
        CancellationToken cancellationToken)
    {
        var backup = Path.GetFullPath(backupDirectory);
        var install = Path.GetFullPath(string.IsNullOrWhiteSpace(installDirectory)
            ? AppContext.BaseDirectory
            : installDirectory);
        if (!Directory.Exists(backup))
        {
            throw new DirectoryNotFoundException($"Updater backup не найден: {backup}");
        }

        if (!File.Exists(Path.Combine(backup, "winstate.exe"))
            || !File.Exists(Path.Combine(backup, "winstate.release.json")))
        {
            throw new InvalidDataException(
                "Updater backup должен содержать winstate.exe и winstate.release.json.");
        }

        var operationId = $"restore-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var operationDirectory = Path.Combine(_homeDirectory, "updates", operationId);
        var safetyBackup = Path.Combine(operationDirectory, "current-installation");
        Directory.CreateDirectory(operationDirectory);
        await CopyDirectoryAsync(install, safetyBackup, cancellationToken);

        var scriptPath = Path.Combine(operationDirectory, "restore-update.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            BuildRestoreScript(),
            new UTF8Encoding(false),
            cancellationToken);

        var scheduled = false;
        if (launch)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Автоматический запуск restore script поддерживается только в Windows.");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" "
                    + $"-Backup \"{backup}\" -Target \"{install}\" -ParentPid {Environment.ProcessId}"
            });
            scheduled = process is not null;
            if (!scheduled)
            {
                throw new InvalidOperationException("Не удалось запустить restore script.");
            }
        }

        return new UpdateRestorePreparationReport(
            backup,
            install,
            safetyBackup,
            scriptPath,
            scheduled,
            scheduled
                ? "Восстановление запланировано после завершения текущего процесса."
                : "Restore script подготовлен без запуска.");
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (IsPreserved(relative))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static bool IsPreserved(string relative)
    {
        var first = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null
            && PreservedDirectories.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildRestoreScript()
        => """
        param(
          [Parameter(Mandatory=$true)][string]$Backup,
          [Parameter(Mandatory=$true)][string]$Target,
          [Parameter(Mandatory=$true)][int]$ParentPid
        )
        $ErrorActionPreference = 'Stop'
        try {
          Wait-Process -Id $ParentPid -ErrorAction SilentlyContinue
          $preserve = @('.winstate', 'profiles', 'logs')
          Get-ChildItem -LiteralPath $Backup -Force | ForEach-Object {
            if ($preserve -contains $_.Name) { return }
            $destination = Join-Path $Target $_.Name
            if ($_.PSIsContainer) {
              Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
            } else {
              Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
            }
          }
          Set-Content -LiteralPath (Join-Path $Target 'restore-success.txt') -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding UTF8
          Start-Process -FilePath (Join-Path $Target 'winstate.exe')
        } catch {
          Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSCommandPath) 'restore-error.log') -Value ($_ | Out-String) -Encoding UTF8
          exit 1
        }
        """;
}
