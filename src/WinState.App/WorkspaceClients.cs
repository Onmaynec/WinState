using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WinState.App;

public sealed class ProcessGitConfigurationClient : IGitConfigurationClient
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsSupported { get; } = ProcessSupport.Detect("git", "--version");

    public async Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var result = await RunAsync(new[] { "config", "--global", "--get", key }, cancellationToken);
        if (result.ExitCode == 1)
        {
            return null;
        }

        EnsureSuccess(result, "Не удалось прочитать глобальную настройку Git.");
        return result.Output.TrimEnd('\r', '\n');
    }

    public async Task WriteGlobalAsync(string key, string value, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var result = await RunAsync(
            new[] { "config", "--global", "--replace-all", key, value },
            cancellationToken);
        EnsureSuccess(result, "Не удалось записать глобальную настройку Git.");
    }

    public async Task RemoveGlobalAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var result = await RunAsync(
            new[] { "config", "--global", "--unset-all", key },
            cancellationToken);
        if (result.ExitCode is not (0 or 1))
        {
            EnsureSuccess(result, "Не удалось удалить глобальную настройку Git.");
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !KeyPattern.IsMatch(key))
        {
            throw new InvalidDataException($"Некорректный ключ Git config: '{key}'.");
        }
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
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

        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить git.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException($"{message} {ProcessSupport.Compact(details)}");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

public sealed class ProcessPowerShellModuleClient : IPowerShellModuleClient
{
    private readonly string? _executable = ResolveExecutable();

    public bool IsSupported => _executable is not null;

    public async Task<string?> ReadInstalledVersionAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        EnsureSupported();
        var command =
            "$m = Get-Module -ListAvailable -Name '" + Escape(name) + "' | "
            + "Sort-Object Version -Descending | Select-Object -First 1; "
            + "if ($null -ne $m) { [Console]::Out.Write($m.Version.ToString()) }";
        var result = await RunAsync(command, cancellationToken);
        EnsureSuccess(result, "Не удалось прочитать PowerShell module inventory.");
        var version = result.Output.Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public async Task InstallAsync(
        string name,
        string? minimumVersion,
        string repository,
        CancellationToken cancellationToken)
    {
        ValidateName(name);
        EnsureSupported();
        if (!string.IsNullOrWhiteSpace(minimumVersion) && !Version.TryParse(minimumVersion, out _))
        {
            throw new InvalidDataException($"Некорректная версия PowerShell module: '{minimumVersion}'.");
        }

        var command = new StringBuilder();
        command.Append("$ErrorActionPreference='Stop'; Install-Module -Name '")
            .Append(Escape(name))
            .Append("' -Scope CurrentUser -Force -AllowClobber");
        if (!string.IsNullOrWhiteSpace(minimumVersion))
        {
            command.Append(" -MinimumVersion '").Append(Escape(minimumVersion)).Append("'");
        }

        if (!string.IsNullOrWhiteSpace(repository))
        {
            command.Append(" -Repository '").Append(Escape(repository)).Append("'");
        }

        var result = await RunAsync(command.ToString(), cancellationToken);
        EnsureSuccess(result, $"Не удалось установить PowerShell module '{name}'.");
    }

    private async Task<ProcessResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executable!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить PowerShell.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string? ResolveExecutable()
    {
        foreach (var candidate in new[] { "pwsh", "powershell.exe" })
        {
            if (ProcessSupport.Detect(
                candidate,
                "-NoLogo -NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\""))
            {
                return candidate;
            }
        }

        return null;
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("PowerShell 7/Windows PowerShell не найден.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException($"Некорректное имя PowerShell module: '{name}'.");
        }
    }

    private static string Escape(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException($"{message} {ProcessSupport.Compact(details)}");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

internal static class ProcessSupport
{
    public static bool Detect(string executable, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(4000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string Compact(string value)
        => string.Join(
            ' ',
            value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
