using System.Text;
using WinState.App;
using WinState.App.Diagnostics;
using WinState.Terminal;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
return await WinStateCli.RunAsync(args, cancellation.Token);

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

        if (invocation.Arguments.Count == 0)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                PrintHelp();
                return 0;
            }

            await using var interactiveApplication = WinStateApplication.Create(invocation.HomeOverride, quiet: true);
            return await new WinStateTerminalShell(interactiveApplication).RunAsync(false, cancellationToken);
        }

        if (invocation.Arguments[0] is "--help" or "-h" or "help")
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
            var quiet = command == "ui";
            await using var application = WinStateApplication.Create(invocation.HomeOverride, quiet: quiet);
            return command switch
            {
                "ui" => await UiAsync(application, invocation.Arguments, cancellationToken),
                "doctor" => await DoctorAsync(application, cancellationToken),
                "validate" => await ValidateAsync(application, invocation.Arguments, invocation.Variables, cancellationToken),
                "config" => Config(application, invocation.Arguments),
                "storage" => await StorageAsync(application, invocation.Arguments, cancellationToken),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[CANCELLED] Операция отменена пользователем.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"[ERROR] {exception.Message}");
            return 4;
        }
    }

    private static Task<int> UiAsync(
        WinStateApplication application,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var demo = arguments.Skip(1).Any(value => value.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        if (!demo && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            Console.Error.WriteLine("[ERROR] Интерактивная панель требует доступный терминал. Для smoke test используйте: winstate ui --demo");
            return Task.FromResult(2);
        }

        return new WinStateTerminalShell(application).RunAsync(demo, cancellationToken);
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
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Использование: winstate validate <profile> [--var name=value]");
            return 2;
        }

        var result = await application.ValidateProfileAsync(arguments[1], variables, cancellationToken);
        var profile = result.Loaded.Profile;
        Console.WriteLine($"Профиль:   {profile.Metadata.Name}");
        Console.WriteLine($"Schema:    {profile.SchemaVersion}");
        Console.WriteLine($"Источники: {result.Loaded.SourceFiles.Count}");
        Console.WriteLine($"Переменные:{result.Loaded.Variables.Count}");
        Console.WriteLine($"User environment:    {profile.Environment.User.Count}");
        Console.WriteLine($"Machine environment: {profile.Environment.Machine.Count}");
        Console.WriteLine($"PATH entries:         {profile.Environment.UserPath.Count + profile.Environment.MachinePath.Count}");

        if (result.Validation.IsValid)
        {
            Console.WriteLine("[OK] Профиль загружен, объединён и нормализован.");
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

        Без аргументов запускается интерактивный Control Center.

        Команды:
          winstate                               Открыть панель со стрелочным управлением
          winstate ui [--demo]                   Открыть панель / вывести CI-превью
          winstate --help                        Показать справку
          winstate --version                     Показать версию
          winstate architecture                  Показать границы модулей
          winstate doctor [--home <path>]        Проверить конфигурацию и SQLite
          winstate validate <profile>            Загрузить и проверить полный YAML
                    [--var name=value]            Переопределить переменную профиля
          winstate config [show|path]            Показать вычисленные настройки
          winstate storage [migrate|status]      Управлять локальной схемой SQLite

        Управление панелью:
          ↑ / ↓     перемещение
          ENTER     открыть выбранный раздел
          любая клавиша — вернуться в главное меню

        Profile Engine:
          includes, extends, variables, WINSTATE_VAR_*, normalization
        """);
    }

    private static void PrintArchitecture()
    {
        Console.WriteLine("""
        Terminal UI → App composition root → Core engines
                          │                 │
                          ├─ Infrastructure └─ Domain contracts
                          └─ SQLite Storage

        Terminal:       панели, меню, анимации и стрелочное управление
        App:            DI, logging и прикладные сценарии
        Core:           Profile Engine, validation и planning
        Infrastructure: конфигурация, пути и платформенные адаптеры
        Storage:        SQLite, миграции, ownership и история
        Domain:         модели и provider contracts
        """);
    }

    private sealed record CliInvocation(
        IReadOnlyList<string> Arguments,
        string? HomeOverride,
        IReadOnlyDictionary<string, string> Variables)
    {
        public static CliInvocation Parse(IReadOnlyList<string> args)
        {
            var filtered = new List<string>();
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? home = null;
            for (var index = 0; index < args.Count; index++)
            {
                if (args[index].Equals("--home", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                    {
                        throw new ArgumentException("После --home необходимо указать путь.");
                    }

                    home = args[++index];
                    continue;
                }

                if (args[index].Equals("--var", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Count)
                    {
                        throw new ArgumentException("После --var необходимо указать name=value.");
                    }

                    var assignment = args[++index];
                    var separator = assignment.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException($"Некорректная переменная '{assignment}'. Используйте name=value.");
                    }

                    variables[assignment[..separator].Trim()] = assignment[(separator + 1)..];
                    continue;
                }

                filtered.Add(args[index]);
            }

            return new CliInvocation(filtered, home, variables);
        }
    }
}
