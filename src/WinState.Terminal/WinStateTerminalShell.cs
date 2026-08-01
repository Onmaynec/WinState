using System.Runtime.InteropServices;
using Spectre.Console;
using WinState.App;
using WinState.App.Diagnostics;
using WinState.Core.Profiles;

namespace WinState.Terminal;

public sealed class WinStateTerminalShell
{
    private static readonly IReadOnlyList<MenuEntry> MainMenu =
    [
        new("dashboard", "Обзор системы", "Платформа, каталоги, профили и локальное состояние"),
        new("profiles", "Центр профилей", "Поиск и проверка YAML-профилей"),
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
        table.AddRow("Каталог", Markup.Escape(_application.Options.HomeDirectory));
        AnsiConsole.Write(table);

        var statusPanel = new Panel(
            new Markup("[green]● ONLINE[/]  [grey]Profile Engine загружен · SQLite доступна · системные изменения отключены[/]"))
            .Header(new PanelHeader(" STATUS "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Green));
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
        (LoadedProfile Loaded, ProfileValidationResult Validation) result = default!;
        Exception? failure = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan1"))
            .StartAsync("[cyan1]Загрузка includes, variables и normalization...[/]", async _ =>
            {
                try
                {
                    result = await _application.ValidateProfileAsync(entry.Path, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failure = exception;
                }
            });

        RenderHeader("PROFILE REPORT");
        if (failure is not null)
        {
            AnsiConsole.Write(new Panel(Markup.Escape(failure.Message))
                .Header(new PanelHeader(" LOAD FAILED "))
                .BorderStyle(new Style(Color.Red))
                .Border(BoxBorder.Rounded));
            WaitForReturn();
            return;
        }

        var profile = result.Loaded.Profile;
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(result.Validation.IsValid ? Color.Green : Color.Yellow))
            .AddColumn("Параметр")
            .AddColumn("Значение");
        summary.AddRow("Имя", Markup.Escape(profile.Metadata.Name));
        summary.AddRow("Источники", result.Loaded.SourceFiles.Count.ToString());
        summary.AddRow("Переменные", result.Loaded.Variables.Count.ToString());
        summary.AddRow("User environment", profile.Environment.User.Count.ToString());
        summary.AddRow("Machine environment", profile.Environment.Machine.Count.ToString());
        summary.AddRow("PATH entries", (profile.Environment.UserPath.Count + profile.Environment.MachinePath.Count).ToString());
        summary.AddRow("Результат", result.Validation.IsValid ? "[green]VALID[/]" : "[yellow]ISSUES FOUND[/]");
        AnsiConsole.Write(summary);

        if (!result.Validation.IsValid)
        {
            var issues = new Table().Border(TableBorder.Simple).AddColumn("Path").AddColumn("Проблема");
            foreach (var issue in result.Validation.Issues)
            {
                issues.AddRow(Markup.Escape(issue.Path), Markup.Escape(issue.Message));
            }

            AnsiConsole.Write(issues);
        }

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
                "[cyan1]Terminal UI[/] → [white]Application scenarios[/] → [white]Core engines[/]\n" +
                "                     ├─ Infrastructure\n" +
                "                     ├─ SQLite Storage\n" +
                "                     └─ Domain contracts")
            .Header(new PanelHeader(" MODULE MAP "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1)));
        AnsiConsole.Write(new Panel(
                "[green]Готово:[/] интерактивная панель, Profile Engine, includes, extends, variables, normalization.\n" +
                "[yellow]Следующий этап:[/] Environment Provider vertical slice: discover → diff → plan → apply → verify → rollback.")
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
            $"[grey]v{WinStateApplication.Version} · ↑↓ navigation · ENTER select · ESC/back[/]");
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

        AnsiConsole.MarkupLine("\n[grey]Нажмите любую клавишу, чтобы вернуться в Control Center...[/]");
        _ = Console.ReadKey(intercept: true);
    }

    private sealed record MenuEntry(string Id, string Title, string Description);

    private sealed record StorageStatusSnapshot(
        string Path,
        int Migrations,
        int SchemaVersion,
        long SizeBytes);
}
