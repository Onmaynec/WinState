using System.Text;
using System.Text.Json;
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
            Console.Error.WriteLine($"[ОШИБКА] {exception.Message}");
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
                "capture" => await CaptureAsync(application, invocation.Arguments, cancellationToken),
                "drift" => await DriftAsync(application, invocation.Arguments, invocation.Variables, cancellationToken),
                "workspace" => await WorkspaceAsync(application, invocation, cancellationToken),
                "update" => await UpdateAsync(application, invocation, cancellationToken),
                "environment" or "env" => await EnvironmentAsync(application, invocation, cancellationToken),
                "config" => Config(application, invocation.Arguments),
                "storage" => await StorageAsync(application, invocation.Arguments, cancellationToken),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[ОТМЕНЕНО] Операция отменена пользователем.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or PlatformNotSupportedException
            or JsonException)
        {
            Console.Error.WriteLine($"[ОШИБКА] {exception.Message}");
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
            Console.Error.WriteLine("[ОШИБКА] Интерактивная панель требует терминал. Для проверки используйте: winstate ui --demo");
            return Task.FromResult(2);
        }

        return new WinStateTerminalShell(application).RunAsync(demo, cancellationToken);
    }

    private static async Task<int> DoctorAsync(WinStateApplication application, CancellationToken cancellationToken)
    {
        var report = await application.RunDoctorAsync(cancellationToken);
        Console.WriteLine("ДИАГНОСТИКА WINSTATE");
        Console.WriteLine(new string('─', 64));
        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                DiagnosticStatus.Ok => "ГОТОВО",
                DiagnosticStatus.Warning => "ВНИМАНИЕ",
                _ => "ОШИБКА"
            };
            Console.WriteLine($"[{marker,-9}] {check.Name,-18} {check.Message}");
        }

        Console.WriteLine(new string('─', 64));
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
            Console.Error.WriteLine("Использование: winstate validate <профиль> [--var имя=значение]");
            return 2;
        }

        var result = await application.ValidateProfileAsync(arguments[1], variables, cancellationToken);
        var profile = result.Loaded.Profile;
        Console.WriteLine($"Профиль:             {profile.Metadata.Name}");
        Console.WriteLine($"Версия схемы:        {profile.SchemaVersion}");
        Console.WriteLine($"Исходных файлов:     {result.Loaded.SourceFiles.Count}");
        Console.WriteLine($"Переменных шаблона:  {result.Loaded.Variables.Count}");
        Console.WriteLine($"User environment:    {profile.Environment.User.Count}");
        Console.WriteLine($"Machine environment: {profile.Environment.Machine.Count}");
        Console.WriteLine($"Записей PATH:        {profile.Environment.UserPath.Count + profile.Environment.MachinePath.Count}");
        Console.WriteLine($"Пакетов:             {profile.Packages.Count}");
        Console.WriteLine($"Компонентов Windows: {profile.Features.Count}");

        if (result.Validation.IsValid)
        {
            Console.WriteLine("[ГОТОВО] Профиль загружен, объединён и нормализован.");
            return 0;
        }

        foreach (var issue in result.Validation.Issues)
        {
            Console.Error.WriteLine($"[ОШИБКА] {issue.Path}: {issue.Message} ({issue.Code})");
        }

        return 3;
    }

    private static async Task<int> CaptureAsync(
        WinStateApplication application,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Использование: winstate capture <снимок.yaml> [название профиля]");
            return 2;
        }

        var name = arguments.Count > 2 ? string.Join(' ', arguments.Skip(2)) : null;
        Console.WriteLine("СОЗДАНИЕ СНИМКА WINSTATE");
        Console.WriteLine(new string('─', 72));
        var report = await application.CaptureAsync(arguments[1], name, cancellationToken);
        Console.WriteLine($"Профиль:             {report.ProfileName}");
        Console.WriteLine($"YAML:                {report.ProfilePath}");
        Console.WriteLine($"Манифест:            {report.ManifestPath}");
        Console.WriteLine($"SHA-256:             {report.Sha256}");
        Console.WriteLine($"User variables:      {report.Counts.UserVariables}");
        Console.WriteLine($"Machine variables:   {report.Counts.MachineVariables}");
        Console.WriteLine($"Записей PATH:        {report.Counts.UserPathEntries + report.Counts.MachinePathEntries}");
        Console.WriteLine($"Пакетов WinGet:      {report.Counts.Packages}");
        Console.WriteLine($"Компонентов Windows: {report.Counts.EnabledFeatures}");
        Console.WriteLine($"Пропущено секретов:  {report.Counts.SkippedSensitiveValues}");
        foreach (var diagnostic in report.Diagnostics)
        {
            Console.WriteLine($"[ВНИМАНИЕ] {diagnostic}");
        }

        Console.WriteLine("[ГОТОВО] Снимок записан атомарно и снабжён проверяемым манифестом.");
        return 0;
    }

    private static async Task<int> DriftAsync(
        WinStateApplication application,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2)
        {
            Console.Error.WriteLine("Использование: winstate drift <профиль.yaml> [отчёт.json]");
            return 2;
        }

        var reportPath = arguments.Count > 2 ? arguments[2] : null;
        var report = await application.CheckDriftAsync(
            arguments[1],
            variables,
            reportPath,
            cancellationToken);
        Console.WriteLine("КОНТРОЛЬ ОТКЛОНЕНИЙ WINSTATE");
        Console.WriteLine(new string('─', 88));
        Console.WriteLine($"Профиль:           {report.ProfileName}");
        Console.WriteLine($"Проверен:          {report.CheckedAt:O}");
        Console.WriteLine($"Валидный:          {report.IsValid}");
        Console.WriteLine($"Поддерживается:    {report.IsSupported}");
        Console.WriteLine($"Изменений:         {report.Changes}");
        Console.WriteLine($"Опасных изменений: {report.DestructiveChanges}");
        Console.WriteLine($"Максимальный риск: {report.MaximumRisk}");
        if (report.ReportPath is not null)
        {
            Console.WriteLine($"JSON-отчёт:        {report.ReportPath}");
        }

        foreach (var diagnostic in report.Diagnostics)
        {
            Console.WriteLine($"[ДИАГНОСТИКА] {diagnostic}");
        }

        foreach (var action in report.Actions)
        {
            Console.WriteLine($"[{action.Risk,-8}] {action.ProviderId,-24} {action.Operation,-10} {action.Resource}");
            Console.WriteLine($"           {action.Explanation}");
        }

        if (!report.IsValid)
        {
            Console.Error.WriteLine("[ОШИБКА] Профиль не прошёл проверку.");
            return 3;
        }

        if (!report.IsSupported)
        {
            Console.Error.WriteLine("[ОШИБКА] Один или несколько провайдеров недоступны.");
            return 6;
        }

        if (report.HasDrift)
        {
            Console.WriteLine("[ОТКЛОНЕНИЕ] Текущее состояние отличается от профиля.");
            return 10;
        }

        Console.WriteLine("[ГОТОВО] Отклонения не обнаружены.");
        return 0;
    }

    private static async Task<int> WorkspaceAsync(
        WinStateApplication application,
        CliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        var subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : "status";
        if (subcommand == "status")
        {
            var status = await application.GetWorkspaceStatusAsync(cancellationToken);
            Console.WriteLine("WORKSPACE CONTROL WINSTATE");
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"Ownership ledger: {status.OwnershipPath}");
            Console.WriteLine($"Git settings:     {status.OwnedGitSettings}");
            Console.WriteLine($"PowerShell modules:{status.OwnedModules}");
            Console.WriteLine($"Managed files:    {status.OwnedFiles}");
            Console.WriteLine($"Managed folders:  {status.OwnedDirectories}");
            Console.WriteLine($"Последняя транзакция: {status.LatestTransactionPath ?? "нет"}");
            return 0;
        }

        if (subcommand == "rollback")
        {
            if (arguments.Count < 3)
            {
                Console.Error.WriteLine("Использование: winstate workspace rollback <transaction.json> --yes");
                return 2;
            }

            if (!invocation.AssumeYes)
            {
                Console.Error.WriteLine("[ЗАЩИТА] Workspace rollback требует явный флаг --yes.");
                return 2;
            }

            var result = await application.RollbackWorkspaceAsync(arguments[2], cancellationToken);
            Console.WriteLine($"Транзакция: {result.TransactionId}");
            Console.WriteLine($"Успешно:    {result.Succeeded}");
            Console.WriteLine($"Восстановлено: {result.RestoredActions}");
            Console.WriteLine($"Пропущено:     {result.SkippedActions}");
            foreach (var message in result.Messages)
            {
                Console.WriteLine(message);
            }

            return result.Succeeded ? 0 : 7;
        }

        if (subcommand is not ("validate" or "plan" or "apply"))
        {
            Console.Error.WriteLine(
                "Использование: winstate workspace [status|validate <manifest>|plan <manifest>|apply <manifest> --yes|rollback <transaction> --yes]");
            return 2;
        }

        if (arguments.Count < 3)
        {
            Console.Error.WriteLine($"Использование: winstate workspace {subcommand} <manifest.json>");
            return 2;
        }

        var manifestPath = arguments[2];
        if (subcommand == "validate")
        {
            var validation = await application.ValidateWorkspaceAsync(manifestPath, cancellationToken);
            Console.WriteLine($"Workspace: {validation.Name}");
            Console.WriteLine($"Manifest:  {validation.ManifestPath}");
            foreach (var issue in validation.Issues)
            {
                Console.WriteLine($"[ОШИБКА] {issue}");
            }

            Console.WriteLine(validation.IsValid
                ? "[ГОТОВО] Workspace manifest валиден."
                : "[ОШИБКА] Workspace manifest содержит ошибки.");
            return validation.IsValid ? 0 : 3;
        }

        var reportDirectory = OptionValue(arguments, "--report");
        if (subcommand == "plan")
        {
            var plan = await application.PlanWorkspaceAsync(
                manifestPath,
                reportDirectory,
                cancellationToken);
            PrintWorkspacePlan(plan);
            if (!plan.IsValid)
            {
                return 3;
            }

            if (!plan.IsSupported)
            {
                return 6;
            }

            return plan.Actions.Any(action => action.Blocked) ? 8 : 0;
        }

        if (!invocation.AssumeYes)
        {
            Console.Error.WriteLine("[ЗАЩИТА] Workspace apply требует --yes после просмотра плана.");
            return 2;
        }

        var execution = await application.ApplyWorkspaceAsync(
            manifestPath,
            HasFlag(arguments, "--allow-modules"),
            HasFlag(arguments, "--allow-delete"),
            reportDirectory,
            cancellationToken);
        Console.WriteLine("ПРИМЕНЕНИЕ WORKSPACE CONTROL");
        Console.WriteLine(new string('─', 88));
        Console.WriteLine($"Транзакция: {execution.TransactionId}");
        Console.WriteLine($"Успешно:    {execution.Succeeded}");
        Console.WriteLine($"Откат:      {execution.RolledBack}");
        Console.WriteLine($"Применено:  {execution.AppliedActions}");
        Console.WriteLine($"Ошибок:     {execution.FailedActions}");
        Console.WriteLine($"Manifest транзакции: {execution.TransactionPath}");
        Console.WriteLine($"JSON-отчёт: {execution.JsonReportPath}");
        Console.WriteLine($"Markdown:   {execution.MarkdownReportPath}");
        foreach (var message in execution.Messages)
        {
            Console.WriteLine(message);
        }

        return execution.Succeeded ? 0 : 7;
    }

    private static void PrintWorkspacePlan(WorkspacePlanReport plan)
    {
        Console.WriteLine("ПЛАН WORKSPACE CONTROL");
        Console.WriteLine(new string('─', 88));
        Console.WriteLine($"Workspace:          {plan.Name}");
        Console.WriteLine($"Manifest:           {plan.ManifestPath}");
        Console.WriteLine($"Валидный:           {plan.IsValid}");
        Console.WriteLine($"Providers доступны: {plan.IsSupported}");
        Console.WriteLine($"Изменений:          {plan.Changes}");
        Console.WriteLine($"Удалений:           {plan.DestructiveChanges}");
        Console.WriteLine($"Необратимых:        {plan.IrreversibleChanges}");
        Console.WriteLine($"JSON-отчёт:         {plan.JsonReportPath}");
        Console.WriteLine($"Markdown:           {plan.MarkdownReportPath}");
        foreach (var diagnostic in plan.Diagnostics)
        {
            Console.WriteLine($"[ДИАГНОСТИКА] {diagnostic}");
        }

        foreach (var action in plan.Actions)
        {
            var marker = action.Blocked ? "БЛОК" : action.Risk;
            Console.WriteLine($"[{marker,-8}] {action.Provider,-20} {action.Operation,-18} {action.Resource}");
            Console.WriteLine($"           {action.Explanation}");
        }

        if (plan.Actions.Count == 0 && plan.IsValid && plan.IsSupported)
        {
            Console.WriteLine("[ГОТОВО] Workspace уже соответствует manifest.");
        }
    }

    private static async Task<int> UpdateAsync(
        WinStateApplication application,
        CliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        var subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : string.Empty;
        if (subcommand is not ("restore" or "prepare-restore") || arguments.Count < 3)
        {
            Console.Error.WriteLine(
                "Использование: winstate update restore <backup-directory> --yes [--install <directory>]");
            return 2;
        }

        var launch = subcommand == "restore";
        if (launch && !invocation.AssumeYes)
        {
            Console.Error.WriteLine("[ЗАЩИТА] Восстановление updater backup требует --yes.");
            return 2;
        }

        var result = await application.PrepareUpdateRestoreAsync(
            arguments[2],
            OptionValue(arguments, "--install"),
            launch,
            cancellationToken);
        Console.WriteLine("ВОССТАНОВЛЕНИЕ ОБНОВЛЕНИЯ WINSTATE");
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"Backup:          {result.BackupDirectory}");
        Console.WriteLine($"Установка:       {result.InstallDirectory}");
        Console.WriteLine($"Safety backup:   {result.SafetyBackupDirectory}");
        Console.WriteLine($"Restore script:  {result.ScriptPath}");
        Console.WriteLine($"Запланировано:   {result.Scheduled}");
        Console.WriteLine(result.Message);
        return 0;
    }

    private static bool HasFlag(IReadOnlyList<string> arguments, string flag)
        => arguments.Any(value => value.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? OptionValue(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"После {option} необходимо указать значение.");
            }

            return arguments[index + 1];
        }

        return null;
    }

    private static async Task<int> EnvironmentAsync(
        WinStateApplication application,
        CliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        var subcommand = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : "status";
        if (subcommand == "status")
        {
            var status = await application.GetEnvironmentStatusAsync(cancellationToken);
            Console.WriteLine("ПРОВАЙДЕР ОКРУЖЕНИЯ WINSTATE");
            Console.WriteLine(new string('─', 64));
            Console.WriteLine($"Поддерживается:      {(status.IsSupported ? "ДА" : "НЕТ")}");
            Console.WriteLine($"User variables:      {status.UserVariables}");
            Console.WriteLine($"Machine variables:   {status.MachineVariables}");
            Console.WriteLine($"User PATH entries:   {status.UserPathEntries}");
            Console.WriteLine($"Machine PATH entries:{status.MachinePathEntries}");
            foreach (var diagnostic in status.Diagnostics)
            {
                Console.WriteLine($"[{(diagnostic.IsWarning ? "ВНИМАНИЕ" : "ИНФО")}] {diagnostic.Message}");
            }

            return 0;
        }

        if (subcommand == "checkpoints")
        {
            var checkpoints = await application.ListEnvironmentCheckpointsAsync(cancellationToken);
            if (checkpoints.Count == 0)
            {
                Console.WriteLine("Контрольные точки окружения не найдены.");
                return 0;
            }

            foreach (var checkpoint in checkpoints)
            {
                Console.WriteLine(
                    $"{checkpoint.CreatedAt:yyyy-MM-dd HH:mm:ss}  {checkpoint.Status,-15}  "
                    + $"{checkpoint.TransactionId}  {checkpoint.ProfileName}");
                Console.WriteLine($"  {checkpoint.ManifestPath}");
            }

            return 0;
        }

        if (subcommand == "rollback")
        {
            if (arguments.Count < 3)
            {
                Console.Error.WriteLine("Использование: winstate environment rollback <манифест|каталог> --yes");
                return 2;
            }

            if (!invocation.AssumeYes)
            {
                Console.Error.WriteLine("[ЗАЩИТА] Откат требует явный флаг --yes.");
                return 2;
            }

            var result = await application.RollbackEnvironmentAsync(arguments[2], cancellationToken);
            PrintExecution(result);
            return result.Succeeded ? 0 : 7;
        }

        if (subcommand is not ("plan" or "apply"))
        {
            Console.Error.WriteLine(
                "Использование: winstate environment [status|plan <профиль>|apply <профиль> --yes|checkpoints|rollback <точка> --yes]");
            return 2;
        }

        if (arguments.Count < 3)
        {
            Console.Error.WriteLine($"Использование: winstate environment {subcommand} <профиль>");
            return 2;
        }

        var plan = await application.PlanEnvironmentAsync(
            arguments[2],
            invocation.Variables,
            cancellationToken);
        PrintEnvironmentPlan(plan);
        if (!plan.Validation.IsValid)
        {
            return 3;
        }

        if (!plan.IsSupported)
        {
            return 6;
        }

        if (subcommand == "plan")
        {
            return 0;
        }

        if (!invocation.AssumeYes)
        {
            Console.Error.WriteLine("[ЗАЩИТА] Применение требует --yes после просмотра плана.");
            return 2;
        }

        var hasMachineActions = plan.Actions.Any(action => action.RequiresAdministrator);
        if (hasMachineActions && !invocation.AllowMachine)
        {
            Console.Error.WriteLine(
                "[ЗАЩИТА] План содержит Machine scope. Добавьте --allow-machine и запустите терминал от администратора.");
            return 2;
        }

        var execution = await application.ApplyEnvironmentAsync(
            arguments[2],
            invocation.Variables,
            invocation.AllowMachine,
            invocation.AutomaticRollback,
            cancellationToken);
        PrintExecution(execution);
        return execution.Succeeded ? 0 : 7;
    }

    private static void PrintEnvironmentPlan(EnvironmentPlanReport plan)
    {
        Console.WriteLine("ПЛАН ИЗМЕНЕНИЯ ОКРУЖЕНИЯ");
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"Профиль:          {plan.Loaded.Profile.Metadata.Name}");
        Console.WriteLine($"Поддерживается:   {plan.IsSupported}");
        Console.WriteLine($"Изменений:        {plan.Summary.Changes}");
        Console.WriteLine($"Machine actions:  {plan.Summary.AdministratorActions}");
        Console.WriteLine($"Максимальный риск:{plan.Summary.MaximumRisk}");
        foreach (var issue in plan.Validation.Issues)
        {
            Console.WriteLine($"[ОШИБКА] {issue.Path}: {issue.Message}");
        }

        foreach (var diagnostic in plan.Diagnostics)
        {
            Console.WriteLine($"[{(diagnostic.IsWarning ? "ВНИМАНИЕ" : "ИНФО")}] {diagnostic.Message}");
        }

        foreach (var action in plan.Actions)
        {
            var scope = Property(action, "scope");
            var resource = action.Resource.ResourceType.EndsWith("variable", StringComparison.Ordinal)
                ? Property(action, "name")
                : Property(action, "path");
            Console.WriteLine($"[{action.Risk,-6}] {scope,-7} {action.Operation,-8} {resource}");
            Console.WriteLine($"         {action.Explanation}");
        }

        if (plan.Actions.Count == 0 && plan.Validation.IsValid && plan.IsSupported)
        {
            Console.WriteLine("[ГОТОВО] Изменения не требуются.");
        }
    }

    private static void PrintExecution(EnvironmentExecutionReport result)
    {
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"Транзакция:       {result.TransactionId}");
        Console.WriteLine($"Профиль:          {result.ProfileName}");
        Console.WriteLine($"Успешно:          {result.Succeeded}");
        Console.WriteLine($"Проверено:        {result.Verified}");
        Console.WriteLine($"Выполнен откат:   {result.RolledBack}");
        Console.WriteLine($"Контрольная точка:{result.CheckpointManifest ?? "нет"}");
        foreach (var action in result.Actions)
        {
            Console.WriteLine($"[{action.Status}] {action.ActionId}: {action.Message}");
        }

        Console.WriteLine(result.Message);
    }

    private static string Property(WinState.Domain.Planning.PlannedAction action, string name)
        => action.Resource.Properties.TryGetValue(name, out var value)
            ? value.Value ?? string.Empty
            : string.Empty;

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

        Console.WriteLine($"Домашний каталог: {application.Options.HomeDirectory}");
        Console.WriteLine($"Профили:          {application.Options.ProfilesDirectory}");
        Console.WriteLine($"База данных:      {application.Options.DatabasePath}");
        Console.WriteLine($"Журналы:          {application.Options.LogsDirectory}");
        Console.WriteLine($"Конфигурация:     {application.Options.ConfigPath}");
        Console.WriteLine($"Переносной режим: {application.Options.PortableMode}");
        Console.WriteLine($"Уровень журнала:  {application.Options.MinimumLogLevel}");
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
        Console.WriteLine($"База данных: {status.DatabasePath}");
        Console.WriteLine($"Миграций:    {status.AppliedMigrations}");
        Console.WriteLine($"Схема:       {status.LatestMigrationVersion}");
        Console.WriteLine($"Размер:      {status.DatabaseSizeBytes} байт");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"[ОШИБКА] Неизвестная команда: {command}");
        Console.Error.WriteLine("Выполните: winstate --help");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        WINSTATE 1.0
        Управление конфигурацией Windows и рабочего окружения как кодом.

        Основные команды:
          winstate                                  Открыть интерактивную панель
          winstate ui [--demo]                      Открыть панель или вывести CI-превью
          winstate --help                           Показать справку
          winstate --version                        Показать версию
          winstate architecture                     Показать границы модулей
          winstate doctor [--home <путь>]           Проверить конфигурацию и SQLite
          winstate validate <профиль>               Проверить полный YAML-профиль
          winstate capture <снимок.yaml> [название] Создать безопасный снимок системы
          winstate drift <профиль> [отчёт.json]     Найти отклонения без изменений системы

        Workspace Control:
          winstate workspace status                 Показать ownership и последнюю транзакцию
          winstate workspace validate <manifest>    Проверить JSON manifest
          winstate workspace plan <manifest>        Построить план и JSON/Markdown-отчёт
                    [--report <каталог>]
          winstate workspace apply <manifest>       Применить после просмотра плана
                    --yes [--allow-modules] [--allow-delete]
          winstate workspace rollback <transaction> Восстановить обратимые действия с --yes

        Система и восстановление:
          winstate environment status               Показать состояние Environment Provider
          winstate environment plan <профиль>       Построить безопасный план
          winstate environment apply <профиль>      Применить план с --yes
          winstate environment checkpoints          Показать контрольные точки
          winstate environment rollback <путь>      Выполнить откат с --yes
          winstate update restore <backup>           Восстановить updater backup с --yes
                    [--install <каталог>]
          winstate config [show|path]               Показать вычисленные настройки
          winstate storage [migrate|status]         Управлять локальной схемой SQLite

        Безопасность Workspace Control:
          Удаляются только ресурсы, записанные в ownership ledger WinState.
          Непустые каталоги и PowerShell modules автоматически не удаляются.
          --allow-modules отдельно разрешает Install-Module для CurrentUser.
          --allow-delete отдельно разрешает управляемые удаления.
          Перед каждой заменой файла и настройкой Git создаётся backup.
        """);
    }

    private static void PrintArchitecture()
    {
        Console.WriteLine("""
        Terminal UI → App workflows → Core engines → Provider contracts
                          │                │                 │
                          ├─ Capture/Drift ├─ Profile Engine ├─ Environment
                          ├─ Workspace     ├─ Apply Engine   ├─ WinGet / DISM
                          ├─ Recovery      └─ Ownership      ├─ Windows System
                          └─ SQLite Storage                  └─ Git / PS / Files

        Workspace:      plan → ownership gate → checkpoint → apply → report → rollback
        Ownership:      versioned JSON ledger с migration compatibility
        Reports:        атомарные JSON и Markdown для plan/apply
        Recovery:       safety backup и отложенное восстановление updater backup
        """);
    }

    private sealed record CliInvocation(
        IReadOnlyList<string> Arguments,
        string? HomeOverride,
        IReadOnlyDictionary<string, string> Variables,
        bool AssumeYes,
        bool AllowMachine,
        bool AutomaticRollback)
    {
        public static CliInvocation Parse(IReadOnlyList<string> args)
        {
            var filtered = new List<string>();
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? home = null;
            var assumeYes = false;
            var allowMachine = false;
            var automaticRollback = true;
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
                        throw new ArgumentException("После --var необходимо указать имя=значение.");
                    }

                    var assignment = args[++index];
                    var separator = assignment.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException($"Некорректная переменная '{assignment}'. Используйте имя=значение.");
                    }

                    variables[assignment[..separator].Trim()] = assignment[(separator + 1)..];
                    continue;
                }

                if (args[index].Equals("--yes", StringComparison.OrdinalIgnoreCase))
                {
                    assumeYes = true;
                    continue;
                }

                if (args[index].Equals("--allow-machine", StringComparison.OrdinalIgnoreCase))
                {
                    allowMachine = true;
                    continue;
                }

                if (args[index].Equals("--no-auto-rollback", StringComparison.OrdinalIgnoreCase))
                {
                    automaticRollback = false;
                    continue;
                }

                filtered.Add(args[index]);
            }

            return new CliInvocation(
                filtered,
                home,
                variables,
                assumeYes,
                allowMachine,
                automaticRollback);
        }
    }
}
