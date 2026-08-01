using System.Runtime.InteropServices;
using Spectre.Console;
using WinState.App;
using WinState.App.Diagnostics;
using WinState.Core.Profiles;
using WinState.Domain.Planning;

namespace WinState.Terminal;

public sealed class WinStateTerminalShell
{
    private static readonly IReadOnlyList<MenuEntry> MainMenu =
    [
        new("dashboard", "Обзор системы", "Платформа, каталоги, профили и локальное состояние"),
        new("profiles", "Центр профилей", "Поиск и проверка YAML-профилей"),
        new("environment", "Environment Center", "Plan, checkpoint, apply, verify и rollback"),
        new("doctor", "Диагностика", "Проверка среды, конфигурации и SQLite"),
        new("storage", "Хранилище", "Миграции, схема и состояние базы"),
        new("configuration", "Конфигурация", "Пути, режим и параметры WinState"),
        new("roadmap", "Архитектура и roadmap", "Текущие модули и следующий vertical slice"),
        new("exit", "Выход", "Завершить работу WinState")
    ];

    private readonly WinStateApplication _application;

    public WinStateTerminalShell(WinStateApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public async Task<int> RunAsync(bool demoMode, CancellationToken cancellationToken)
    {
        Console.Title = $"WinState {WinStateApplication.Version}";
        if (demoMode)
        {
            await RenderDashboardAsync(cancellationToken);
            return 0;
        }

        await ShowBootAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            RenderHeader("CONTROL CENTER");
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<MenuEntry>()
                    .Title("[bold white]Выберите раздел[/]")
                    .PageSize(MainMenu.Count)
                    .HighlightStyle(new Style(Color.Black, Color.Cyan1))
                    .UseConverter(item => $"{item.Title} [grey]— {item.Description}[/]")
                    .AddChoices(MainMenu));

            switch (selected.Id)
            {
                case "dashboard":
                    await RenderDashboardAsync(cancellationToken);
                    break;
                case "profiles":
                    await ShowProfilesAsync(cancellationToken);
                    break;
                case "environment":
                    await ShowEnvironmentAsync(cancellationToken);
                    break;
                case "doctor":
                    await ShowDoctorAsync(cancellationToken);
                    break;
                case "storage":
                    await ShowStorageAsync(cancellationToken);
                    break;
                case "configuration":
                    ShowConfiguration();
                    break;
                case "roadmap":
                    ShowRoadmap();
                    break;
                case "exit":
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[cyan1]WinState завершён. Состояние сохранено.[/]");
                    return 0;
            }
        }

        return 130;
    }

    private async Task ShowBootAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear();
        DrawLogo();
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Запуск модулей WinState...[/]", async _ =>
            {
                await Task.Delay(140, cancellationToken);
                await _application.InitializeStorageAsync(cancellationToken);
                await Task.Delay(100, cancellationToken);
            });
    }

    private async Task RenderDashboardAsync(CancellationToken cancellationToken)
    {
        RenderHeader("SYSTEM OVERVIEW");
        StorageStatusSnapshot storage = new("не инициализирована", 0, 0, 0);
        IReadOnlyList<ProfileCatalogEntry> profiles = Array.Empty<ProfileCatalogEntry>();
        EnvironmentStatusReport environment = new(false, 0, 0, 0, 0, Array.Empty<WinState.Domain.Providers.ProviderDiagnostic>());

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Сбор состояния...[/]", async _ =>
            {
                await _application.InitializeStorageAsync(cancellationToken);
                var status = await _application.GetStorageStatusAsync(cancellationToken);
                storage = new StorageStatusSnapshot(
                    status.DatabasePath,
                    status.AppliedMigrations,
                    status.LatestMigrationVersion,
                    status.DatabaseSizeBytes);
                profiles = await _application.ListProfilesAsync(cancellationToken);
                environment = await _application.GetEnvironmentStatusAsync(cancellationToken);
            });

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn(new TableColumn("[bold cyan1]Узел[/]"))
            .AddColumn(new TableColumn("[bold white]Состояние[/]"));
        table.AddRow("Версия", Markup.Escape(WinStateApplication.Version));
        table.AddRow("Платформа", Markup.Escape($"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}"));
        table.AddRow("Режим", _application.Options.PortableMode ? "[yellow]Portable[/]" : "[green]User data[/]");
        table.AddRow("Профили", $"[white]{profiles.Count}[/]");
        table.AddRow("SQLite", $"[green]готово[/] · schema {storage.SchemaVersion} · {storage.Migrations} migration(s)");
        table.AddRow(
            "Environment Provider",
            environment.IsSupported
                ? $"[green]READY[/] · {environment.UserVariables + environment.MachineVariables} variables · {environment.UserPathEntries + environment.MachinePathEntries} PATH"
                : "[grey]Windows only[/]");
        table.AddRow("Каталог", Markup.Escape(_application.Options.HomeDirectory));
        AnsiConsole.Write(table);

        var statusText = environment.IsSupported
            ? "[green]● ONLINE[/]  [grey]Profile Engine · SQLite · Environment Provider ready[/]"
            : "[yellow]● LIMITED[/]  [grey]Profile Engine и SQLite готовы · Environment Provider требует Windows[/]";
        var statusPanel = new Panel(new Markup(statusText))
            .Header(new PanelHeader(" STATUS "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(environment.IsSupported ? Color.Green : Color.Yellow));
        AnsiConsole.Write(statusPanel);
        WaitForReturn();
    }

    private async Task ShowProfilesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderHeader("PROFILE CENTER");
            var profiles = await _application.ListProfilesAsync(cancellationToken);
            if (profiles.Count == 0)
            {
                AnsiConsole.Write(new Panel(
                        $"Профили не найдены.\n\nКаталог: [cyan1]{Markup.Escape(_application.Options.ProfilesDirectory)}[/]\n" +
                        "Скопируйте туда `.yaml` / `.yml` или используйте пример из `samples/profile-engine`.")
                    .Header(new PanelHeader(" EMPTY PROFILE LIBRARY "))
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Yellow)));
                WaitForReturn();
                return;
            }

            var back = new ProfileCatalogEntry("← Назад", string.Empty, 0, DateTimeOffset.MinValue);
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<ProfileCatalogEntry>()
                    .Title("[bold white]Выберите профиль для проверки[/]")
                    .PageSize(Math.Min(12, profiles.Count + 1))
                    .HighlightStyle(new Style(Color.Black, Color.Cyan1))
                    .UseConverter(item => string.IsNullOrEmpty(item.Path)
                        ? item.Name
                        : $"{item.Name} [grey]· {item.SizeBytes} bytes[/]")
                    .AddChoices(profiles.Concat([back])));

            if (string.IsNullOrEmpty(selected.Path))
            {
                return;
            }

            await ValidateProfileAsync(selected, cancellationToken);
        }
    }

    private async Task ValidateProfileAsync(ProfileCatalogEntry entry, CancellationToken cancellationToken)
    {
        LoadedProfile? loaded = null;
        ProfileValidationResult? validation = null;
        Exception? failure = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Загрузка includes, variables и normalization...[/]", async _ =>
            {
                try
                {
                    var result = await _application.ValidateProfileAsync(entry.Path, cancellationToken);
                    loaded = result.Loaded;
                    validation = result.Validation;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failure = exception;
                }
            });

        RenderHeader("PROFILE REPORT");
        if (failure is not null || loaded is null || validation is null)
        {
            var message = failure?.Message ?? "Profile Engine не вернул результат.";
            AnsiConsole.Write(new Panel(Markup.Escape(message))
                .Header(new PanelHeader(" LOAD FAILED "))
                .BorderStyle(new Style(Color.Red))
                .Border(BoxBorder.Rounded));
            WaitForReturn();
            return;
        }

        var profile = loaded.Profile;
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(validation.IsValid ? Color.Green : Color.Yellow))
            .AddColumn("Параметр")
            .AddColumn("Значение");
        summary.AddRow("Имя", Markup.Escape(profile.Metadata.Name));
        summary.AddRow("Источники", loaded.SourceFiles.Count.ToString());
        summary.AddRow("Переменные", loaded.Variables.Count.ToString());
        summary.AddRow("User environment", profile.Environment.User.Count.ToString());
        summary.AddRow("Machine environment", profile.Environment.Machine.Count.ToString());
        summary.AddRow("PATH entries", (profile.Environment.UserPath.Count + profile.Environment.MachinePath.Count).ToString());
        summary.AddRow("Результат", validation.IsValid ? "[green]VALID[/]" : "[yellow]ISSUES FOUND[/]");
        AnsiConsole.Write(summary);

        if (!validation.IsValid)
        {
            var issues = new Table().Border(TableBorder.Simple).AddColumn("Path").AddColumn("Проблема");
            foreach (var issue in validation.Issues)
            {
                issues.AddRow(Markup.Escape(issue.Path), Markup.Escape(issue.Message));
            }

            AnsiConsole.Write(issues);
        }

        WaitForReturn();
    }

    private async Task ShowEnvironmentAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderHeader("ENVIRONMENT CENTER");
            var status = await _application.GetEnvironmentStatusAsync(cancellationToken);
            if (!status.IsSupported)
            {
                AnsiConsole.Write(new Panel(
                        "Environment Provider работает с User/Machine environment только в Windows.\n" +
                        "На текущей платформе доступны Profile Engine, plan-модели и unit-тесты, но apply отключён.")
                    .Header(new PanelHeader(" WINDOWS REQUIRED "))
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Yellow)));
                WaitForReturn();
                return;
            }

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<MenuEntry>()
                    .Title("[bold white]Выберите операцию[/]")
                    .PageSize(4)
                    .HighlightStyle(new Style(Color.Black, Color.Cyan1))
                    .UseConverter(item => $"{item.Title} [grey]— {item.Description}[/]")
                    .AddChoices(
                        new MenuEntry("plan", "План и применение", "Выбрать профиль, увидеть diff и применить"),
                        new MenuEntry("status", "Текущее состояние", "Количество User/Machine variables и PATH"),
                        new MenuEntry("rollback", "Rollback checkpoint", "Восстановить сохранённое состояние"),
                        new MenuEntry("back", "← Назад", "Вернуться в Control Center")));

            switch (selected.Id)
            {
                case "plan":
                    await PlanAndApplyEnvironmentAsync(cancellationToken);
                    break;
                case "status":
                    ShowEnvironmentStatus(status);
                    break;
                case "rollback":
                    await RollbackEnvironmentAsync(cancellationToken);
                    break;
                case "back":
                    return;
            }
        }
    }

    private static void ShowEnvironmentStatus(EnvironmentStatusReport status)
    {
        RenderHeader("ENVIRONMENT STATUS");
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Scope")
            .AddColumn("Variables")
            .AddColumn("PATH entries");
        table.AddRow("User", status.UserVariables.ToString(), status.UserPathEntries.ToString());
        table.AddRow("Machine", status.MachineVariables.ToString(), status.MachinePathEntries.ToString());
        AnsiConsole.Write(table);
        foreach (var diagnostic in status.Diagnostics)
        {
            AnsiConsole.MarkupLine(
                $"[{(diagnostic.IsWarning ? "yellow" : "grey")}]{Markup.Escape(diagnostic.Message)}[/]");
        }

        WaitForReturn();
    }

    private async Task PlanAndApplyEnvironmentAsync(CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync("Выберите профиль для Environment plan", cancellationToken);
        if (profile is null)
        {
            return;
        }

        EnvironmentPlanReport? plan = null;
        Exception? failure = null;
        RenderHeader("ENVIRONMENT PLAN");
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Discovery → diff → risk analysis...[/]", async _ =>
            {
                try
                {
                    plan = await _application.PlanEnvironmentAsync(
                        profile.Path,
                        null,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        if (failure is not null || plan is null)
        {
            ShowError(failure?.Message ?? "Environment plan не был создан.");
            return;
        }

        RenderEnvironmentPlan(plan);
        if (!plan.Validation.IsValid || !plan.IsSupported || plan.Actions.Count == 0)
        {
            WaitForReturn();
            return;
        }

        var proceed = AnsiConsole.Confirm(
            "\n[bold yellow]Применить показанный plan с checkpoint и verification?[/]",
            false);
        if (!proceed)
        {
            AnsiConsole.MarkupLine("[grey]Применение отменено. Система не изменена.[/]");
            WaitForReturn();
            return;
        }

        var hasMachineActions = plan.Actions.Any(action => action.RequiresAdministrator);
        if (hasMachineActions && !AnsiConsole.Confirm(
            "[bold red]Plan содержит Machine scope. Подтвердить elevated-операции?[/]",
            false))
        {
            AnsiConsole.MarkupLine("[grey]Machine scope не подтверждён. Система не изменена.[/]");
            WaitForReturn();
            return;
        }

        EnvironmentExecutionReport? execution = null;
        failure = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.BouncingBar)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Checkpoint → apply → verify...[/]", async _ =>
            {
                try
                {
                    execution = await _application.ApplyEnvironmentAsync(
                        profile.Path,
                        null,
                        hasMachineActions,
                        true,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderHeader("ENVIRONMENT RESULT");
        if (failure is not null || execution is null)
        {
            ShowError(failure?.Message ?? "Environment workflow не вернул результат.");
            return;
        }

        RenderExecution(execution);
        WaitForReturn();
    }

    private async Task RollbackEnvironmentAsync(CancellationToken cancellationToken)
    {
        var checkpoints = await _application.ListEnvironmentCheckpointsAsync(cancellationToken);
        RenderHeader("ROLLBACK CENTER");
        if (checkpoints.Count == 0)
        {
            AnsiConsole.Write(new Panel("Checkpoint отсутствуют.")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Grey)));
            WaitForReturn();
            return;
        }

        var back = new EnvironmentCheckpointEntry(
            string.Empty,
            "← Назад",
            DateTimeOffset.MinValue,
            string.Empty,
            0,
            string.Empty);
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<EnvironmentCheckpointEntry>()
                .Title("[bold white]Выберите checkpoint[/]")
                .PageSize(Math.Min(12, checkpoints.Count + 1))
                .HighlightStyle(new Style(Color.Black, Color.Cyan1))
                .UseConverter(item => string.IsNullOrEmpty(item.ManifestPath)
                    ? item.ProfileName
                    : $"{item.CreatedAt:yyyy-MM-dd HH:mm} · {item.ProfileName} · {item.Status} · {item.ActionCount} actions")
                .AddChoices(checkpoints.Concat([back])));
        if (string.IsNullOrEmpty(selected.ManifestPath))
        {
            return;
        }

        if (!AnsiConsole.Confirm(
            $"[bold red]Восстановить checkpoint {Markup.Escape(selected.TransactionId)}?[/]",
            false))
        {
            return;
        }

        EnvironmentExecutionReport? result = null;
        Exception? failure = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.BouncingBar)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync("[yellow]Восстановление переменных и PATH...[/]", async _ =>
            {
                try
                {
                    result = await _application.RollbackEnvironmentAsync(
                        selected.ManifestPath,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderHeader("ROLLBACK RESULT");
        if (failure is not null || result is null)
        {
            ShowError(failure?.Message ?? "Rollback не вернул результат.");
            return;
        }

        RenderExecution(result);
        WaitForReturn();
    }

    private async Task<ProfileCatalogEntry?> SelectProfileAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var profiles = await _application.ListProfilesAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            AnsiConsole.Write(new Panel(
                    $"Каталог профилей пуст: [cyan1]{Markup.Escape(_application.Options.ProfilesDirectory)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Yellow)));
            WaitForReturn();
            return null;
        }

        var back = new ProfileCatalogEntry("← Назад", string.Empty, 0, DateTimeOffset.MinValue);
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileCatalogEntry>()
                .Title($"[bold white]{Markup.Escape(title)}[/]")
                .PageSize(Math.Min(12, profiles.Count + 1))
                .HighlightStyle(new Style(Color.Black, Color.Cyan1))
                .UseConverter(item => string.IsNullOrEmpty(item.Path)
                    ? item.Name
                    : $"{item.Name} [grey]· {item.SizeBytes} bytes[/]")
                .AddChoices(profiles.Concat([back])));
        return string.IsNullOrEmpty(selected.Path) ? null : selected;
    }

    private static void RenderEnvironmentPlan(EnvironmentPlanReport plan)
    {
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Metric")
            .AddColumn("Value");
        summary.AddRow("Profile", Markup.Escape(plan.Loaded.Profile.Metadata.Name));
        summary.AddRow("Changes", plan.Summary.Changes.ToString());
        summary.AddRow("Machine actions", plan.Summary.AdministratorActions.ToString());
        summary.AddRow("Destructive", plan.Summary.Destructive.ToString());
        summary.AddRow("Max risk", Markup.Escape(plan.Summary.MaximumRisk.ToString()));
        AnsiConsole.Write(summary);

        if (!plan.Validation.IsValid)
        {
            var issues = new Table().Border(TableBorder.Simple).AddColumn("Path").AddColumn("Проблема");
            foreach (var issue in plan.Validation.Issues)
            {
                issues.AddRow(Markup.Escape(issue.Path), Markup.Escape(issue.Message));
            }

            AnsiConsole.Write(issues);
            return;
        }

        var actions = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Risk")
            .AddColumn("Scope")
            .AddColumn("Operation")
            .AddColumn("Resource")
            .AddColumn("Explanation");
        foreach (var action in plan.Actions)
        {
            var scope = ActionProperty(action, "scope");
            var resource = action.Resource.ResourceType.EndsWith("variable", StringComparison.Ordinal)
                ? ActionProperty(action, "name")
                : ActionProperty(action, "path");
            var risk = action.Risk.ToString();
            var riskMarkup = action.Risk >= WinState.Domain.Configuration.RiskLevel.Medium
                ? $"[yellow]{Markup.Escape(risk)}[/]"
                : $"[green]{Markup.Escape(risk)}[/]";
            actions.AddRow(
                riskMarkup,
                Markup.Escape(scope),
                Markup.Escape(action.Operation.ToString()),
                Markup.Escape(resource),
                Markup.Escape(action.Explanation));
        }

        AnsiConsole.Write(actions);
        if (plan.Actions.Count == 0)
        {
            AnsiConsole.MarkupLine("\n[green]Система уже соответствует environment-секции профиля.[/]");
        }
    }

    private static void RenderExecution(EnvironmentExecutionReport result)
    {
        var style = result.Succeeded ? Color.Green : result.RolledBack ? Color.Yellow : Color.Red;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(style))
            .AddColumn("Параметр")
            .AddColumn("Значение");
        table.AddRow("Transaction", Markup.Escape(result.TransactionId));
        table.AddRow("Profile", Markup.Escape(result.ProfileName));
        table.AddRow("Succeeded", result.Succeeded ? "[green]YES[/]" : "[red]NO[/]");
        table.AddRow("Verified", result.Verified ? "[green]YES[/]" : "[red]NO[/]");
        table.AddRow("Rolled back", result.RolledBack ? "[yellow]YES[/]" : "NO");
        table.AddRow("Checkpoint", Markup.Escape(result.CheckpointManifest ?? "none"));
        AnsiConsole.Write(table);

        var actions = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Action")
            .AddColumn("Status")
            .AddColumn("Message");
        foreach (var action in result.Actions)
        {
            actions.AddRow(
                Markup.Escape(action.ActionId),
                Markup.Escape(action.Status.ToString()),
                Markup.Escape(action.Message));
        }

        if (result.Actions.Count > 0)
        {
            AnsiConsole.Write(actions);
        }

        AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(result.Message)}[/]");
    }

    private static string ActionProperty(PlannedAction action, string name)
        => action.Resource.Properties.TryGetValue(name, out var value)
            ? value.Value ?? string.Empty
            : string.Empty;

    private static void ShowError(string message)
    {
        AnsiConsole.Write(new Panel(Markup.Escape(message))
            .Header(new PanelHeader(" OPERATION FAILED "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Red)));
        WaitForReturn();
    }

    private async Task ShowDoctorAsync(CancellationToken cancellationToken)
    {
        DoctorReport report = default!;
        RenderHeader("DOCTOR");
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Проверка модулей и локального состояния...[/]", async _ =>
            {
                report = await _application.RunDoctorAsync(cancellationToken);
            });

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Статус")
            .AddColumn("Проверка")
            .AddColumn("Результат");
        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                DiagnosticStatus.Ok => "[green]OK[/]",
                DiagnosticStatus.Warning => "[yellow]WARN[/]",
                _ => "[red]FAIL[/]"
            };
            table.AddRow(marker, Markup.Escape(check.Name), Markup.Escape(check.Message));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(report.IsHealthy
            ? "\n[green]Система готова к работе.[/]"
            : "\n[red]Обнаружены критические проблемы.[/]");
        WaitForReturn();
    }

    private async Task ShowStorageAsync(CancellationToken cancellationToken)
    {
        RenderHeader("STORAGE CENTER");
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Проверка миграций SQLite...[/]", async _ =>
            {
                await _application.InitializeStorageAsync(cancellationToken);
            });

        var status = await _application.GetStorageStatusAsync(cancellationToken);
        var tables = await _application.GetStorageTablesAsync(cancellationToken);
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Параметр")
            .AddColumn("Значение");
        table.AddRow("Database", Markup.Escape(status.DatabasePath));
        table.AddRow("Schema", status.LatestMigrationVersion.ToString());
        table.AddRow("Migrations", status.AppliedMigrations.ToString());
        table.AddRow("Size", $"{status.DatabaseSizeBytes} bytes");
        table.AddRow("Tables", tables.Count.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.Write(new Panel(string.Join("  ·  ", tables.Select(Markup.Escape)))
            .Header(new PanelHeader(" TABLES "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey)));
        WaitForReturn();
    }

    private void ShowConfiguration()
    {
        RenderHeader("CONFIGURATION");
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .AddColumn("Ключ")
            .AddColumn("Значение");
        table.AddRow("Home", Markup.Escape(_application.Options.HomeDirectory));
        table.AddRow("Profiles", Markup.Escape(_application.Options.ProfilesDirectory));
        table.AddRow("Database", Markup.Escape(_application.Options.DatabasePath));
        table.AddRow("Logs", Markup.Escape(_application.Options.LogsDirectory));
        table.AddRow("Config", Markup.Escape(_application.Options.ConfigPath));
        table.AddRow("Portable", _application.Options.PortableMode ? "[yellow]true[/]" : "[green]false[/]");
        table.AddRow("Log level", Markup.Escape(_application.Options.MinimumLogLevel.ToString()));
        AnsiConsole.Write(table);
        WaitForReturn();
    }

    private static void ShowRoadmap()
    {
        RenderHeader("ARCHITECTURE");
        AnsiConsole.Write(new Panel(
                "[cyan1]Terminal UI[/] → [white]Application workflows[/] → [white]Core engines[/]\n" +
                "                     ├─ Environment Provider\n" +
                "                     ├─ Infrastructure\n" +
                "                     ├─ SQLite Storage\n" +
                "                     └─ Domain contracts")
            .Header(new PanelHeader(" MODULE MAP "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1)));
        AnsiConsole.Write(new Panel(
                "[green]Готово:[/] Control Center, Profile Engine и Environment Provider с checkpoint/apply/verify/rollback.\n" +
                "[yellow]Следующий этап:[/] Apply Engine: общий transaction pipeline, resume и cross-provider orchestration.")
            .Header(new PanelHeader(" ROADMAP "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Yellow)));
        WaitForReturn();
    }

    private static void RenderHeader(string section)
    {
        AnsiConsole.Clear();
        DrawLogo();
        var header = new Table()
            .Border(TableBorder.None)
            .Expand()
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn(string.Empty).RightAligned());
        header.AddRow(
            $"[bold cyan1]{Markup.Escape(section)}[/]",
            $"[grey]v{WinStateApplication.Version} · ↑↓ navigation · ENTER select · safeguards enabled[/]");
        AnsiConsole.Write(header);
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.Cyan1)));
    }

    private static void DrawLogo()
    {
        var logo = new FigletText("WINSTATE")
            .Centered()
            .Color(Color.Cyan1);
        AnsiConsole.Write(logo);
        AnsiConsole.MarkupLine("[grey]                Git for your Windows configuration[/]");
    }

    private static void WaitForReturn()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[grey]Нажмите любую клавишу, чтобы вернуться...[/]");
        _ = Console.ReadKey(intercept: true);
    }

    private sealed record MenuEntry(string Id, string Title, string Description);

    private sealed record StorageStatusSnapshot(
        string Path,
        int Migrations,
        int SchemaVersion,
        long SizeBytes);
}
