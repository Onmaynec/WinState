using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Resources;

namespace WinState.Domain.Configuration
{
    public enum DesiredState { Present, Absent, Enabled, Disabled, Running, Stopped, Configured, Unmanaged }
    public enum RiskLevel { None, Low, Medium, High, Critical }
}

namespace WinState.Domain.Errors
{
    public sealed class WinStateDomainException : Exception
    {
        public WinStateDomainException(string message) : base(message) { }
    }
}

namespace WinState.Domain.Resources
{
    public sealed record StateValue(string? Value, bool IsSecret = false)
    {
        public static StateValue From(string? value) => new(value);
        public static StateValue SecretReference(string reference) => new($"<secret: {reference}>", true);
        public override string ToString() => IsSecret ? "<secret>" : Value ?? "<null>";
    }

    public static class ResourceIdentity
    {
        public static string Normalize(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
                throw new Errors.WinStateDomainException("Идентификатор ресурса не может быть пустым.");

            var normalized = identity.Trim().Replace('\\', '/');
            while (normalized.Contains("//", StringComparison.Ordinal))
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            return normalized;
        }
    }

    public sealed record StateResource
    {
        public required string ProviderId { get; init; }
        public required string ResourceType { get; init; }
        public required string Identity { get; init; }
        public required DesiredState State { get; init; }
        public IReadOnlyDictionary<string, StateValue> Properties { get; init; } = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();
        public string NormalizedIdentity => ResourceIdentity.Normalize(Identity);
    }
}

namespace WinState.Domain.Planning
{
    public enum ActionType { Create, Install, Update, Modify, Enable, Disable, Start, Stop, Remove, Uninstall, Reorder, Copy, Restore, NoOp, Manual, Unsupported }
    public enum ActionStatus { Pending, Running, Succeeded, Failed, Skipped, Cancelled, RolledBack, RollbackFailed, VerificationFailed, ManualActionRequired }

    public sealed record PlannedAction
    {
        public required string Id { get; init; }
        public required string ProviderId { get; init; }
        public required StateResource Resource { get; init; }
        public required ActionType Operation { get; init; }
        public required RiskLevel Risk { get; init; }
        public IReadOnlyDictionary<string, StateValue> CurrentProperties { get; init; } = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, StateValue> DesiredProperties { get; init; } = new Dictionary<string, StateValue>(StringComparer.OrdinalIgnoreCase);
        public bool RequiresAdministrator { get; init; }
        public bool MayRequireReboot { get; init; }
        public bool SupportsRollback { get; init; }
        public IReadOnlyCollection<string> DependsOn { get; init; } = Array.Empty<string>();
        public required string Explanation { get; init; }
        public ActionStatus Status { get; init; } = ActionStatus.Pending;
    }
}

namespace WinState.Domain.Providers
{
    [Flags]
    public enum ProviderCapabilities
    {
        None = 0,
        Capture = 1 << 0,
        Apply = 1 << 1,
        Rollback = 1 << 2,
        Remove = 1 << 3,
        Offline = 1 << 4,
        MayRequireAdministrator = 1 << 5,
        MayRequireReboot = 1 << 6
    }

    public interface IStateProvider
    {
        string Id { get; }
        string DisplayName { get; }
        ProviderCapabilities Capabilities { get; }
        Task<ProviderDiscoveryResult> DiscoverAsync(ProviderContext context, CancellationToken cancellationToken);
        Task<IReadOnlyCollection<PlannedAction>> PlanAsync(DesiredProviderState desiredState, CurrentProviderState currentState, PlanningContext context, CancellationToken cancellationToken);
        Task<ActionExecutionResult> ApplyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken);
        Task<VerificationResult> VerifyAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken);
    }

    public interface IRollbackProvider
    {
        Task<RollbackPreparationResult> PrepareRollbackAsync(PlannedAction action, ProviderExecutionContext context, CancellationToken cancellationToken);
        Task<RollbackExecutionResult> RollbackAsync(RollbackAction action, ProviderExecutionContext context, CancellationToken cancellationToken);
    }

    public sealed record ProviderContext(string ProfileId, bool IsElevated, string WorkingDirectory);
    public sealed record PlanningContext(bool StrictMode, bool AllowCritical, string ProfileId);
    public sealed record ProviderExecutionContext(string TransactionId, bool IsElevated, string BackupDirectory);
    public sealed record DesiredProviderState(IReadOnlyCollection<StateResource> Resources);
    public sealed record CurrentProviderState(IReadOnlyCollection<StateResource> Resources);
    public sealed record ProviderDiagnostic(string Code, string Message, bool IsWarning = false);
    public sealed record ProviderDiscoveryResult(IReadOnlyCollection<StateResource> Resources, IReadOnlyCollection<ProviderDiagnostic> Diagnostics);
    public sealed record ActionExecutionResult(ActionStatus Status, string Message, IReadOnlyCollection<ProviderDiagnostic> Diagnostics);
    public sealed record VerificationResult(bool IsMatch, string Message);
    public sealed record RollbackPreparationResult(bool IsSupported, string? BackupReference, string Message);
    public sealed record RollbackAction(string ActionId, string ProviderId, string BackupReference);
    public sealed record RollbackExecutionResult(bool Succeeded, string Message);
}

namespace WinState.Domain.Profiles
{
    public sealed record WinStateProfile
    {
        public int SchemaVersion { get; init; } = 1;
        public required ProfileMetadata Metadata { get; init; }
        public ProfileSettings Settings { get; init; } = new();
        public EnvironmentProfileSection Environment { get; init; } = new();
        public IReadOnlyCollection<WingetPackageProfile> Packages { get; init; } = Array.Empty<WingetPackageProfile>();
        public IReadOnlyCollection<WindowsFeatureProfile> Features { get; init; } = Array.Empty<WindowsFeatureProfile>();
        public IReadOnlyCollection<string> Includes { get; init; } = Array.Empty<string>();
        public IReadOnlyCollection<string> Extends { get; init; } = Array.Empty<string>();
    }

    public sealed record ProfileMetadata
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public string? Author { get; init; }
        public int ProfileVersion { get; init; } = 1;
    }

    public sealed record ProfileSettings
    {
        public bool StrictMode { get; init; }
        public bool RemoveUnmanagedPackages { get; init; }
        public bool AllowReboot { get; init; }
    }

    public sealed record EnvironmentProfileSection
    {
        public IReadOnlyDictionary<string, string> User { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string> Machine { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<PathEntryProfile> UserPath { get; init; } = Array.Empty<PathEntryProfile>();
        public IReadOnlyCollection<PathEntryProfile> MachinePath { get; init; } = Array.Empty<PathEntryProfile>();
    }

    public sealed record PathEntryProfile
    {
        public required string Path { get; init; }
        public string State { get; init; } = "present";
        public string Position { get; init; } = "append";
    }

    public sealed record WingetPackageProfile
    {
        public required string Id { get; init; }
        public string State { get; init; } = "present";
        public string Version { get; init; } = "latest";
        public string Source { get; init; } = "winget";
        public string Scope { get; init; } = "user";
        public bool AllowUpgrade { get; init; } = true;
        public bool MayRequireReboot { get; init; }
    }

    public sealed record WindowsFeatureProfile
    {
        public required string Name { get; init; }
        public string State { get; init; } = "enabled";
        public bool IncludeParents { get; init; } = true;
    }
}

namespace WinState.Domain.Transactions
{
    public enum TransactionStatus { Planned, Running, Succeeded, SucceededRebootPending, Partial, Failed, Cancelled, RolledBack, RollbackFailed, VerificationFailed }

    public sealed record TransactionRecord
    {
        public required string Id { get; init; }
        public required string ProfileId { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public required string WinStateVersion { get; init; }
        public required string UserName { get; init; }
        public required string Mode { get; init; }
        public TransactionStatus Status { get; init; } = TransactionStatus.Planned;
        public IReadOnlyCollection<PlannedAction> Plan { get; init; } = Array.Empty<PlannedAction>();
        public IReadOnlyCollection<TransactionActionResult> Results { get; init; } = Array.Empty<TransactionActionResult>();
        public bool RebootRequired { get; init; }
    }

    public sealed record TransactionActionResult
    {
        public required string ActionId { get; init; }
        public required ActionStatus Status { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? Message { get; init; }
        public string? BackupReference { get; init; }
    }
}
