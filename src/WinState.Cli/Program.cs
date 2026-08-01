using System.Text;
using WinState.App;
using WinState.App.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
return await WinStateCli.RunAsync(args, CancellationToken.None);

internal static class WinStateCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        CliInvocation invocation;
        try
        {
            invocation = CliInvocation.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"[ERROR] {exception.Message}");
            return 2;
        }

        if (invocation.Arguments.Count == 0 || invocation.Arguments[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = invocation.Arguments[0].ToLowerInvariant();
        if (command is "--version" or "version")
        {
            Console.WriteLine($"WinState {WinStateApplication.Version}");
            return 0;
        }

        if (command == "architecture")
        {
            PrintArchitecture();
            return 0;
        }

        try
        {
            await using var application = WinStateApplication.Create(invocation.HomeOverride);
            return command switch
            {
                "doctor" => await DoctorAsync(application, cancellationToken),
                "validate" => await ValidateAsync(application, invocation.Arguments, cancellationToken),
                "config" => Config(application, invocation.Arguments),
                "storage" => await StorageAsync(application, invocation.Arguments, cancellationToken),
                _ => Unknown(command)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"[ERROR] {exception.Message}");
            return 4;
        }
    }

    private static async Task<int> DoctorAsync(WinStateApplication application, CancellationToken cancellationToken)
    {
        var report = await application.RunDoctorAsync(cancellationToken);
        Console.WriteLine("WINSTATE DOCTOR");
        Console.WriteLine(new string('─', 56));
        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                DiagnosticStatus.Ok => "OK",
                DiagnosticStatus.Warning => "WARN",
                _ => "FAIL"
            };
            Console.WriteLine($"[{marker,-4}] {check.Name,-18} {check.Message}");
        }

        Console.WriteLine(new string('─', 56));
        Console.WriteLine(report.IsHealthy ? "Состояние: готово к работе." : "Состояние: обнаружены критические проблемы.");
        return report.IsHealthy ? 0 : 5;
    }

    private static async Task<int> ValidateAsync(
        WinStateApplication application,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Использование: winstate validate <путь-к-winstate.yaml>");
            return 2;
        }

        var result = await application.ValidateProfileAsync(arguments[1], cancellationToken);
        Console.WriteLine($"Профиль: {result.Profile.Metadata.Name}");
        Console.WriteLine($"Schema:  {result.Profile.SchemaVersion}");
        Console.WriteLine($"User environment:    {result.Profile.Environment.User.Count}");
        Console.WriteLine($"Machine environment: {result.Profile.Environment.Machine.Count}");

        if (result.Validation.IsValid)
        {
            Console.WriteLine("[OK] Базовая структура профиля корректна.");
            return 0;
        }

        foreach (var issue in result.Validation.Issues)
        {
            Console.Error.WriteLine($"[ERROR] {issue.Path}: {issue.Message} ({issue.Code})");
        }

        return 3;
    }

    private static int Config(WinStateApplication application, IReadOnlyList<string> arguments)
    {
        var subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : "show";
        if (subcommand == "path")
        {
            Console.WriteLine(application.Options.ConfigPath);
            return 0;
        }

        if (subcommand != "show")
        {
            Console.Error.WriteLine("Использование: winstate config [show|path]");
            return 2;
        }

        Console.WriteLine($"Home:      {application.Options.HomeDirectory}");
        Console.WriteLine($"Profiles:  {application.Options.ProfilesDirectory}");
        Console.WriteLine($"Database:  {application.Options.DatabasePath}");
        Console.WriteLine($"Logs:      {application.Options.LogsDirectory}");
        Console.WriteLine($"Config:    {application.Options.ConfigPath}");
        Console.WriteLine($"Portable:  {application.Options.PortableMode}");
        Console.WriteLine($"Log level: {application.Options.MinimumLogLevel}");
        return 0;
    }

    private static async Task<int> StorageAsync(
        WinStateApplication application,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : "status";
        if (subcommand is not ("migrate" or "status"))
        {
            Console.Error.WriteLine("Использование: winstate storage [migrate|status]");
            return 2;
        }

        await application.InitializeStorageAsync(cancellationToken);
        var status = await application.GetStorageStatusAsync(cancellationToken);
        Console.WriteLine($"Database:   {status.DatabasePath}");
        Console.WriteLine($"Migrations: {status.AppliedMigrations}");
        Console.WriteLine($"Schema:     {status.LatestMigrationVersion}");
        Console.WriteLine($"Size:       {status.DatabaseSizeBytes} bytes");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"[ERROR] Неизвестная команда: {command}");
        Console.Error.WriteLine("Выполните: winstate --help");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WINSTATE
        Git для конфигурации Windows.

        Текущий этап: application skeleton 0.2.0-alpha.1

        Команды:
          winstate --help                         Показать справку
          winstate --version                      Показать версию
          winstate architecture                   Показать границы модулей
          winstate doctor [--home <path>]         Проверить конфигурацию и SQLite
          winstate validate <profile>             Проверить bootstrap YAML
          winstate config [show|path]             Показать вычисленные настройки
          winstate storage [migrate|status]       Управлять локальной схемой SQLite

        Переменные окружения:
          WINSTATE_HOME, WINSTATE_PROFILES, WINSTATE_DATABASE,
          WINSTATE_LOGS, WINSTATE_LOG_LEVEL, WINSTATE_PORTABLE

        Следующий этап:
          полный Profile Engine: includes, variables и normalization
        """);
    }

    private static void PrintArchitecture()
    {
        Console.WriteLine("""
        CLI → App composition root → Core engines
                    │                 │
                    ├─ Infrastructure └─ Domain contracts
                    └─ SQLite Storage

        Domain:         модели и контракты без внешних интеграций
        Core:           профили, валидация и планирование
        Infrastructure: конфигурация, пути и платформенные адаптеры
        Storage:        SQLite, миграции, ownership и история
        App:            DI, logging и прикладные сценарии
        CLI:            команды, вывод и exit codes
        """);
    }

    private sealed record CliInvocation(IReadOnlyList<string> Arguments, string? HomeOverride)
    {
        public static CliInvocation Parse(IReadOnlyList<string> args)
        {
            var filtered = new List<string>();
            string? home = null;
            for (var index = 0; index < args.Count; index++)
            {
                if (!args[index].Equals("--home", StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(args[index]);
                    continue;
                }

                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException("После --home необходимо указать путь.");
                }

                home = args[++index];
            }

            return new CliInvocation(filtered, home);
        }
    }
}
