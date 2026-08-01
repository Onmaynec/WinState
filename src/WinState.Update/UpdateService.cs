using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinState.Update;

public enum UpdateChannel
{
    Stable,
    Prerelease
}

public enum AutomaticUpdateMode
{
    Off,
    Check,
    Prompt,
    Install
}

public sealed record UpdateSettings
{
    public string Repository { get; init; } = "Onmaynec/WinState";
    public UpdateChannel Channel { get; init; } = UpdateChannel.Prerelease;
    public AutomaticUpdateMode Mode { get; init; } = AutomaticUpdateMode.Prompt;
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public string RuntimeIdentifier { get; init; } = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "win-arm64"
        : "win-x64";

    public static UpdateSettings FromEnvironment()
    {
        var repository = Environment.GetEnvironmentVariable("WINSTATE_UPDATE_REPOSITORY");
        var channel = ParseChannel(Environment.GetEnvironmentVariable("WINSTATE_UPDATE_CHANNEL"));
        var mode = ParseMode(Environment.GetEnvironmentVariable("WINSTATE_AUTO_UPDATE"));
        var interval = ParsePositiveInt(
            Environment.GetEnvironmentVariable("WINSTATE_UPDATE_INTERVAL_HOURS"),
            6);
        var timeout = ParsePositiveInt(
            Environment.GetEnvironmentVariable("WINSTATE_UPDATE_TIMEOUT_SECONDS"),
            6);
        var runtime = Environment.GetEnvironmentVariable("WINSTATE_UPDATE_RUNTIME");

        return new UpdateSettings
        {
            Repository = string.IsNullOrWhiteSpace(repository)
                ? "Onmaynec/WinState"
                : repository.Trim(),
            Channel = channel,
            Mode = mode,
            CheckInterval = TimeSpan.FromHours(interval),
            NetworkTimeout = TimeSpan.FromSeconds(timeout),
            RuntimeIdentifier = string.IsNullOrWhiteSpace(runtime)
                ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "win-arm64"
                    : "win-x64"
                : runtime.Trim()
        };
    }

    private static UpdateChannel ParseChannel(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "stable" => UpdateChannel.Stable,
            _ => UpdateChannel.Prerelease
        };

    private static AutomaticUpdateMode ParseMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "disabled" => AutomaticUpdateMode.Off,
            "check" or "notify" => AutomaticUpdateMode.Check,
            "install" or "auto" => AutomaticUpdateMode.Install,
            _ => AutomaticUpdateMode.Prompt
        };

    private static int ParsePositiveInt(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? parsed
                : fallback;
}

public sealed record ReleaseAsset(
    string Name,
    long Size,
    Uri DownloadUrl);

public sealed record ReleaseInfo(
    string Tag,
    SemanticVersion Version,
    string Name,
    bool IsPrerelease,
    DateTimeOffset PublishedAt,
    Uri PageUrl,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record UpdateCheckResult(
    string CurrentVersion,
    ReleaseInfo? Release,
    bool IsUpdateAvailable,
    string Message);

public sealed record UpdateDownloadResult(
    ReleaseInfo Release,
    string ArchivePath,
    string PayloadDirectory,
    string Sha256,
    long BytesDownloaded);

public sealed record UpdateInstallResult(
    bool Scheduled,
    bool RequiresExit,
    string Message,
    string? ScriptPath);

public sealed record UpdateCheckLedger
{
    public DateTimeOffset LastCheckedAtUtc { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string? LatestVersion { get; init; }
    public bool UpdateAvailable { get; init; }
}

public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(
        int major,
        int minor,
        int patch,
        IReadOnlyList<string> preRelease,
        string original)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        Original = original;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> PreRelease { get; }
    public string Original { get; }
    public bool IsPrerelease => PreRelease.Count > 0;

    public static SemanticVersion Parse(string value)
        => TryParse(value, out var result)
            ? result!
            : throw new FormatException($"Некорректная semantic version: {value}.");

    public static bool TryParse(string? value, out SemanticVersion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var original = value.Trim();
        var normalized = original.TrimStart('v', 'V');
        var withoutBuild = normalized.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var core = parts[0].Split('.');
        if (core.Length < 3
            || !int.TryParse(core[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(core[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(core[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            return false;
        }

        var preRelease = parts.Length == 2
            ? parts[1]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        result = new SemanticVersion(major, minor, patch, preRelease, normalized);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (PreRelease.Count == 0 && other.PreRelease.Count == 0)
        {
            return 0;
        }

        if (PreRelease.Count == 0)
        {
            return 1;
        }

        if (other.PreRelease.Count == 0)
        {
            return -1;
        }

        var length = Math.Max(PreRelease.Count, other.PreRelease.Count);
        for (var index = 0; index < length; index++)
        {
            if (index >= PreRelease.Count)
            {
                return -1;
            }

            if (index >= other.PreRelease.Count)
            {
                return 1;
            }

            var left = PreRelease[index];
            var right = other.PreRelease[index];
            var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric)
            {
                comparison = -1;
            }
            else if (rightNumeric)
            {
                comparison = 1;
            }
            else
            {
                comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public override string ToString() => Original;
}

public sealed class UpdateService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly UpdateSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public UpdateService(UpdateSettings? settings = null, HttpClient? httpClient = null)
    {
        _settings = settings ?? UpdateSettings.FromEnvironment();
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = _settings.NetworkTimeout;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WinState-Updater/0.6");
        }

        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public UpdateSettings Settings => _settings;

    public bool CanSelfInstall
        => OperatingSystem.IsWindows()
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "winstate.release.json"))
            && File.Exists(GetExecutablePath());

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var current = SemanticVersion.Parse(currentVersion);
        var releases = await GetReleasesAsync(cancellationToken);
        var latest = releases
            .Where(release => _settings.Channel == UpdateChannel.Prerelease || !release.IsPrerelease)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();

        if (latest is null)
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                "Подходящие GitHub Releases не найдены.");
        }

        var available = latest.Version.CompareTo(current) > 0;
        return new UpdateCheckResult(
            currentVersion,
            latest,
            available,
            available
                ? $"Доступна WinState {latest.Version}."
                : $"WinState {currentVersion} уже актуален.");
    }

    public async Task<UpdateDownloadResult> DownloadAndStageAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Автоматическая установка release package поддерживается только в Windows.");
        }

        var version = release.Version.ToString();
        var expectedName = $"WinState-{version}-{_settings.RuntimeIdentifier}.zip";
        var archiveAsset = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith($"-{_settings.RuntimeIdentifier}.zip", StringComparison.OrdinalIgnoreCase));
        if (archiveAsset is null)
        {
            throw new InvalidDataException(
                $"В release {release.Tag} отсутствует пакет {_settings.RuntimeIdentifier}.");
        }

        var checksumAsset = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals($"{archiveAsset.Name}.sha256", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
        if (checksumAsset is null)
        {
            throw new InvalidDataException(
                $"В release {release.Tag} отсутствует SHA-256 файл.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "WinState",
            "updates",
            release.Version.ToString());
        var payloadDirectory = Path.Combine(root, "payload");
        Directory.CreateDirectory(root);
        if (Directory.Exists(payloadDirectory))
        {
            Directory.Delete(payloadDirectory, true);
        }

        Directory.CreateDirectory(payloadDirectory);
        var archivePath = Path.Combine(root, archiveAsset.Name);
        var checksumPath = Path.Combine(root, checksumAsset.Name);
        var bytes = await DownloadFileAsync(
            archiveAsset,
            archivePath,
            progress,
            cancellationToken);
        _ = await DownloadFileAsync(checksumAsset, checksumPath, null, cancellationToken);

        var expectedHash = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken));
        var actualHash = await ComputeSha256Async(archivePath, cancellationToken);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archivePath);
            throw new InvalidDataException(
                $"SHA-256 mismatch: expected {expectedHash}, actual {actualHash}.");
        }

        await ExtractSafelyAsync(archivePath, payloadDirectory, cancellationToken);
        var marker = Path.Combine(payloadDirectory, "winstate.release.json");
        if (!File.Exists(marker))
        {
            throw new InvalidDataException(
                "Release package не содержит winstate.release.json и не может быть установлен автоматически.");
        }

        return new UpdateDownloadResult(
            release,
            archivePath,
            payloadDirectory,
            actualHash,
            bytes);
    }

    public Task<UpdateInstallResult> ScheduleInstallAsync(
        UpdateDownloadResult download,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanSelfInstall)
        {
            return Task.FromResult(new UpdateInstallResult(
                false,
                false,
                "Self-update доступен только для распакованной release-сборки. Для исходников используйте git pull.",
                null));
        }

        var destination = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var executable = GetExecutablePath();
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "WinState", "updater");
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, $"apply-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, UpdaterScript);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(download.PayloadDirectory);
        startInfo.ArgumentList.Add("-Destination");
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("-Executable");
        startInfo.ArgumentList.Add(executable);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(download.Release.Version.ToString());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить updater process.");
        process.Dispose();
        return Task.FromResult(new UpdateInstallResult(
            true,
            true,
            $"WinState {download.Release.Version} будет установлена после завершения текущего процесса.",
            scriptPath));
    }

    public async Task<bool> ShouldCheckAsync(
        string stateDirectory,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        if (_settings.Mode == AutomaticUpdateMode.Off)
        {
            return false;
        }

        var path = GetLedgerPath(stateDirectory);
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var ledger = JsonSerializer.Deserialize<UpdateCheckLedger>(json, JsonOptions);
            return ledger is null
                || !ledger.CurrentVersion.Equals(currentVersion, StringComparison.OrdinalIgnoreCase)
                || DateTimeOffset.UtcNow - ledger.LastCheckedAtUtc >= _settings.CheckInterval;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _ = exception;
            return true;
        }
    }

    public async Task SaveLedgerAsync(
        string stateDirectory,
        UpdateCheckResult result,
        CancellationToken cancellationToken)
    {
        var path = GetLedgerPath(stateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ledger = new UpdateCheckLedger
        {
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
            CurrentVersion = result.CurrentVersion,
            LatestVersion = result.Release?.Version.ToString(),
            UpdateAvailable = result.IsUpdateAvailable
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(ledger, JsonOptions),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.github.com/repos/{_settings.Repository}/releases?per_page=30");
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var releases = new List<ReleaseInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = element.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!SemanticVersion.TryParse(tag, out var version) || version is null)
            {
                continue;
            }

            var assets = new List<ReleaseAsset>();
            if (element.TryGetProperty("assets", out var assetsElement))
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var downloadUrl))
                    {
                        assets.Add(new ReleaseAsset(
                            name,
                            asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                            downloadUrl));
                    }
                }
            }

            var page = element.GetProperty("html_url").GetString();
            if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUrl))
            {
                continue;
            }

            var published = element.TryGetProperty("published_at", out var publishedElement)
                && DateTimeOffset.TryParse(
                    publishedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var publishedAt)
                    ? publishedAt
                    : DateTimeOffset.MinValue;
            releases.Add(new ReleaseInfo(
                tag,
                version,
                element.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? tag
                    : tag,
                element.TryGetProperty("prerelease", out var prerelease)
                    && prerelease.GetBoolean(),
                published,
                pageUrl,
                assets));
        }

        return releases;
    }

    private async Task<long> DownloadFileAsync(
        ReleaseAsset asset,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            true);
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)written / total, 0, 1));
            }
        }

        progress?.Report(1);
        return written;
    }

    private static async Task ExtractSafelyAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"ZIP entry выходит за пределы staging directory: {entry.FullName}.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ParseChecksum(string content)
    {
        var token = content
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("SHA-256 файл имеет некорректный формат.");
        }

        return token.ToLowerInvariant();
    }

    private static string GetLedgerPath(string stateDirectory)
        => Path.Combine(stateDirectory, "updates", "check-state.json");

    private static string GetExecutablePath()
        => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "winstate.exe" : "winstate");

    private const string UpdaterScript = """
        param(
            [Parameter(Mandatory = $true)][int]$ProcessId,
            [Parameter(Mandatory = $true)][string]$Source,
            [Parameter(Mandatory = $true)][string]$Destination,
            [Parameter(Mandatory = $true)][string]$Executable,
            [Parameter(Mandatory = $true)][string]$Version
        )

        $ErrorActionPreference = 'Stop'
        try {
            Wait-Process -Id $ProcessId -Timeout 90 -ErrorAction SilentlyContinue
            $backup = Join-Path $env:TEMP ("WinState\\update-backup-" + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
            New-Item -Path $backup -ItemType Directory -Force | Out-Null

            Get-ChildItem -LiteralPath $Destination -Force |
                Where-Object { $_.Name -notin @('.winstate', 'profiles', 'logs') } |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backup -Recurse -Force }

            Get-ChildItem -LiteralPath $Source -Force |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force }

            Set-Content -LiteralPath (Join-Path $Destination 'update-success.txt') `
                -Value ("Installed WinState " + $Version + " at " + [DateTimeOffset]::UtcNow.ToString('O')) `
                -Encoding utf8
            Start-Process -FilePath $Executable -WorkingDirectory $Destination
        }
        catch {
            $errorPath = Join-Path $env:TEMP 'WinState\\update-error.log'
            New-Item -Path (Split-Path $errorPath) -ItemType Directory -Force | Out-Null
            $_ | Out-String | Set-Content -LiteralPath $errorPath -Encoding utf8
            exit 1
        }
        """;
}
