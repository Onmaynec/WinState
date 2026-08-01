using System.Runtime.Versioning;

namespace WinState.Providers.SystemControl;

public static class WindowsSystemClientFactory
{
    public static IWindowsSystemClient Create()
        => OperatingSystem.IsWindows()
            ? CreateWindowsClient()
            : new UnsupportedWindowsSystemClient();

    [SupportedOSPlatform("windows")]
    private static IWindowsSystemClient CreateWindowsClient() => new WindowsSystemClient();

    private sealed class UnsupportedWindowsSystemClient : IWindowsSystemClient
    {
        public bool IsSupported => false;

        public Task<RegistryValueSnapshot> GetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
            => Unsupported<RegistryValueSnapshot>();

        public Task<WindowsSystemOperationResult> SetRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<WindowsSystemOperationResult> DeleteRegistryAsync(RegistryValueProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<ServiceSnapshot> GetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken)
            => Unsupported<ServiceSnapshot>();

        public Task<WindowsSystemOperationResult> SetServiceAsync(WindowsServiceProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<StartupEntrySnapshot> GetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
            => Unsupported<StartupEntrySnapshot>();

        public Task<WindowsSystemOperationResult> SetStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<WindowsSystemOperationResult> DeleteStartupAsync(StartupEntryProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<ScheduledTaskSnapshot> GetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
            => Unsupported<ScheduledTaskSnapshot>();

        public Task<WindowsSystemOperationResult> SetTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<WindowsSystemOperationResult> DeleteTaskAsync(ScheduledTaskProfile profile, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        public Task<WindowsSystemOperationResult> RestoreTaskAsync(string name, string xml, CancellationToken cancellationToken)
            => Unsupported<WindowsSystemOperationResult>();

        private static Task<T> Unsupported<T>()
            => Task.FromException<T>(new PlatformNotSupportedException(
                "Windows System Control доступен только в Windows."));
    }
}