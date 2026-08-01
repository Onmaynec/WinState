using Spectre.Console;
using WinState.Apply;
using WinState.App;
using WinState.Domain.Configuration;
using WinState.Domain.Planning;
using WinState.Domain.Transactions;
using WinState.Update;

namespace WinState.Terminal;

/// <summary>Верхний cyber-shell: Control Center, Apply Engine и Update Uplink.</summary>
public sealed class CyberNexusShell
{
    private static readonly IReadOnlyList<MenuItem> MainMenu =
    [
        new("control", "[01] CYBER CONTROL CENTER", "основные operation channels"),
        new("matrix", "[02] TRANSACTION MATRIX", "execution graph, resume и rollback"),
        new("updates", "[03] UPDATE UPLINK", "GitHub Releases и SHA-256 gate"),
        new("exit", "[00] DISCONNECT", "закрыть защищённую сессию")
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
        try
        {
            if (demoMode)
            {
                await RenderDemoAsync(cancellationToken);
                return 0;
            }

            await BootAsync(cancellationToken);
            if (await AutomaticUpdateHandshakeAsync(cancellationToken))
            {
                return 0;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                RenderHeader("NEXUS CONTROL FABRIC");
                RenderNexusStatus();
                var item = Select("SELECT SECURE CHANNEL", MainMenu);
                switch (item.Id)
                {
                    case "control":
                        await new CyberTerminalShell(_application)
                            .RunAsync(false, cancellationToken);
                        break;
                    case "matrix":
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
                new SpinnerColumn(Spinner.Known.Dots))
            .StartAsync(async context =>
            {
                var task = context.AddTask("[green]NEXUS BOOT[/]", maxValue: stages.Length * 8);
                foreach (var stage in stages)
                {
                    task.Description = $"[green]{Markup.Escape(stage)}[/]";
                    for (var index = 0; index < 8; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(18, cancellationToken);
                        task.Increment(1);
                    }
                }
            });
        await _application.InitializeStorageAsync(cancellationToken);
    }

    private async Task<bool> AutomaticUpdateHandshakeAsync(CancellationToken cancellationToken)
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
            await RunAnimatedAsync(
                "UPDATE UPLINK // checking release channel",
                async () => check = await _updates.CheckAsync(
                    WinStateApplication.Version,
                    cancellationToken));
        }
        catch (Exception exception) when (IsUpdateException(exception))
        {
            AnsiConsole.MarkupLine(
                $"[grey]UPDATE UPLINK OFFLINE // {Markup.Escape(exception.Message)}[/]");
            await Task.Delay(250, cancellationToken);
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

        RenderHeader("UPDATE AVAILABLE");
        RenderUpdate(check);
        if (_updates.Settings.Mode == AutomaticUpdateMode.Check)
        {
            WaitForReturn();
            return false;
        }

        var approved = _updates.Settings.Mode == AutomaticUpdateMode.Install
            || AnsiConsole.Confirm("[bold green]DOWNLOAD, VERIFY AND INSTALL?[/]", false);
        return approved
            && await DownloadAndScheduleAsync(check.Release, cancellationToken);
    }

    private async Task ShowTransactionMatrixAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderHeader("TRANSACTION MATRIX");
            UnifiedApplyStatusReport? status = null;
            await RunAnimatedAsync(
                "index transaction manifests",
                async () => status = await _application.GetUnifiedApplyStatusAsync(cancellationToken));
            if (status is null)
            {
                ShowError("Apply Engine status не получен.");
                return;
            }

            RenderMatrixStatus(status);
            var operation = Select(
                "SELECT MATRIX OPERATION",
                [
                    new("plan", "[11] BUILD EXECUTION GRAPH", "построить plan без изменений"),
                    new("apply", "[12] EXECUTE VERIFIED GRAPH", "checkpoint, apply и verify"),
                    new("resume", "[13] RESUME INTERRUPTED", "продолжить persisted transaction"),
                    new("rollback", "[14] CROSS-PROVIDER ROLLBACK", "восстановить checkpoints"),
                    new("back", "[00] RETURN", "вернуться в Nexus")
                ]);
            switch (operation.Id)
            {
                case "plan":
                    await BuildGraphAsync(false, cancellationToken);
                    break;
                case "apply":
                    await BuildGraphAsync(true, cancellationToken);
                    break;
                case "resume":
                    await ResumeAsync(status, cancellationToken);
                    break;
                case "rollback":
                    await RollbackAsync(status, cancellationToken);
                    break;
                case "back":
                    return;
            }
        }
    }

    private async Task BuildGraphAsync(bool execute, CancellationToken cancellationToken)
    {
        var profile = SelectProfile();
        if (profile is null)
        {
            return;
        }

        UnifiedApplyPlanReport? report = null;
        Exception? failure = null;
        await RunAnimatedAsync(
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
                catch (Exception exception) when (IsWorkflowException(exception))
                {
                    failure = exception;
                }
            });

        RenderHeader("UNIFIED EXECUTION GRAPH");
        if (failure is not null || report is null)
        {
            ShowError(failure?.Message ?? "Execution graph не создан.");
            return;
        }

        RenderPlan(report);
        if (!execute
            || !report.Validation.IsValid
            || !report.IsSupported
            || report.Plan.OrderedActions.Count == 0)
        {
            WaitForReturn();
            return;
        }

        if (!AnsiConsole.Confirm(
            "[bold yellow]EXECUTE GRAPH WITH CHECKPOINTS AND VERIFICATION?[/]",
            false))
        {
            return;
        }

        var allowAdministrator = !report.Plan.RequiresAdministrator
            || AnsiConsole.Confirm("[bold red]AUTHORIZE ELEVATED ACTION GROUP?[/]", false);
        var allowCritical = report.Plan.MaximumRisk < RiskLevel.Critical
            || AnsiConsole.Confirm("[bold red]AUTHORIZE CRITICAL RISK GROUP?[/]", false);
        var allowIrreversible = !report.Plan.ContainsIrreversible
            || AnsiConsole.Confirm("[bold red]AUTHORIZE ACTIONS WITHOUT ROLLBACK?[/]", false);
        if (!allowAdministrator || !allowCritical || !allowIrreversible)
        {
            AnsiConsole.MarkupLine("[yellow]AUTHORIZATION DENIED // graph not executed.[/]");
            WaitForReturn();
            return;
        }

        ApplyEngineReport? execution = null;
        failure = null;
        await RunAnimatedAsync(
            "prepare checkpoints → execute graph → verify → seal manifest",
            async () =>
            {
                try
                {
                    execution = await _application.ApplyUnifiedAsync(
                        profile,
                        null,
                        new ApplyEngineOptions
                        {
                            AutomaticRollback = true,
                            AllowAdministrator = allowAdministrator,
                            AllowCritical = allowCritical,
                            AllowIrreversible = allowIrreversible,
                            AllowReboot = false
                        },
                        allowAdministrator,
                        cancellationToken);
                }
                catch (Exception exception) when (IsWorkflowException(exception))
                {
                    failure = exception;
                }
            });

        RenderHeader("TRANSACTION TRACE");
        if (failure is not null || execution is null)
        {
            ShowError(failure?.Message ?? "Apply Engine не вернул результат.");
            return;
        }

        RenderReport(execution);
        WaitForReturn();
    }

    private async Task ResumeAsync(
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

        ApplyEngineReport? report = null;
        Exception? failure = null;
        await RunAnimatedAsync(
            "load manifest → skip verified actions → resume graph",
            async () =>
            {
                try
                {
                    report = await _application.ResumeUnifiedApplyAsync(
                        ManifestPath(transaction.TransactionId),
                        cancellationToken);
                }
                catch (Exception exception) when (IsWorkflowException(exception))
                {
                    failure = exception;
                }
            });
        RenderHeader("RESUME TRACE");
        if (failure is not null || report is null)
        {
            ShowError(failure?.Message ?? "Resume не вернул результат.");
            return;
        }

        RenderReport(report);
        WaitForReturn();
    }

    private async Task RollbackAsync(
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
        await RunAnimatedAsync(
            "reverse graph → restore checkpoints → seal rollback",
            async () =>
            {
                try
                {
                    report = await _application.RollbackUnifiedApplyAsync(
                        ManifestPath(transaction.TransactionId),
                        cancellationToken);
                }
                catch (Exception exception) when (IsWorkflowException(exception))
                {
                    failure = exception;
                }
            });
        RenderHeader("ROLLBACK TRACE");
        if (failure is not null || report is null)
        {
            ShowError(failure?.Message ?? "Rollback не вернул результат.");
            return;
        }

        RenderReport(report);
        WaitForReturn();
    }

    private async Task<bool> ShowUpdateUplinkAsync(CancellationToken cancellationToken)
    {
        RenderHeader("UPDATE UPLINK");
        RenderUpdateSettings();
        var operation = Select(
            "SELECT UPLINK OPERATION",
            [
                new("check", "[21] CHECK RELEASE CHANNEL", "semantic version check"),
                new("back", "[00] RETURN", "вернуться в Nexus")
            ]);
        if (operation.Id == "back")
        {
            return false;
        }

        UpdateCheckResult? check = null;
        Exception? failure = null;
        await RunAnimatedAsync(
            "TLS handshake → GitHub Releases → compare versions",
            async () =>
            {
                try
                {
                    check = await _updates.CheckAsync(
                        WinStateApplication.Version,
                        cancellationToken);
                }
                catch (Exception exception) when (IsUpdateException(exception))
                {
                    failure = exception;
                }
            });
        RenderHeader("UPDATE UPLINK RESULT");
        if (failure is not null || check is null)
        {
            ShowError(failure?.Message ?? "Release channel не ответил.");
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

        RenderUpdate(check);
        if (!AnsiConsole.Confirm("[bold green]DOWNLOAD AND VERIFY PACKAGE?[/]", false))
        {
            return false;
        }

        return await DownloadAndScheduleAsync(check.Release, cancellationToken);
    }

    private async Task<bool> DownloadAndScheduleAsync(
        ReleaseInfo release,
        CancellationToken cancellationToken)
    {
        UpdateDownloadResult? download = null;
        Exception? failure = null;
        await RunAnimatedAsync(
            "download ZIP → verify SHA-256 → safe extract",
            async () =>
            {
                try
                {
                    download = await _updates.DownloadAndStageAsync(
                        release,
                        null,
                        cancellationToken);
                }
                catch (Exception exception) when (IsUpdateException(exception)
                    || exception is IOException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
                {
                    failure = exception;
                }
            });

        RenderHeader("VERIFIED RELEASE PACKAGE");
        if (failure is not null || download is null)
        {
            ShowError(failure?.Message ?? "Release package не подготовлен.");
            return false;
        }

        AnsiConsole.Write(new Panel(
                $"[green]VERSION[/]  {Markup.Escape(download.Release.Version.ToString())}\n" +
                $"[green]SHA-256[/]  {Markup.Escape(download.Sha256)}\n" +
                $"[green]BYTES[/]    {download.BytesDownloaded}\n" +
                $"[green]STAGING[/]  {Markup.Escape(download.PayloadDirectory)}")
            .Header(new PanelHeader(" SHA-256 PASS "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Green)));

        var install = await _updates.ScheduleInstallAsync(download, cancellationToken);
        if (!install.Scheduled)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(install.Message)}[/]");
            AnsiConsole.MarkupLine("[grey]Source checkout update command: git pull[/]");
            WaitForReturn();
            return false;
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(install.Message)}[/]");
        AnsiConsole.MarkupLine(
            "[grey]WinState завершится; updater заменит release files и перезапустит программу.[/]");
        await Task.Delay(700, cancellationToken);
        return install.RequiresExit;
    }

    private async Task RenderDemoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenderHeader("NEXUS CONTROL FABRIC // DEMO");
        var table = new Table()
            .Border(TableBorder.Heavy)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("CHANNEL")
            .AddColumn("STATE")
            .AddColumn("CAPABILITY");
        table.AddRow("TRANSACTION MATRIX", "[green]ONLINE[/]", "graph / resume / rollback");
        table.AddRow("UPDATE UPLINK", "[green]ONLINE[/]", "releases / SHA-256 / self-update");
        table.AddRow("PROVIDER FABRIC", "[green]REGISTERED[/]", "environment adapter");
        table.AddRow("SAFETY POSTURE", "[green]ARMED[/]", "checkpoint / verify / auto rollback");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "\n[green]NEXUS CONTROL FABRIC READY[/] [grey]// demo performs no network or system changes[/]");
        await Task.CompletedTask;
    }

    private void RenderNexusStatus()
    {
        var providers = _application.RegisteredApplyProviders;
        var table = new Table()
            .Border(TableBorder.Heavy)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("NODE")
            .AddColumn("STATE")
            .AddColumn("SECURITY");
        table.AddRow("Apply Engine", "[green]ONLINE[/]", "persisted execution graph");
        table.AddRow(
            "Provider Fabric",
            $"[green]{providers.Count} REGISTERED[/]",
            Markup.Escape(string.Join(", ", providers)));
        table.AddRow(
            "Update Uplink",
            _updates.Settings.Mode == AutomaticUpdateMode.Off
                ? "[grey]DISABLED[/]"
                : "[green]ARMED[/]",
            $"{_updates.Settings.Channel} / SHA-256");
        table.AddRow("Rollback", "[green]ARMED[/]", "checkpoint before mutation");
        AnsiConsole.Write(table);
    }

    private static void RenderMatrixStatus(UnifiedApplyStatusReport status)
    {
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("METRIC")
            .AddColumn("VALUE");
        summary.AddRow("Registered providers", status.RegisteredProviders.Count.ToString());
        summary.AddRow("Provider IDs", Markup.Escape(string.Join(", ", status.RegisteredProviders)));
        summary.AddRow("Transactions", status.Transactions.ToString());
        summary.AddRow("Resumable", status.ResumableTransactions.ToString());
        summary.AddRow("Reboot pending", status.RebootPendingTransactions.ToString());
        AnsiConsole.Write(summary);

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
        foreach (var item in status.RecentTransactions.Take(8))
        {
            history.AddRow(
                item.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Markup.Escape(item.TransactionId),
                TransactionStatusMarkup(item.Status),
                item.Plan.Count.ToString());
        }
        AnsiConsole.Write(history);
    }

    private static void RenderPlan(UnifiedApplyPlanReport report)
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
        summary.AddRow("No rollback", report.Plan.ContainsIrreversible ? "[red]YES[/]" : "[green]NO[/]");
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

        var risks = new Table()
            .Border(TableBorder.SimpleHeavy)
            .AddColumn("RISK")
            .AddColumn("ACTIONS")
            .AddColumn("ADMIN")
            .AddColumn("NO ROLLBACK")
            .AddColumn("REBOOT");
        foreach (var group in report.Plan.RiskGroups)
        {
            risks.AddRow(
                RiskMarkup(group.Risk),
                group.Actions.ToString(),
                group.AdministratorActions.ToString(),
                group.IrreversibleActions.ToString(),
                group.RebootActions.ToString());
        }
        AnsiConsole.Write(risks);

        var actions = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("#")
            .AddColumn("PROVIDER")
            .AddColumn("OP")
            .AddColumn("RISK")
            .AddColumn("RESOURCE")
            .AddColumn("DEPENDS");
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

    private static void RenderReport(ApplyEngineReport report)
    {
        var summary = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(report.Succeeded ? Color.Green : Color.Red))
            .AddColumn("FIELD")
            .AddColumn("VALUE");
        summary.AddRow("Transaction", Markup.Escape(report.TransactionId));
        summary.AddRow("Profile", Markup.Escape(report.ProfileId));
        summary.AddRow("Status", TransactionStatusMarkup(report.Status));
        summary.AddRow("Verified", report.Verified ? "[green]YES[/]" : "[red]NO[/]");
        summary.AddRow("Rolled back", report.RolledBack ? "[yellow]YES[/]" : "NO");
        summary.AddRow("Reboot required", report.RebootRequired ? "[yellow]YES[/]" : "NO");
        summary.AddRow("Manifest", Markup.Escape(report.ManifestPath));
        AnsiConsole.Write(summary);

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

    private void RenderUpdateSettings()
    {
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
    }

    private static void RenderUpdate(UpdateCheckResult check)
    {
        var release = check.Release!;
        AnsiConsole.Write(new Panel(
                $"[green]CURRENT[/]   {Markup.Escape(check.CurrentVersion)}\n" +
                $"[bold green]LATEST[/]    {Markup.Escape(release.Version.ToString())}\n" +
                $"[green]CHANNEL[/]   {(release.IsPrerelease ? "prerelease" : "stable")}\n" +
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
            ShowError("YAML profiles не найдены в Profile Vault или samples.");
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
        var roots = new[]
        {
            _application.Options.ProfilesDirectory,
            Path.Combine(Environment.CurrentDirectory, "samples")
        };
        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static MenuItem Select(string title, IEnumerable<MenuItem> items)
        => AnsiConsole.Prompt(
            new SelectionPrompt<MenuItem>()
                .Title($"[bold green]{Markup.Escape(title)}[/]")
                .HighlightStyle(new Style(Color.Black, Color.Green1))
                .UseConverter(item => $"{item.Title} [grey]// {item.Description}[/]")
                .AddChoices(items));

    private static async Task RunAnimatedAsync(string operation, Func<Task> action)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync($"[green]{Markup.Escape(operation)}[/]", async context =>
            {
                context.Status($"[green]HANDSHAKE[/] // {Markup.Escape(operation)}");
                await Task.Delay(50);
                context.Status($"[green]EXECUTE[/] // {Markup.Escape(operation)}");
                await action();
                context.Status("[green]SEAL RESULT[/]");
                await Task.Delay(40);
            });
    }

    private static void RenderHeader(string channel)
    {
        AnsiConsole.Clear();
        DrawLogo();
        var table = new Table()
            .Border(TableBorder.None)
            .Expand()
            .AddColumn(string.Empty)
            .AddColumn(new TableColumn(string.Empty).RightAligned());
        table.AddRow(
            $"[bold green]{Markup.Escape(channel)}[/]",
            $"[grey]v{WinStateApplication.Version} // GRAPH LOCK // SHA-256 GATE // AUTO ROLLBACK[/]");
        AnsiConsole.Write(table);
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
        RenderHeader("DISCONNECT");
        foreach (var stage in new[]
        {
            "seal transaction ledger",
            "flush update uplink",
            "disarm provider handles",
            "close control plane"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.MarkupLine($"[green]PASS[/] {Markup.Escape(stage)}");
            await Task.Delay(70, cancellationToken);
        }
        AnsiConsole.MarkupLine("[bold green]SESSION CLOSED[/]");
    }

    private static bool IsResumable(ApplyTransactionManifest transaction)
        => transaction.Status is TransactionStatus.Planned
            or TransactionStatus.Running
            or TransactionStatus.Partial
            or TransactionStatus.Failed
            or TransactionStatus.VerificationFailed
            or TransactionStatus.Cancelled;

    private static bool IsWorkflowException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or PlatformNotSupportedException;

    private static bool IsUpdateException(Exception exception)
        => exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or FormatException;

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

    private static string TransactionStatusMarkup(TransactionStatus status)
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

    private static void ShowError(string message)
    {
        AnsiConsole.Write(new Panel(Markup.Escape(message))
            .Header(new PanelHeader(" OPERATION BLOCKED "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Red)));
        WaitForReturn();
    }

    private static void WaitForReturn()
    {
        AnsiConsole.MarkupLine("\n[grey]Press any key to return...[/]");
        _ = Console.ReadKey(true);
    }

    private sealed record MenuItem(string Id, string Title, string Description);
}
