namespace WinState.App;

public sealed record WorkspaceManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public List<WorkspaceGitEntry> Git { get; init; } = new();
    public List<WorkspacePowerShellModule> PowerShellModules { get; init; } = new();
    public List<WorkspaceDirectoryEntry> Directories { get; init; } = new();
    public List<WorkspaceFileEntry> Files { get; init; } = new();
}

public sealed record WorkspaceGitEntry
{
    public string Key { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string State { get; init; } = "present";
}

public sealed record WorkspacePowerShellModule
{
    public string Name { get; init; } = string.Empty;
    public string? MinimumVersion { get; init; }
    public string Repository { get; init; } = "PSGallery";
    public string State { get; init; } = "present";
}

public sealed record WorkspaceDirectoryEntry
{
    public string Path { get; init; } = string.Empty;
    public string State { get; init; } = "present";
}

public sealed record WorkspaceFileEntry
{
    public string Path { get; init; } = string.Empty;
    public string State { get; init; } = "present";
    public string? Content { get; init; }
    public string? Source { get; init; }
    public string Encoding { get; init; } = "utf-8";
}

public sealed record WorkspaceValidationReport(
    string ManifestPath,
    string Name,
    bool IsValid,
    IReadOnlyList<string> Issues);

public sealed record WorkspaceAction(
    string Id,
    string Provider,
    string Operation,
    string Resource,
    string Risk,
    bool Destructive,
    bool SupportsRollback,
    bool Blocked,
    string? Current,
    string? Desired,
    string Explanation);

public sealed record WorkspacePlanReport(
    string ManifestPath,
    string Name,
    DateTimeOffset PlannedAt,
    bool IsValid,
    bool IsSupported,
    int Changes,
    int DestructiveChanges,
    int IrreversibleChanges,
    IReadOnlyList<WorkspaceAction> Actions,
    IReadOnlyList<string> Diagnostics,
    string? JsonReportPath,
    string? MarkdownReportPath);

public sealed record WorkspaceExecutionReport(
    string TransactionId,
    string ManifestPath,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    bool RolledBack,
    int AppliedActions,
    int FailedActions,
    string TransactionPath,
    string? JsonReportPath,
    string? MarkdownReportPath,
    IReadOnlyList<string> Messages);

public sealed record WorkspaceRollbackReport(
    string TransactionId,
    bool Succeeded,
    int RestoredActions,
    int SkippedActions,
    IReadOnlyList<string> Messages);

public sealed record WorkspaceStatusReport(
    string OwnershipPath,
    int OwnedGitSettings,
    int OwnedModules,
    int OwnedFiles,
    int OwnedDirectories,
    string? LatestTransactionPath);

public interface IGitConfigurationClient
{
    bool IsSupported { get; }
    Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken);
    Task WriteGlobalAsync(string key, string value, CancellationToken cancellationToken);
    Task RemoveGlobalAsync(string key, CancellationToken cancellationToken);
}

public interface IPowerShellModuleClient
{
    bool IsSupported { get; }
    Task<string?> ReadInstalledVersionAsync(string name, CancellationToken cancellationToken);
    Task InstallAsync(
        string name,
        string? minimumVersion,
        string repository,
        CancellationToken cancellationToken);
}

public sealed record UpdateRestorePreparationReport(
    string BackupDirectory,
    string InstallDirectory,
    string SafetyBackupDirectory,
    string ScriptPath,
    bool Scheduled,
    string Message);
