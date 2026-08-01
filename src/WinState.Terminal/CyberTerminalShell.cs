using System.Runtime.InteropServices;
using Spectre.Console;
using WinState.App;
using WinState.App.Diagnostics;
using WinState.Core.Profiles;
using WinState.Domain.Planning;

namespace WinState.Terminal;

/// <summary>
/// Cyber-oriented interactive frontend inspired by the dense control-node style of NexRoute.
/// The class contains presentation only; all system mutations still pass through WinStateApplication.
/// </summary>
public sealed class CyberTerminalShell
{
    private static readonly IReadOnlyList<CyberMenuEntry> MainMenu =
    [
        new("dashboard", "[01] CONTROL NODE", "Live system telemetry and module readiness"),
        new("profiles", "[02] PROFILE VAULT", "Inspect and validate YAML configuration profiles"),
        new("environment", "[03] ENVIRONMENT OPS", "Plan, checkpoint, apply, verify and rollback"),
        new("checkpoints", "[04] CHECKPOINT VAULT", "Browse and restore captured state"),
        new("doctor", "[05] DEEP SCAN", "Run animated diagnostics against every module"),
        new("storage", "[06] DATA CORE", "Inspect SQLite schema and transaction storage"),
        new("configuration", "[07] NODE CONFIG", "Show paths, mode and runtime settings"),
        new("roadmap", "[08] SYSTEM MAP", "Architecture, security model and next stage"),
        new("exit", "[00] DISCONNECT", "Close the WinState control node")
    ];

    private readonly WinStateApplication _application;
    private bool _fastMode;

    public CyberTerminalShell(WinStateApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public async Task<int> RunAsync(bool demoMode, CancellationToken cancellationToken)
    {
        _fastMode = demoMode;
        TrySetConsoleTitle($"WinState Cyber Control Center {WinStateApplication.Version}");

        if (demoMode)
        {
            await RenderDashboardAsync(false, cancellationToken);
            return 0;
        }

        await RunBootSequenceAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            RenderFrame("CONTROL NODE", "SECURE INTERACTIVE SESSION");
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<CyberMenuEntry>()
                    .Title("[grey]Select operation channel[/]")
                    .PageSize(MainMenu.Count)
                    .MoreChoicesText("[grey]Use arrows to move through channels[/]")
                    .HighlightStyle(new Style(Color.Black, Color.Green))
                    .UseConverter(item => $"[bold green]{item.Title}[/]  [grey]// {item.Description}[/]")
                    .AddChoices(MainMenu));

            try
            {
                switch (selected.Id)
                {
                    case "dashboard":
                        await RenderDashboardAsync(true, cancellationToken);
                        break;
                    case "profiles":
                        await ShowProfileVaultAsync(cancellationToken);
                        break;
                    case "environment":
                        await ShowEnvironmentOpsAsync(cancellationToken);
                        break;
                    case "checkpoints":
                        await ShowCheckpointVaultAsync(cancellationToken);
                        break;
                    case "doctor":
                        await ShowDeepScanAsync(cancellationToken);
                        break;
                    case "storage":
                        await ShowDataCoreAsync(cancellationToken);
                        break;
                    case "configuration":
                        ShowNodeConfiguration();
                        break;
                    case "roadmap":
                        ShowSystemMap();
                        break;
                    case "exit":
                        await ShutdownSequenceAsync(cancellationToken);
                        return 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or ArgumentException)
            {
                ShowFailure(exception.Message);
            }
        }

        return 130;
    }

    private async Task RunBootSequenceAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Clear();
        DrawCyberLogo();
        AnsiConsole.MarkupLine("[grey]boot://winstate/control-node[/]");
        AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.DarkGreen)));

        var bootLines = new[]
        {
            ("KERNEL", "loading domain contracts", "OK"),
            ("PROFILE", "mounting YAML profile engine", "OK"),
            ("STORAGE", "opening encrypted-style local state channel", "OK"),
            ("PROVIDER", OperatingSystem.IsWindows() ? "binding Windows environment provider" : "provider parked: Windows required", OperatingSystem.IsWindows() ? "OK" : "LIMITED"),
            ("SAFEGUARD", "arming plan/checkpoint/verify/rollback gates", "ARMED")
        };

        foreach (var line in bootLines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.MarkupLine(
                $"[grey]{DateTime.Now:HH:mm:ss.fff}[/] [green]›[/] [bold white]{line.Item1,-9}[/] " +
                $"[grey]{Markup.Escape(line.Item2),-48}[/] [bold green]{line.Item3}[/]");
            await DelayAsync(90, cancellationToken);
        }

        await RunAnimatedOperationAsync(
            "INITIALIZING DATA CORE",
            async () =>
            {
                await _application.InitializeStorageAsync(cancellationToken);
                return true;
            },
            cancellationToken);

        AnsiConsole.MarkupLine("\n[bold green]CONTROL NODE ONLINE[/] [grey]// press any key to establish session[/]");
        _ = Console.ReadKey(intercept: true);
    }

    private async Task RenderDashboardAsync(bool pause, CancellationToken cancellationToken)
    {
        RenderFrame("CONTROL NODE", "LIVE TELEMETRY");
        var telemetry = await CollectTelemetryAsync(cancellationToken);

        var systemTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand()
            .AddColumn(new TableColumn("[bold green]NODE[/]"))
            .AddColumn(new TableColumn("[bold white]VALUE[/]"));
        systemTable.AddRow("VERSION", Markup.Escape(WinStateApplication.Version));
        systemTable.AddRow("HOST", Markup.Escape(Environment.MachineName));
        systemTable.AddRow("PLATFORM", Markup.Escape($"{RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}"));
        systemTable.AddRow("PROCESS", Markup.Escape($"PID {Environment.ProcessId} / {RuntimeInformation.ProcessArchitecture}"));
        systemTable.AddRow("MODE", _application.Options.PortableMode ? "[yellow]PORTABLE[/]" : "[green]USER DATA[/]");
        systemTable.AddRow("UPTIME", Markup.Escape(FormatUptime(Environment.TickCount64)));

        var moduleTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn(new TableColumn("[bold green]MODULE[/]"))
            .AddColumn(new TableColumn("[bold white]STATE[/]"));
        moduleTable.AddRow("PROFILE ENGINE", "[green]ONLINE[/]");
        moduleTable.AddRow("DATA CORE", $"[green]ONLINE[/] [grey]schema {telemetry.Storage.LatestMigrationVersion}[/]");
        moduleTable.AddRow(
            "ENV PROVIDER",
            telemetry.Environment.IsSupported
                ? "[green]ARMED[/]"
                : "[yellow]WINDOWS ONLY[/]");
        moduleTable.AddRow("SAFEGUARDS", "[green]PLAN + CHECKPOINT + VERIFY[/]");
        moduleTable.AddRow("ROLLBACK", "[green]AUTO-ARMED[/]");
        moduleTable.AddRow("PROFILE VAULT", $"[white]{telemetry.Profiles.Count} files[/]");

        AnsiConsole.Write(new Columns(systemTable, moduleTable).Expand());

        var counters = new Table()
            .Border(TableBorder.Simple)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand()
            .AddColumn(new TableColumn("[grey]USER VARS[/]").Centered())
            .AddColumn(new TableColumn("[grey]MACHINE VARS[/]").Centered())
            .AddColumn(new TableColumn("[grey]USER PATH[/]").Centered())
            .AddColumn(new TableColumn("[grey]MACHINE PATH[/]").Centered())
            .AddColumn(new TableColumn("[grey]CHECKPOINTS[/]").Centered())
            .AddColumn(new TableColumn("[grey]DB SIZE[/]").Centered());
        counters.AddRow(
            $"[bold green]{telemetry.Environment.UserVariables}[/]",
            $"[bold green]{telemetry.Environment.MachineVariables}[/]",
            $"[bold green]{telemetry.Environment.UserPathEntries}[/]",
            $"[bold green]{telemetry.Environment.MachinePathEntries}[/]",
            $"[bold green]{telemetry.Checkpoints.Count}[/]",
            $"[bold green]{FormatBytes(telemetry.Storage.DatabaseSizeBytes)}[/]");
        AnsiConsole.Write(counters);

        var posture = telemetry.Environment.IsSupported
            ? "[green]● SECURE[/] [grey]System mutations are locked behind explicit plan, checkpoint and verification gates.[/]"
            : "[yellow]● LIMITED[/] [grey]Read-only cyber console active. Windows provider is unavailable on this host.[/]";
        AnsiConsole.Write(new Panel(new Markup(posture))
            .Header(new PanelHeader(" THREAT POSTURE "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(telemetry.Environment.IsSupported ? Color.Green : Color.Yellow))
            .Expand());

        RenderEventFeed(
        [
            ("PROFILE", $"{telemetry.Profiles.Count} profile(s) indexed"),
            ("STORAGE", $"{telemetry.Storage.AppliedMigrations} migration(s) active"),
            ("BACKUP", $"{telemetry.Checkpoints.Count} rollback checkpoint(s) available"),
            ("GUARD", "unmanaged resources remain untouched")
        ]);

        if (pause)
        {
            WaitForReturn();
        }
    }

    private async Task<CyberTelemetry> CollectTelemetryAsync(CancellationToken cancellationToken)
    {
        await _application.InitializeStorageAsync(cancellationToken);
        var storage = await _application.GetStorageStatusAsync(cancellationToken);
        var profiles = await DiscoverProfilesAsync(cancellationToken);
        var environment = await _application.GetEnvironmentStatusAsync(cancellationToken);
        var checkpoints = await _application.ListEnvironmentCheckpointsAsync(cancellationToken);
        return new CyberTelemetry(storage, profiles, environment, checkpoints);
    }

    private async Task ShowProfileVaultAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderFrame("PROFILE VAULT", "YAML INTELLIGENCE");
            var profiles = await DiscoverProfilesAsync(cancellationToken);
            if (profiles.Count == 0)
            {
                AnsiConsole.Write(new Panel(
                        $"[yellow]NO PROFILE SIGNALS DETECTED[/]\n\n" +
                        $"Vault: [green]{Markup.Escape(_application.Options.ProfilesDirectory)}[/]\n" +
                        "Place .yaml/.yml files in the vault or keep repository samples in ./samples.")
                    .Header(new PanelHeader(" EMPTY VAULT "))
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Yellow)));
                WaitForReturn();
                return;
            }

            var back = new ProfileCatalogEntry("[00] RETURN", string.Empty, 0, DateTimeOffset.MinValue);
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<ProfileCatalogEntry>()
                    .Title("[grey]Choose profile packet for analysis[/]")
                    .PageSize(Math.Min(15, profiles.Count + 1))
                    .HighlightStyle(new Style(Color.Black, Color.Green))
                    .UseConverter(item => string.IsNullOrEmpty(item.Path)
                        ? item.Name
                        : $"[green]›[/] {item.Name,-32} [grey]{FormatBytes(item.SizeBytes),8}  {item.ModifiedAt:yyyy-MM-dd HH:mm}[/]")
                    .AddChoices(profiles.Concat([back])));
            if (string.IsNullOrEmpty(selected.Path))
            {
                return;
            }

            await AnalyzeProfileAsync(selected, cancellationToken);
        }
    }

    private async Task AnalyzeProfileAsync(ProfileCatalogEntry entry, CancellationToken cancellationToken)
    {
        var result = await RunAnimatedOperationAsync(
            "DECRYPTING PROFILE GRAPH",
            () => _application.ValidateProfileAsync(entry.Path, cancellationToken),
            cancellationToken);

        RenderFrame("PROFILE REPORT", "STATIC ANALYSIS COMPLETE");
        var profile = result.Loaded.Profile;
        var table = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(result.Validation.IsValid ? Color.Green : Color.Yellow))
            .Expand()
            .AddColumn("[bold green]FIELD[/]")
            .AddColumn("[bold white]VALUE[/]");
        table.AddRow("NAME", Markup.Escape(profile.Metadata.Name));
        table.AddRow("PATH", Markup.Escape(entry.Path));
        table.AddRow("SOURCE LAYERS", result.Loaded.SourceFiles.Count.ToString());
        table.AddRow("RESOLVED VARIABLES", result.Loaded.Variables.Count.ToString());
        table.AddRow("USER ENV", profile.Environment.User.Count.ToString());
        table.AddRow("MACHINE ENV", profile.Environment.Machine.Count.ToString());
        table.AddRow("PATH DIRECTIVES", (profile.Environment.UserPath.Count + profile.Environment.MachinePath.Count).ToString());
        table.AddRow("SIGNATURE", result.Validation.IsValid ? "[green]VALID[/]" : "[yellow]ISSUES DETECTED[/]");
        AnsiConsole.Write(table);

        if (!result.Validation.IsValid)
        {
            var issues = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Yellow))
                .AddColumn("[yellow]LOCATION[/]")
                .AddColumn("[yellow]ANOMALY[/]");
            foreach (var issue in result.Validation.Issues)
            {
                issues.AddRow(Markup.Escape(issue.Path), Markup.Escape(issue.Message));
            }

            AnsiConsole.Write(issues);
        }

        RenderEventFeed(
        [
            ("PARSE", "YAML syntax accepted"),
            ("MERGE", $"{result.Loaded.SourceFiles.Count} source layer(s) resolved"),
            ("NORMALIZE", "environment and PATH directives normalized"),
            ("RESULT", result.Validation.IsValid ? "profile is ready for planning" : "profile requires operator attention")
        ]);
        WaitForReturn();
    }

    private async Task ShowEnvironmentOpsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderFrame("ENVIRONMENT OPS", "WINDOWS STATE CHANNEL");
            var status = await _application.GetEnvironmentStatusAsync(cancellationToken);
            if (!status.IsSupported)
            {
                AnsiConsole.Write(new Panel(
                        "[yellow]PROVIDER CHANNEL OFFLINE[/]\n\n" +
                        "Environment apply is intentionally limited to Windows. Profile analysis and CI-safe simulations remain available.")
                    .Header(new PanelHeader(" WINDOWS REQUIRED "))
                    .Border(BoxBorder.Double)
                    .BorderStyle(new Style(Color.Yellow)));
                WaitForReturn();
                return;
            }

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<CyberMenuEntry>()
                    .Title("[grey]Select environment operation[/]")
                    .PageSize(5)
                    .HighlightStyle(new Style(Color.Black, Color.Green))
                    .UseConverter(item => $"[bold green]{item.Title}[/]  [grey]// {item.Description}[/]")
                    .AddChoices(
                        new CyberMenuEntry("plan", "[01] BUILD EXECUTION PLAN", "discover, diff and calculate risk"),
                        new CyberMenuEntry("apply", "[02] PLAN + EXECUTE", "checkpoint, apply, verify and auto-rollback"),
                        new CyberMenuEntry("status", "[03] LIVE ENV TELEMETRY", "read User/Machine variables and PATH"),
                        new CyberMenuEntry("rollback", "[04] ROLLBACK CHANNEL", "restore a saved checkpoint"),
                        new CyberMenuEntry("back", "[00] RETURN", "disconnect from environment ops")));

            switch (selected.Id)
            {
                case "plan":
                    await PlanEnvironmentAsync(false, cancellationToken);
                    break;
                case "apply":
                    await PlanEnvironmentAsync(true, cancellationToken);
                    break;
                case "status":
                    ShowEnvironmentStatus(status);
                    break;
                case "rollback":
                    await ShowCheckpointVaultAsync(cancellationToken);
                    break;
                case "back":
                    return;
            }
        }
    }

    private async Task PlanEnvironmentAsync(bool allowApply, CancellationToken cancellationToken)
    {
        var profile = await SelectProfileAsync("Select profile payload", cancellationToken);
        if (profile is null)
        {
            return;
        }

        var plan = await RunAnimatedOperationAsync(
            "SCANNING CURRENT ENVIRONMENT",
            () => _application.PlanEnvironmentAsync(profile.Path, null, cancellationToken),
            cancellationToken);

        RenderFrame("EXECUTION PLAN", "NO MUTATIONS PERFORMED");
        RenderEnvironmentPlan(plan);
        if (!plan.Validation.IsValid || !plan.IsSupported || plan.Actions.Count == 0 || !allowApply)
        {
            WaitForReturn();
            return;
        }

        AnsiConsole.Write(new Panel(
                "[yellow]The following transaction will create rollback material before touching Windows.[/]\n" +
                "[grey]Default choice is NO. Unmanaged resources will not be removed.[/]")
            .Header(new PanelHeader(" SAFEGUARD GATE "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Yellow))
            .Expand());

        if (!AnsiConsole.Confirm("[bold yellow]AUTHORIZE TRANSACTION?[/]", false))
        {
            AnsiConsole.MarkupLine("[grey]Transaction rejected. Zero system changes were made.[/]");
            WaitForReturn();
            return;
        }

        var hasMachineActions = plan.Actions.Any(action => action.RequiresAdministrator);
        if (hasMachineActions && !AnsiConsole.Confirm(
            "[bold red]MACHINE SCOPE DETECTED. AUTHORIZE ELEVATED ACTIONS?[/]",
            false))
        {
            AnsiConsole.MarkupLine("[grey]Elevated transaction rejected. Zero system changes were made.[/]");
            WaitForReturn();
            return;
        }

        var execution = await RunAnimatedOperationAsync(
            "EXECUTING SECURE TRANSACTION",
            () => _application.ApplyEnvironmentAsync(
                profile.Path,
                null,
                hasMachineActions,
                true,
                cancellationToken),
            cancellationToken);

        RenderFrame("TRANSACTION TRACE", execution.Succeeded ? "VERIFIED" : "RECOVERY PATH");
        await RenderExecutionTraceAsync(execution, cancellationToken);
        WaitForReturn();
    }

    private static void ShowEnvironmentStatus(EnvironmentStatusReport status)
    {
        RenderFrame("ENV TELEMETRY", "LIVE READ-ONLY SNAPSHOT");
        var table = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("[bold green]SCOPE[/]")
            .AddColumn(new TableColumn("[bold white]VARIABLES[/]").Centered())
            .AddColumn(new TableColumn("[bold white]PATH ENTRIES[/]").Centered())
            .AddColumn("[bold white]ACCESS[/]");
        table.AddRow("USER", status.UserVariables.ToString(), status.UserPathEntries.ToString(), "[green]STANDARD[/]");
        table.AddRow("MACHINE", status.MachineVariables.ToString(), status.MachinePathEntries.ToString(), "[yellow]ELEVATED[/]");
        AnsiConsole.Write(table);

        foreach (var diagnostic in status.Diagnostics)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{DateTime.Now:HH:mm:ss}[/] " +
                $"[{(diagnostic.IsWarning ? "yellow" : "green")}]›[/] {Markup.Escape(diagnostic.Message)}");
        }

        WaitForReturn();
    }

    private async Task ShowCheckpointVaultAsync(CancellationToken cancellationToken)
    {
        RenderFrame("CHECKPOINT VAULT", "ROLLBACK MATERIAL");
        var checkpoints = await _application.ListEnvironmentCheckpointsAsync(cancellationToken);
        if (checkpoints.Count == 0)
        {
            AnsiConsole.Write(new Panel("[grey]No checkpoint manifests are currently indexed.[/]")
                .Header(new PanelHeader(" EMPTY VAULT "))
                .Border(BoxBorder.Double)
                .BorderStyle(new Style(Color.DarkGreen)));
            WaitForReturn();
            return;
        }

        var back = new EnvironmentCheckpointEntry(
            string.Empty,
            "[00] RETURN",
            DateTimeOffset.MinValue,
            string.Empty,
            0,
            string.Empty);
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<EnvironmentCheckpointEntry>()
                .Title("[grey]Select recovery manifest[/]")
                .PageSize(Math.Min(14, checkpoints.Count + 1))
                .HighlightStyle(new Style(Color.Black, Color.Green))
                .UseConverter(item => string.IsNullOrEmpty(item.ManifestPath)
                    ? item.ProfileName
                    : $"[green]›[/] {item.CreatedAt:yyyy-MM-dd HH:mm:ss}  {item.Status,-16}  {item.ProfileName}  [grey]{item.ActionCount} action(s)[/]")
                .AddChoices(checkpoints.Concat([back])));
        if (string.IsNullOrEmpty(selected.ManifestPath))
        {
            return;
        }

        var details = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Yellow))
            .AddColumn("FIELD")
            .AddColumn("VALUE");
        details.AddRow("TRANSACTION", Markup.Escape(selected.TransactionId));
        details.AddRow("PROFILE", Markup.Escape(selected.ProfileName));
        details.AddRow("STATUS", Markup.Escape(selected.Status));
        details.AddRow("ACTIONS", selected.ActionCount.ToString());
        details.AddRow("MANIFEST", Markup.Escape(selected.ManifestPath));
        AnsiConsole.Write(details);

        if (!AnsiConsole.Confirm("[bold red]RESTORE THIS CHECKPOINT?[/]", false))
        {
            AnsiConsole.MarkupLine("[grey]Recovery channel closed without changes.[/]");
            WaitForReturn();
            return;
        }

        var result = await RunAnimatedOperationAsync(
            "RESTORING CAPTURED STATE",
            () => _application.RollbackEnvironmentAsync(selected.ManifestPath, cancellationToken),
            cancellationToken);

        RenderFrame("ROLLBACK TRACE", result.Succeeded ? "STATE RESTORED" : "PARTIAL FAILURE");
        await RenderExecutionTraceAsync(result, cancellationToken);
        WaitForReturn();
    }

    private static void RenderEnvironmentPlan(EnvironmentPlanReport plan)
    {
        var summary = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("[bold green]SIGNAL[/]")
            .AddColumn("[bold white]VALUE[/]");
        summary.AddRow("PROFILE", Markup.Escape(plan.Loaded.Profile.Metadata.Name));
        summary.AddRow("PLATFORM CHANNEL", plan.IsSupported ? "[green]READY[/]" : "[yellow]OFFLINE[/]");
        summary.AddRow("CHANGE COUNT", plan.Summary.Changes.ToString());
        summary.AddRow("MACHINE ACTIONS", plan.Summary.AdministratorActions.ToString());
        summary.AddRow("DESTRUCTIVE", plan.Summary.Destructive.ToString());
        summary.AddRow("MAX RISK", RiskMarkup(plan.Summary.MaximumRisk.ToString()));
        AnsiConsole.Write(summary);

        if (!plan.Validation.IsValid)
        {
            var issues = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Red))
                .AddColumn("LOCATION")
                .AddColumn("ANOMALY");
            foreach (var issue in plan.Validation.Issues)
            {
                issues.AddRow(Markup.Escape(issue.Path), Markup.Escape(issue.Message));
            }

            AnsiConsole.Write(issues);
            return;
        }

        var actions = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand()
            .AddColumn("[bold green]RISK[/]")
            .AddColumn("[bold green]SCOPE[/]")
            .AddColumn("[bold green]OP[/]")
            .AddColumn("[bold green]RESOURCE[/]")
            .AddColumn("[bold green]TRACE[/]");
        foreach (var action in plan.Actions)
        {
            var scope = ActionProperty(action, "scope");
            var resource = action.Resource.ResourceType.EndsWith("variable", StringComparison.Ordinal)
                ? ActionProperty(action, "name")
                : ActionProperty(action, "path");
            actions.AddRow(
                RiskMarkup(action.Risk.ToString()),
                Markup.Escape(scope.ToUpperInvariant()),
                Markup.Escape(action.Operation.ToString().ToUpperInvariant()),
                Markup.Escape(resource),
                Markup.Escape(action.Explanation));
        }

        AnsiConsole.Write(actions);
        if (plan.Actions.Count == 0)
        {
            AnsiConsole.MarkupLine("\n[bold green]NO DRIFT DETECTED[/] [grey]// target state already matches profile[/]");
        }
    }

    private async Task RenderExecutionTraceAsync(
        EnvironmentExecutionReport result,
        CancellationToken cancellationToken)
    {
        var header = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(result.Succeeded ? Color.Green : result.RolledBack ? Color.Yellow : Color.Red))
            .Expand()
            .AddColumn("[bold green]FIELD[/]")
            .AddColumn("[bold white]VALUE[/]");
        header.AddRow("TRANSACTION", Markup.Escape(result.TransactionId));
        header.AddRow("PROFILE", Markup.Escape(result.ProfileName));
        header.AddRow("APPLY", result.Succeeded ? "[green]SUCCESS[/]" : "[red]FAILED[/]");
        header.AddRow("VERIFY", result.Verified ? "[green]MATCH[/]" : "[red]MISMATCH[/]");
        header.AddRow("ROLLBACK", result.RolledBack ? "[yellow]EXECUTED[/]" : "[grey]NOT REQUIRED[/]");
        header.AddRow("MANIFEST", Markup.Escape(result.CheckpointManifest ?? "none"));
        AnsiConsole.Write(header);

        AnsiConsole.MarkupLine("\n[grey]stream://transaction/actions[/]");
        foreach (var action in result.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = action.Status.ToString() switch
            {
                "Succeeded" => "[green]PASS[/]",
                "RolledBack" => "[yellow]ROLLBACK[/]",
                "RollbackFailed" => "[red]ROLLBACK-FAIL[/]",
                "VerificationFailed" => "[red]VERIFY-FAIL[/]",
                _ => $"[red]{Markup.Escape(action.Status.ToString().ToUpperInvariant())}[/]"
            };
            AnsiConsole.MarkupLine(
                $"[grey]{DateTime.Now:HH:mm:ss.fff}[/] {marker,-22} " +
                $"[white]{Markup.Escape(action.ActionId)}[/] [grey]// {Markup.Escape(action.Message)}[/]");
            await DelayAsync(70, cancellationToken);
        }

        AnsiConsole.Write(new Panel(new Markup(Markup.Escape(result.Message)))
            .Header(new PanelHeader(result.Succeeded ? " TRANSACTION VERIFIED " : " TRANSACTION RECOVERY "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(result.Succeeded ? Color.Green : result.RolledBack ? Color.Yellow : Color.Red))
            .Expand());
    }

    private async Task ShowDeepScanAsync(CancellationToken cancellationToken)
    {
        RenderFrame("DEEP SCAN", "DIAGNOSTIC PROBES");
        var report = await RunAnimatedOperationAsync(
            "PROBING MODULE BOUNDARIES",
            () => _application.RunDoctorAsync(cancellationToken),
            cancellationToken);

        var table = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(report.IsHealthy ? Color.Green : Color.Red))
            .Expand()
            .AddColumn("[bold green]STATE[/]")
            .AddColumn("[bold green]PROBE[/]")
            .AddColumn("[bold green]TRACE[/]");
        foreach (var check in report.Checks)
        {
            var state = check.Status switch
            {
                DiagnosticStatus.Ok => "[green]PASS[/]",
                DiagnosticStatus.Warning => "[yellow]WARN[/]",
                _ => "[red]FAIL[/]"
            };
            table.AddRow(state, Markup.Escape(check.Name), Markup.Escape(check.Message));
        }

        AnsiConsole.Write(table);
        AnsiConsole.Write(new Panel(
                report.IsHealthy
                    ? "[bold green]ALL CRITICAL PROBES PASSED[/]"
                    : "[bold red]CRITICAL ANOMALIES DETECTED[/]")
            .Header(new PanelHeader(" SCAN RESULT "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(report.IsHealthy ? Color.Green : Color.Red)));
        WaitForReturn();
    }

    private async Task ShowDataCoreAsync(CancellationToken cancellationToken)
    {
        RenderFrame("DATA CORE", "SQLITE STATE STORAGE");
        await RunAnimatedOperationAsync(
            "VERIFYING MIGRATION CHAIN",
            async () =>
            {
                await _application.InitializeStorageAsync(cancellationToken);
                return true;
            },
            cancellationToken);

        var status = await _application.GetStorageStatusAsync(cancellationToken);
        var tables = await _application.GetStorageTablesAsync(cancellationToken);
        var summary = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("[bold green]FIELD[/]")
            .AddColumn("[bold white]VALUE[/]");
        summary.AddRow("DATABASE", Markup.Escape(status.DatabasePath));
        summary.AddRow("SCHEMA", status.LatestMigrationVersion.ToString());
        summary.AddRow("MIGRATIONS", status.AppliedMigrations.ToString());
        summary.AddRow("SIZE", FormatBytes(status.DatabaseSizeBytes));
        summary.AddRow("TABLE COUNT", tables.Count.ToString());
        AnsiConsole.Write(summary);

        var tableText = tables.Count == 0
            ? "[grey]No user tables found.[/]"
            : string.Join("\n", tables.Select((value, index) => $"[green]{index + 1:00}[/] [white]{Markup.Escape(value)}[/]"));
        AnsiConsole.Write(new Panel(new Markup(tableText))
            .Header(new PanelHeader(" STORAGE MAP "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand());
        WaitForReturn();
    }

    private void ShowNodeConfiguration()
    {
        RenderFrame("NODE CONFIG", "RUNTIME PARAMETERS");
        var table = new Table()
            .Border(TableBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand()
            .AddColumn("[bold green]KEY[/]")
            .AddColumn("[bold white]VALUE[/]");
        table.AddRow("HOME", Markup.Escape(_application.Options.HomeDirectory));
        table.AddRow("PROFILE VAULT", Markup.Escape(_application.Options.ProfilesDirectory));
        table.AddRow("DATA CORE", Markup.Escape(_application.Options.DatabasePath));
        table.AddRow("LOG CHANNEL", Markup.Escape(_application.Options.LogsDirectory));
        table.AddRow("CONFIG", Markup.Escape(_application.Options.ConfigPath));
        table.AddRow("PORTABLE", _application.Options.PortableMode ? "[yellow]TRUE[/]" : "[green]FALSE[/]");
        table.AddRow("LOG LEVEL", Markup.Escape(_application.Options.MinimumLogLevel.ToString().ToUpperInvariant()));
        AnsiConsole.Write(table);

        AnsiConsole.Write(new Panel(
                "[grey]Environment overrides use the WINSTATE_* namespace.\n" +
                "System operations remain guarded regardless of frontend or configuration mode.[/]")
            .Header(new PanelHeader(" CONFIG POLICY "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.DarkGreen)));
        WaitForReturn();
    }

    private static void ShowSystemMap()
    {
        RenderFrame("SYSTEM MAP", "ARCHITECTURE + ROADMAP");
        AnsiConsole.Write(new Panel(
                "[bold green]CYBER TERMINAL[/]\n" +
                "      │ animated control node, live traces, confirmations\n" +
                "      ▼\n" +
                "[bold white]APPLICATION WORKFLOWS[/]\n" +
                "      │ plan → checkpoint → apply → verify → rollback\n" +
                "      ├── Profile Engine\n" +
                "      ├── Environment Provider\n" +
                "      ├── SQLite History\n" +
                "      └── Domain Contracts")
            .Header(new PanelHeader(" MODULE TOPOLOGY "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand());

        var roadmap = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand()
            .AddColumn("STAGE")
            .AddColumn("CHANNEL")
            .AddColumn("STATE");
        roadmap.AddRow("0.4", "Environment Provider vertical slice", "[green]COMPLETE[/]");
        roadmap.AddRow("0.5", "Cyber Control Center + animated action telemetry", "[green]ACTIVE[/]");
        roadmap.AddRow("0.6", "Unified multi-provider Apply Engine", "[yellow]NEXT[/]");
        roadmap.AddRow("0.7", "Packages / Features / Services providers", "[grey]QUEUED[/]");
        AnsiConsole.Write(roadmap);

        AnsiConsole.Write(new Panel(
                "[green]NexRoute-inspired DNA:[/] dense control-node layout, numbered operation channels, " +
                "boot traces, action streams and high-contrast terminal visuals.\n" +
                "[grey]WinState keeps its own safety-first identity: visuals never bypass application safeguards.[/]")
            .Header(new PanelHeader(" VISUAL DIRECTION "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Green))
            .Expand());
        WaitForReturn();
    }

    private async Task<ProfileCatalogEntry?> SelectProfileAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var profiles = await DiscoverProfilesAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            AnsiConsole.Write(new Panel(
                    $"[yellow]No profiles found.[/]\nVault: {Markup.Escape(_application.Options.ProfilesDirectory)}")
                .Border(BoxBorder.Double)
                .BorderStyle(new Style(Color.Yellow)));
            WaitForReturn();
            return null;
        }

        var back = new ProfileCatalogEntry("[00] RETURN", string.Empty, 0, DateTimeOffset.MinValue);
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileCatalogEntry>()
                .Title($"[grey]{Markup.Escape(title)}[/]")
                .PageSize(Math.Min(15, profiles.Count + 1))
                .HighlightStyle(new Style(Color.Black, Color.Green))
                .UseConverter(item => string.IsNullOrEmpty(item.Path)
                    ? item.Name
                    : $"[green]›[/] {item.Name,-34} [grey]{FormatBytes(item.SizeBytes),8}[/]")
                .AddChoices(profiles.Concat([back])));
        return string.IsNullOrEmpty(selected.Path) ? null : selected;
    }

    private async Task<IReadOnlyList<ProfileCatalogEntry>> DiscoverProfilesAsync(
        CancellationToken cancellationToken)
    {
        var profiles = (await _application.ListProfilesAsync(cancellationToken)).ToList();
        var sampleRoot = Path.Combine(Environment.CurrentDirectory, "samples");
        if (Directory.Exists(sampleRoot))
        {
            foreach (var path in Directory.EnumerateFiles(sampleRoot, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                if (profiles.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                profiles.Add(new ProfileCatalogEntry(
                    $"sample/{Path.GetFileNameWithoutExtension(fullPath)}",
                    fullPath,
                    info.Length,
                    info.LastWriteTimeUtc));
            }
        }

        return profiles
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<T> RunAnimatedOperationAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        T result = default!;
        var assigned = false;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                var handshake = context.AddTask("[grey]handshake[/]", maxValue: 100);
                var operationTask = context.AddTask($"[green]{Markup.Escape(operation.ToLowerInvariant())}[/]", maxValue: 100);
                var seal = context.AddTask("[grey]seal result[/]", maxValue: 100);

                await PulseAsync(handshake, 100, 4, cancellationToken);
                operationTask.Value = 12;
                result = await action();
                assigned = true;
                operationTask.Value = 100;
                await PulseAsync(seal, 100, 3, cancellationToken);
            });

        if (!assigned)
        {
            throw new InvalidOperationException($"Operation '{operation}' returned no result.");
        }

        return result;
    }

    private async Task PulseAsync(
        ProgressTask task,
        double target,
        int steps,
        CancellationToken cancellationToken)
    {
        var remaining = target - task.Value;
        var increment = steps == 0 ? remaining : remaining / steps;
        for (var index = 0; index < steps; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            task.Increment(increment);
            await DelayAsync(45, cancellationToken);
        }

        task.Value = target;
    }

    private async Task ShutdownSequenceAsync(CancellationToken cancellationToken)
    {
        RenderFrame("DISCONNECT", "CLOSING SECURE SESSION");
        var lines = new[]
        {
            "flushing terminal telemetry",
            "locking checkpoint vault",
            "detaching provider channel",
            "session closed"
        };
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss.fff}[/] [green]›[/] {Markup.Escape(line)}");
            await DelayAsync(80, cancellationToken);
        }

        AnsiConsole.MarkupLine("\n[bold green]WINSTATE OFFLINE[/]");
    }

    private async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (_fastMode)
        {
            return;
        }

        await Task.Delay(milliseconds, cancellationToken);
    }

    private static void RenderFrame(string channel, string state)
    {
        AnsiConsole.Clear();
        DrawCyberLogo();
        var header = new Table()
            .Border(TableBorder.None)
            .Expand()
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn(string.Empty).RightAligned());
        header.AddRow(
            $"[bold green]CHANNEL://{Markup.Escape(channel.Replace(' ', '-').ToLowerInvariant())}[/]",
            $"[grey]v{WinStateApplication.Version}  {Markup.Escape(state)}  {DateTime.Now:HH:mm:ss}[/]");
        AnsiConsole.Write(header);
        AnsiConsole.Write(new Rule("[grey]WINSTATE SECURE CONTROL FABRIC[/]")
            .RuleStyle(new Style(Color.DarkGreen))
            .Centered());
    }

    private static void DrawCyberLogo()
    {
        var logo = new FigletText("WINSTATE")
            .LeftJustified()
            .Color(Color.Green);
        AnsiConsole.Write(logo);
        AnsiConsole.MarkupLine(
            "[bold green]CYBER CONTROL CENTER[/] [grey]// state orchestration node // safety gates armed[/]");
    }

    private static void RenderEventFeed(IEnumerable<(string Channel, string Message)> events)
    {
        var rows = events.Select(item =>
            (IRenderable)new Markup(
                $"[grey]{DateTime.Now:HH:mm:ss}[/] [green]›[/] " +
                $"[bold white]{Markup.Escape(item.Channel),-10}[/] [grey]{Markup.Escape(item.Message)}[/]"));
        AnsiConsole.Write(new Panel(new Rows(rows))
            .Header(new PanelHeader(" LIVE EVENT FEED "))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.DarkGreen))
            .Expand());
    }

    private static string RiskMarkup(string risk)
    {
        return risk.ToUpperInvariant() switch
        {
            "NONE" or "LOW" => $"[green]{Markup.Escape(risk.ToUpperInvariant())}[/]",
            "MEDIUM" => "[yellow]MEDIUM[/]",
            _ => $"[red]{Markup.Escape(risk.ToUpperInvariant())}[/]"
        };
    }

    private static string ActionProperty(PlannedAction action, string name)
        => action.Resource.Properties.TryGetValue(name, out var value)
            ? value.Value ?? string.Empty
            : string.Empty;

    private static void ShowFailure(string message)
    {
        RenderFrame("FAULT", "OPERATION ABORTED");
        AnsiConsole.Write(new Panel(
                new Markup($"[bold red]FAULT DETECTED[/]\n\n[white]{Markup.Escape(message)}[/]\n\n" +
                    "[grey]No safeguard was bypassed. Inspect the trace and retry after correction.[/]"))
            .Header(new PanelHeader(" EXECUTION FAILURE "))
            .Border(BoxBorder.Double)
            .BorderStyle(new Style(Color.Red))
            .Expand());
        WaitForReturn();
    }

    private static void WaitForReturn()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[grey]Press any key to return to the control node...[/]");
        _ = Console.ReadKey(intercept: true);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private static string FormatUptime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
            : value.ToString("hh\\:mm\\:ss");
    }

    private static void TrySetConsoleTitle(string title)
    {
        try
        {
            Console.Title = title;
        }
        catch (IOException)
        {
            // Some redirected or virtual terminals do not expose a title channel.
        }
    }

    private sealed record CyberMenuEntry(string Id, string Title, string Description);

    private sealed record CyberTelemetry(
        WinState.Storage.StorageStatus Storage,
        IReadOnlyList<ProfileCatalogEntry> Profiles,
        EnvironmentStatusReport Environment,
        IReadOnlyList<EnvironmentCheckpointEntry> Checkpoints);
}
