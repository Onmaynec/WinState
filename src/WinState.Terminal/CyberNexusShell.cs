using Spectre.Console;
using WinState.Apply;
using WinState.App;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Transactions;
using WinState.Update;

namespace WinState.Terminal;

/// <summary>
/// Верхний cyber-shell WinState 0.6: Control Center, Transaction Matrix и Update Uplink.
/// </summary>
public sealed class CyberNexusShell
{
    private static readonly IReadOnlyList<NexusChannel> Channels =
    [
        new("control", "[01] CYBER CONTROL CENTER", "Основные operation channels WinState"),
        new("transactions", "[02] TRANSACTION MATRIX", "Unified Apply Engine, resume и rollback"),
        new("updates", "[03] UPDATE UPLINK", "GitHub Releases, SHA-256 и self-update"),
        new("exit", "[00] DISCONNECT", "Завершить защищённую сессию")
    ];

    private readonly WinStateApplication _application;
    private readonly UpdateService _updates;

    public CyberNexusShell(WinStateApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _updates = new UpdateService();
    }

    public async Task<int> RunAsync(bool demoMode, CancellationToken cancellationToken)
    {
        Console.Title = $"WinState Nexus {WinStateApplication.Version}";
        if (demoMode)
        {
            await RenderDemoAsync(cancellationToken);
            _updates.Dispose();
            return 0;
        }

        try
        {
            await BootAsync(cancellationToken);
            if (await AutomaticUpdateHandshakeAsync(cancellationToken))
            {
                return 0;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                RenderNexusHeader("NEXUS CONTROL FABRIC");
                RenderNexusTelemetry();
                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<NexusChannel>()
                        .Title("[bold green]SELECT SECURE CHANNEL[/]")
                        .PageSize(Channels.Count)
                        .HighlightStyle(new Style(Color.Black, Color.Green1))
                        .UseConverter(channel =>
                            $"{channel.Title} [grey]// {channel.Description}[/]")
                        .AddChoices(Channels));

                switch (selected.Id)
                {
                    case "control":
                        await new CyberTerminalShell(_application)
                            .RunAsync(false, cancellationToken);
                        break;
                    case "transactions":
                        await ShowTransactionMatrixAsync(cancellationToken);
                        break;
                    case "updates":
                        if (await ShowUpdateUplinkAsync(cancellationToken))
                        {
                            return 0;
                        }
                        break;
                    case "exit":
                        await ShutdownAsync(cancellationToken);
                        return 0;
                }
            }

            return 130;
        }
        finally
        {
            _updates.Dispose();
        }
    }

    private async Task BootAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear();
        DrawLogo();
        var stages = new[]
        {
            "mount cyber terminal",
            "load provider registry",
            "restore transaction graph",
            "arm rollback safeguards",
            "establish update uplink"
        };
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(Spinner.Known.Binary))
            .StartAsync(async context =>
            {
                var task = context.AddTask("[green]NEXUS BOOT[/]", maxValue: stages.Length * 10);
                foreach (var stage in stages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    task.Description = $"[green]{Markup.Escape(stage)}[/]";
                    for (var tick = 0; tick < 10; tick++)
                    {
                        await Task.Delay(20, cancellationToken);
                        task.Increment(1);
                    }
                }
            });
        await _application.InitializeStorageAsync(cancellationToken);
    }

    private async Task<bool> AutomaticUpdateHandshakeAsync(
        CancellationToken cancellationToken)
    {
        if (_updates.Settings.Mode == AutomaticUpdateMode.Off
            || !await _updates.ShouldCheckAsync(
                _application.Options.HomeDirectory,
                WinStateApplication.Version,
                cancellationToken))
        {
            return false;
        }

        UpdateCheckResult? check = null;
        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Binary)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync("[green]UPDATE UPLINK // checking release channel...[/]", async _ =>
                {
                    check = await _updates.CheckAsync(
                        WinStateApplication.Version,
                        cancellationToken);
                });
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or FormatException)
        {
            AnsiConsole.MarkupLine(
                $"[grey]UPDATE UPLINK OFFLINE // {Markup.Escape(exception.Message)}[/]");
            await Task.Delay(350, cancellationToken);
            return false;
        }

        if (check is null)
        {
            return false;
        }

        await _updates.SaveLedgerAsync(
            _application.Options.HomeDirectory,
            check,
            cancellationToken);
        if (!check.IsUpdateAvailable || check.Release is null)
        {
            return false;
        }

        RenderUpdateAvailable(check);
        if (_updates.Settings.Mode == AutomaticUpdateMode.Check)
        {
            WaitForReturn();
            return false;
        }

        var install = _updates.Settings.Mode == AutomaticUpdateMode.Install
            || AnsiConsole.Confirm(
                "[bold green]DOWNLOAD, VERIFY AND INSTALL NOW?[/]",
                false);
        if (!install)
        {
            return false;
        }

        return await DownloadAndScheduleAsync(check.Release, cancellationToken);
    }

    private async Task ShowTransactionMatrixAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderNexusHeader("TRANSACTION MATRIX");
            UnifiedApplyStatusReport? status = null;
            await RunStatusAsync(
                "index transaction manifests",
                async () => status = await _application.GetUnifiedApplyStatusAsync(cancellationToken));
            if (status is null)
            {
                ShowFailure("Apply Engine status не получен.");
                return;
            }

            RenderTransactionStatus(status);
            var operation = AnsiConsole.Prompt(
                new SelectionPrompt<NexusChannel>()
                    .Title("[bold green]SELECT MATRIX OPERATION[/]")
                    .HighlightStyle(new Style(Color.Black, Color.Green1))
                    .UseConverter(item => $"{item.Title} [grey]// {item.Description}[/]")
                    .AddChoices(
                        new NexusChannel("plan", "[11] BUILD EXECUTION GRAPH", "План из YAML без изменений"),
                        new NexusChannel("apply", "[12] EXECUTE VERIFIED GRAPH", "Checkpoint, apply, verify, rollback"),
                        new NexusChannel("resume", "[13] RESUME INTERRUPTED", "Продолжить незавершённую транзакцию"),
                        new NexusChannel("rollback", "[14] CROSS-PROVIDER ROLLBACK", "Откатить успешные actions в обратном порядке"),
                        new NexusChannel("back", "[00] RETURN", "Вернуться в Nexus")));

            switch (operation.Id)
            {
                case "plan":
                    await PlanUnifiedAsync(false, cancellationToken);
                    break;
                case "apply":
                    await PlanUnifiedAsync(true, cancellationToken);
                    break;
                case "resume":
                    await ResumeUnifiedAsync(status, cancellationToken);
                    break;
                case "rollback":
                    await RollbackUnifiedAsync(status, cancellationToken);
                    break;
                case "back":
                    return;
            }
        }
    }

    private async Task PlanUnifiedAsync(
        bool allowExecution,
        CancellationToken cancellationToken)
    {
        var profile = SelectProfile();
        if (profile is null)
        {
            return;
        }

        UnifiedApplyPlanReport? report = null;
        Exception? failure = null;
        await RunStatusAsync(
            "discover → diff → merge provider graphs → risk groups",
            async () =>
            {
                try
                {
                    report = await _application.PlanUnifiedApplyAsync(
                        profile,
                        null,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderNexusHeader("UNIFIED EXECUTION GRAPH");
        if (failure is not null || report is null)
        {
            ShowFailure(failure?.Message ?? "Execution graph не создан.");
            return;
        }

        RenderUnifiedPlan(report);
        if (!allowExecution
            || !report.Validation.IsValid
            || !report.IsSupported
            || report.Plan.OrderedActions.Count == 0)
        {
            WaitForReturn();
            return;
        }

        if (!AnsiConsole.Confirm(
            "[bold yellow]EXECUTE THIS GRAPH WITH CHECKPOINTS AND VERIFICATION?[/]",
            false))
        {
            return;
        }

        var options = new ApplyEngineOptions
        {
            AutomaticRollback = true,
            AllowAdministrator = !report.Plan.RequiresAdministrator
                || AnsiConsole.Confirm(
                    "[bold red]ELEVATED ACTIONS DETECTED. AUTHORIZE ADMIN GROUP?[/]",
                    false),
            AllowCritical = report.Plan.MaximumRisk < RiskLevel.Critical
                || AnsiConsole.Confirm(
                    "[bold red]CRITICAL RISK GROUP DETECTED. AUTHORIZE?[/]",
                    false),
            AllowIrreversible = !report.Plan.ContainsIrreversible
                || AnsiConsole.Confirm(
                    "[bold red]IRREVERSIBLE ACTIONS DETECTED. AUTHORIZE?[/]",
                    false),
            AllowReboot = false
        };
        if (report.Plan.RequiresAdministrator && !options.AllowAdministrator
            || report.Plan.MaximumRisk >= RiskLevel.Critical && !options.AllowCritical
            || report.Plan.ContainsIrreversible && !options.AllowIrreversible)
        {
            AnsiConsole.MarkupLine("[yellow]AUTHORIZATION DENIED // graph not executed.[/]");
            WaitForReturn();
            return;
        }

        ApplyEngineReport? execution = null;
        failure = null;
        await RunStatusAsync(
            "prepare all checkpoints → execute graph → verify → seal manifest",
            async () =>
            {
                try
                {
                    execution = await _application.ApplyUnifiedAsync(
                        profile,
                        null,
                        options,
                        options.AllowAdministrator,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderNexusHeader("TRANSACTION TRACE");
        if (failure is not null || execution is null)
        {
            ShowFailure(failure?.Message ?? "Apply Engine не вернул результат.");
            return;
        }

        RenderApplyReport(execution);
        WaitForReturn();
    }

    private async Task ResumeUnifiedAsync(
        UnifiedApplyStatusReport status,
        CancellationToken cancellationToken)
    {
        var transaction = SelectTransaction(
            status.RecentTransactions.Where(IsResumable),
            "SELECT INTERRUPTED TRANSACTION");
        if (transaction is null)
        {
            return;
        }

        var path = ManifestPath(transaction.TransactionId);
        ApplyEngineReport? report = null;
        Exception? failure = null;
        await RunStatusAsync(
            "load persisted graph → skip verified actions → resume",
            async () =>
            {
                try
                {
                    report = await _application.ResumeUnifiedApplyAsync(path, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });
        RenderNexusHeader("RESUME TRACE");
        if (failure is not null || report is null)
        {
            ShowFailure(failure?.Message ?? "Resume не вернул результат.");
            return;
        }

        RenderApplyReport(report);
        WaitForReturn();
    }

    private async Task RollbackUnifiedAsync(
        UnifiedApplyStatusReport status,
        CancellationToken cancellationToken)
    {
        var transaction = SelectTransaction(
            status.RecentTransactions.Where(item => item.Results.Any(result =>
                result.Status == ActionStatus.Succeeded)),
            "SELECT TRANSACTION FOR ROLLBACK");
        if (transaction is null
            || !AnsiConsole.Confirm(
                $"[bold red]ROLLBACK {Markup.Escape(transaction.TransactionId)}?[/]",
                false))
        {
            return;
        }

        ApplyEngineReport? report = null;
        Exception? failure = null;
        await RunStatusAsync(
            "reverse graph → restore provider checkpoints → verify history",
            async () =>
            {
                try
                {
                    report = await _application.RollbackUnifiedApplyAsync(
                        ManifestPath(transaction.TransactionId),
                        cancellationToken);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });
        RenderNexusHeader("ROLLBACK TRACE");
        if (failure is not null || report is null)
        {
            ShowFailure(failure?.Message ?? "Rollback не вернул результат.");
            return;
        }

        RenderApplyReport(report);
        WaitForReturn();
    }

    private async Task<bool> ShowUpdateUplinkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderNexusHeader("UPDATE UPLINK");
            var settings = _updates.Settings;
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Green))
                .AddColumn("LINK")
                .AddColumn("VALUE");
            table.AddRow("Repository", Markup.Escape(settings.Repository));
            table.AddRow("Channel", settings.Channel.ToString());
            table.AddRow("Mode", settings.Mode.ToString());
            table.AddRow("Runtime", Markup.Escape(settings.RuntimeIdentifier));
            table.AddRow("Current", WinStateApplication.Version);
            table.AddRow("Self install", _updates.CanSelfInstall ? "[green]ARMED[/]" : "[yellow]SOURCE MODE[/]");
            AnsiConsole.Write(table);

            var operation = AnsiConsole.Prompt(
                new SelectionPrompt<NexusChannel>()
                    .Title("[bold green]SELECT UPLINK OPERATION[/]")
                    .HighlightStyle(new Style(Color.Black, Color.Green1))
                    .UseConverter(item => $"{item.Title} [grey]// {item.Description}[/]")
                    .AddChoices(
                        new NexusChannel("check", "[21] CHECK RELEASE CHANNEL", "Проверить актуальную версию"),
                        new NexusChannel("back", "[00] RETURN", "Вернуться в Nexus")));
            if (operation.Id == "back")
            {
                return false;
            }

            UpdateCheckResult? check = null;
            Exception? failure = null;
            await RunStatusAsync(
                "TLS handshake → GitHub Releases → semantic version compare",
                async () =>
                {
                    try
                    {
                        check = await _updates.CheckAsync(
                            WinStateApplication.Version,
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                        or TaskCanceledException
                        or InvalidDataException
                        or FormatException)
                    {
                        failure = exception;
                    }
                });
            RenderNexusHeader("UPDATE UPLINK RESULT");
            if (failure is not null || check is null)
            {
                ShowFailure(failure?.Message ?? "Release channel не ответил.");
                return false;
            }

            await _updates.SaveLedgerAsync(
                _application.Options.HomeDirectory,
                check,
                cancellationToken);
            if (!check.IsUpdateAvailable || check.Release is null)
            {
                AnsiConsole.Write(new Panel($"[green]{Markup.Escape(check.Message)}[/]")
                    .Header(new PanelHeader(" CHANNEL CURRENT "))
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Green)));
                WaitForReturn();
                return false;
            }

            RenderUpdateAvailable(check);
            if (!AnsiConsole.Confirm(
                "[bold green]DOWNLOAD AND VERIFY RELEASE PACKAGE?[/]",
                false))
            {
                return false;
            }

            return await DownloadAndScheduleAsync(check.Release, cancellationToken);
        }
    }

    private async Task<bool> DownloadAndScheduleAsync(
        ReleaseInfo release,
        CancellationToken cancellationToken)
    {
        UpdateDownloadResult? download = null;
        Exception? failure = null;
        await RunStatusAsync(
            "download package → verify SHA-256 → safe extract",
            async () =>
            {
                try
                {
                    download = await _updates.DownloadAndStageAsync(
                        release,
                        null,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException
                    or TaskCanceledException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderNexusHeader("UPDATE PACKAGE VERIFIED");
        if (failure is not null || download is null)
        {
            ShowFailure(failure?.Message ?? "Release package не подготовлен.");
            return false;
        }

        var panel = new Panel(
                $"[green]VERSION[/]  {Markup.Escape(download.Release.Version.ToString())}\n" +
                $"[green]SHA-256[/]  {Markup.Escape(download.Sha256)}\n" +
                $"[green]BYTES[/]    {download.BytesDownloaded}\n" +
                $"[green]STAGING[/]  {Markup.Escape(download.PayloadDirectory)}")
            .Header(new PanelHeader(" VERIFIED RELEASE "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Green));
        AnsiConsole.Write(panel);

        var install = await _updates.ScheduleInstallAsync(download, cancellationToken);
        if (!install.Scheduled)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(install.Message)}[/]");
            AnsiConsole.MarkupLine("[grey]Source checkout update: git pull[/]");
            WaitForReturn();
            return false;
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(install.Message)}[/]");
        AnsiConsole.MarkupLine("[grey]Current process will exit; verified updater will replace files and restart WinState.[/]");
        await Task.Delay(900, cancellationToken);
        return install.RequiresExit;
    }

    private async Task RenderDemoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenderNexusHeader("NEXUS CONTROL FABRIC // DEMO");
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(
            BuildDemoPanel(
                "TRANSACTION MATRIX",
                "[green]● ONLINE[/]\nexecution graph: READY\nresume ledger: ARMED\ncross-provider rollback: ARMED"),
            BuildDemoPanel(
                "UPDATE UPLINK",
                "[green]● ONLINE[/]\nchannel: prerelease\nSHA-256 gate: ARMED\nself-update: RELEASE ONLY"));
        grid.AddRow(
            BuildDemoPanel(
                "PROVIDER FABRIC",
                "environment: REGISTERED\nfuture adapters: HOT-PLUG\nrisk groups: ENFORCED"),
            BuildDemoPanel(
                "SAFETY POSTURE",
                "checkpoint before mutation\nverify before success\nautomatic rollback\nno silent elevation"));
        AnsiConsole.Write(grid);
        AnsiConsole.MarkupLine(
            "\n[green]CYBER NEXUS READY[/] [grey]// demo mode performs no network or system changes[/]");
        await Task.CompletedTask;
    }

    private void RenderNexusTelemetry()
    {
        var table = new Table()
            .Border(TableBorder.Heavy)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("NODE")
            .AddColumn("STATE")
            .AddColumn("SECURITY");
        table.AddRow("Apply Engine", "[green]ONLINE[/]", "graph + resume + rollback");
        table.AddRow(
            "Provider Fabric",
            $"[green]{_application.RegisteredApplyProviders.Count} REGISTERED[/]",
            string.Join(", ", _application.RegisteredApplyProviders.Select(Markup.Escape)));
        table.AddRow(
            "Update Uplink",
            _updates.Settings.Mode == AutomaticUpdateMode.Off
                ? "[grey]DISABLED[/]"
                : "[green]ARMED[/]",
            $"{_updates.Settings.Channel} / SHA-256");
        table.AddRow("Rollback", "[green]ARMED[/]", "checkpoint before mutation");
        AnsiConsole.Write(table);
    }

    private static void RenderTransactionStatus(UnifiedApplyStatusReport status)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("METRIC")
            .AddColumn("VALUE");
        table.AddRow("Registered providers", status.RegisteredProviders.Count.ToString());
        table.AddRow("Provider IDs", Markup.Escape(string.Join(", ", status.RegisteredProviders)));
        table.AddRow("Transactions", status.Transactions.ToString());
        table.AddRow("Resumable", status.ResumableTransactions.ToString());
        table.AddRow("Reboot pending", status.RebootPendingTransactions.ToString());
        AnsiConsole.Write(table);

        if (status.RecentTransactions.Count == 0)
        {
            return;
        }

        var history = new Table()
            .Border(TableBorder.SimpleHeavy)
            .AddColumn("TIME")
            .AddColumn("TRANSACTION")
            .AddColumn("STATUS")
            .AddColumn("ACTIONS");
        foreach (var transaction in status.RecentTransactions.Take(8))
        {
            history.AddRow(
                transaction.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(transaction.TransactionId),
                StatusMarkup(transaction.Status),
                transaction.Plan.Count.ToString());
        }

        AnsiConsole.Write(history);
    }

    private static void RenderUnifiedPlan(UnifiedApplyPlanReport report)
    {
        var summary = new Table()
            .Border(TableBorder.Heavy)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("GRAPH")
            .AddColumn("VALUE");
        summary.AddRow("Profile", Markup.Escape(report.Loaded.Profile.Metadata.Name));
        summary.AddRow("Providers", Markup.Escape(string.Join(", ", report.Plan.Providers)));
        summary.AddRow("Actions", report.Plan.OrderedActions.Count.ToString());
        summary.AddRow("Maximum risk", RiskMarkup(report.Plan.MaximumRisk));
        summary.AddRow("Elevated", report.Plan.RequiresAdministrator ? "[red]YES[/]" : "[green]NO[/]");
        summary.AddRow("Reboot possible", report.Plan.RequiresReboot ? "[yellow]YES[/]" : "NO");
        summary.AddRow("Irreversible", report.Plan.ContainsIrreversible ? "[red]YES[/]" : "[green]NO[/]");
        summary.AddRow("Validation", report.Validation.IsValid ? "[green]PASS[/]" : "[red]FAIL[/]");
        AnsiConsole.Write(summary);

        if (!report.Validation.IsValid)
        {
            foreach (var issue in report.Validation.Issues)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(issue.Path)}[/] // {Markup.Escape(issue.Message)}");
            }
            return;
        }

        var risk = new Table()
            .Border(TableBorder.SimpleHeavy)
            .AddColumn("RISK GROUP")
            .AddColumn("ACTIONS")
            .AddColumn("ADMIN")
            .AddColumn("NO ROLLBACK")
            .AddColumn("REBOOT");
        foreach (var group in report.Plan.RiskGroups)
        {
            risk.AddRow(
                RiskMarkup(group.Risk),
                group.Actions.ToString(),
                group.AdministratorActions.ToString(),
                group.IrreversibleActions.ToString(),
                group.RebootActions.ToString());
        }
        AnsiConsole.Write(risk);

        var actions = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("#")
            .AddColumn("PROVIDER")
            .AddColumn("OP")
            .AddColumn("RISK")
            .AddColumn("RESOURCE")
            .AddColumn("DEPENDENCIES");
        var index = 1;
        foreach (var action in report.Plan.OrderedActions)
        {
            actions.AddRow(
                index++.ToString("00"),
                Markup.Escape(action.ProviderId),
                Markup.Escape(action.Operation.ToString()),
                RiskMarkup(action.Risk),
                Markup.Escape(action.Resource.Identity),
                Markup.Escape(action.DependsOn.Count == 0
                    ? "-"
                    : string.Join(",", action.DependsOn)));
        }
        AnsiConsole.Write(actions);

        if (report.Plan.OrderedActions.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]NO DRIFT // execution graph is empty.[/]");
        }
    }

    private static void RenderApplyReport(ApplyEngineReport report)
    {
        var table = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(report.Succeeded ? Color.Green : Color.Red))
            .AddColumn("FIELD")
            .AddColumn("VALUE");
        table.AddRow("Transaction", Markup.Escape(report.TransactionId));
        table.AddRow("Profile", Markup.Escape(report.ProfileId));
        table.AddRow("Status", StatusMarkup(report.Status));
        table.AddRow("Verified", report.Verified ? "[green]YES[/]" : "[red]NO[/]");
        table.AddRow("Rolled back", report.RolledBack ? "[yellow]YES[/]" : "NO");
        table.AddRow("Reboot required", report.RebootRequired ? "[yellow]YES[/]" : "NO");
        table.AddRow("Manifest", Markup.Escape(report.ManifestPath));
        AnsiConsole.Write(table);

        var trace = new Table()
            .Border(TableBorder.SimpleHeavy)
            .AddColumn("TIME")
            .AddColumn("STATUS")
            .AddColumn("PROVIDER")
            .AddColumn("ACTION")
            .AddColumn("MESSAGE");
        foreach (var result in report.Results)
        {
            trace.AddRow(
                result.CompletedAt.ToLocalTime().ToString("HH:mm:ss.fff"),
                ActionStatusMarkup(result.Status),
                Markup.Escape(result.ProviderId),
                Markup.Escape(result.ActionId),
                Markup.Escape(result.Message));
        }
        AnsiConsole.Write(trace);
        AnsiConsole.MarkupLine($"\n[bold]{Markup.Escape(report.Message)}[/]");
    }

    private static void RenderUpdateAvailable(UpdateCheckResult check)
    {
        var release = check.Release!;
        AnsiConsole.Write(new Panel(
                $"[green]CURRENT[/]  {Markup.Escape(check.CurrentVersion)}\n" +
                $"[bold green]LATEST[/]   {Markup.Escape(release.Version.ToString())}\n" +
                $"[green]CHANNEL[/]  {(release.IsPrerelease ? "prerelease" : "stable")}\n" +
                $"[green]PUBLISHED[/] {release.PublishedAt:yyyy-MM-dd HH:mm} UTC\n" +
                $"[green]ASSETS[/]    {release.Assets.Count}")
            .Header(new PanelHeader(" UPDATE AVAILABLE "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Green)));
    }

    private string? SelectProfile()
    {
        var files = DiscoverProfiles();
        if (files.Count == 0)
        {
            ShowFailure("YAML profiles не найдены в Profile Vault или samples.");
            return null;
        }

        const string back = "← RETURN";
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold green]SELECT PROFILE PAYLOAD[/]")
                .PageSize(Math.Min(14, files.Count + 1))
                .HighlightStyle(new Style(Color.Black, Color.Green1))
                .AddChoices(files.Concat([back])));
        return selected == back ? null : selected;
    }

    private IReadOnlyList<string> DiscoverProfiles()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _application.Options.ProfilesDirectory,
            Path.Combine(Environment.CurrentDirectory, "samples")
        };
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(Path.GetFullPath(file));
            }
        }

        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ApplyTransactionManifest? SelectTransaction(
        IEnumerable<ApplyTransactionManifest> source,
        string title)
    {
        var transactions = source.ToArray();
        if (transactions.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]NO MATCHING TRANSACTIONS[/]");
            WaitForReturn();
            return null;
        }

        var back = new ApplyTransactionManifest { TransactionId = "← RETURN" };
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ApplyTransactionManifest>()
                .Title($"[bold green]{Markup.Escape(title)}[/]")
                .PageSize(Math.Min(12, transactions.Length + 1))
                .HighlightStyle(new Style(Color.Black, Color.Green1))
                .UseConverter(item => item.TransactionId == "← RETURN"
                    ? item.TransactionId
                    : $"{item.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} // {item.TransactionId} // {item.Status}")
                .AddChoices(transactions.Concat([back])));
        return selected.TransactionId == "← RETURN" ? null : selected;
    }

    private string ManifestPath(string transactionId)
        => Path.Combine(
            _application.Options.HomeDirectory,
            "backups",
            "transactions",
            transactionId,
            "transaction.json");

    private static bool IsResumable(ApplyTransactionManifest transaction)
        => transaction.Status is TransactionStatus.Planned
            or TransactionStatus.Running
            or TransactionStatus.Partial
            or TransactionStatus.Failed
            or TransactionStatus.VerificationFailed
            or TransactionStatus.Cancelled;

    private static async Task RunStatusAsync(string operation, Func<Task> work)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Binary)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync($"[green]{Markup.Escape(operation)}[/]", async context =>
            {
                context.Status($"[green]HANDSHAKE[/] // {Markup.Escape(operation)}");
                await Task.Delay(80);
                context.Status($"[green]EXECUTE[/] // {Markup.Escape(operation)}");
                await work();
                context.Status("[green]SEAL RESULT[/]");
                await Task.Delay(60);
            });
    }

    private static Panel BuildDemoPanel(string title, string content)
        => new(content)
        {
            Header = new PanelHeader($" {title} "),
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Green),
            Expand = true
        };

    private static void RenderNexusHeader(string channel)
    {
        AnsiConsole.Clear();
        DrawLogo();
        var header = new Table()
            .Border(TableBorder.None)
            .Expand()
            .AddColumn(string.Empty)
            .AddColumn(new TableColumn(string.Empty).RightAligned());
        header.AddRow(
            $"[bold green]{Markup.Escape(channel)}[/]",
            $"[grey]v{WinStateApplication.Version} // GRAPH LOCK // SHA-256 GATE // AUTO ROLLBACK[/]");
        AnsiConsole.Write(header);
        AnsiConsole.Write(new Rule("[green]SECURE CONTROL PLANE[/]")
            .RuleStyle(new Style(Color.Green)));
    }

    private static void DrawLogo()
    {
        AnsiConsole.Write(new FigletText("WINSTATE")
            .Color(Color.Green)
            .LeftJustified());
        AnsiConsole.MarkupLine(
            "[green]NEXUS/06[/] [grey]declarative Windows state fabric // verified execution only[/]");
    }

    private static async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        RenderNexusHeader("DISCONNECT");
        foreach (var line in new[]
        {
            "seal transaction ledger",
            "flush update uplink",
            "disarm provider handles",
            "close control plane"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.MarkupLine($"[green]PASS[/] {Markup.Escape(line)}");
            await Task.Delay(90, cancellationToken);
        }
        AnsiConsole.MarkupLine("[bold green]SESSION CLOSED[/]");
    }

    private static void ShowFailure(string message)
    {
        AnsiConsole.Write(new Panel(Markup.Escape(message))
            .Header(new PanelHeader(" OPERATION BLOCKED "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Red)));
        WaitForReturn();
    }

    private static string RiskMarkup(RiskLevel risk)
        => risk switch
        {
            RiskLevel.None => "[grey]NONE[/]",
            RiskLevel.Low => "[green]LOW[/]",
            RiskLevel.Medium => "[yellow]MEDIUM[/]",
            RiskLevel.High => "[red]HIGH[/]",
            RiskLevel.Critical => "[bold red]CRITICAL[/]",
            _ => Markup.Escape(risk.ToString())
        };

    private static string StatusMarkup(TransactionStatus status)
        => status switch
        {
            TransactionStatus.Succeeded => "[green]SUCCEEDED[/]",
            TransactionStatus.SucceededRebootPending => "[yellow]REBOOT PENDING[/]",
            TransactionStatus.RolledBack => "[yellow]ROLLED BACK[/]",
            TransactionStatus.Running => "[green]RUNNING[/]",
            TransactionStatus.Planned => "[grey]PLANNED[/]",
            _ => $"[red]{Markup.Escape(status.ToString().ToUpperInvariant())}[/]"
        };

    private static string ActionStatusMarkup(ActionStatus status)
        => status switch
        {
            ActionStatus.Succeeded => "[green]PASS[/]",
            ActionStatus.RolledBack => "[yellow]ROLLBACK[/]",
            ActionStatus.Pending => "[grey]PENDING[/]",
            ActionStatus.Running => "[green]RUNNING[/]",
            _ => $"[red]{Markup.Escape(status.ToString().ToUpperInvariant())}[/]"
        };

    private static void WaitForReturn()
    {
        AnsiConsole.MarkupLine("\n[grey]Press any key to return...[/]");
        _ = Console.ReadKey(true);
    }

    private sealed record NexusChannel(
        string Id,
        string Title,
        string Description);
}
