using Spectre.Console;
using WinState.Apply;
using WinState.App;
using WinState.Domain.Planning;

namespace WinState.Terminal;

/// <summary>Верхний frontend 0.7: Nexus плюс Package & Feature Forge.</summary>
public sealed class CyberForgeShell
{
    private readonly WinStateApplication _application;

    public CyberForgeShell(WinStateApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public async Task<int> RunAsync(bool demoMode, CancellationToken cancellationToken)
    {
        Console.Title = $"WinState Forge {WinStateApplication.Version}";
        if (demoMode)
        {
            RenderDemo();
            return 0;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            RenderHeader("FORGE CONTROL FABRIC");
            var channel = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold green]SELECT FORGE CHANNEL[/]")
                    .HighlightStyle(new Style(Color.Black, Color.Green1))
                    .AddChoices(
                        "[01] NEXUS CONTROL FABRIC // transactions, update uplink and legacy control center",
                        "[02] PACKAGE & FEATURE FORGE // WinGet inventory, DISM features and unified plans",
                        "[00] DISCONNECT // close secure session"));

            if (channel.StartsWith("[01]", StringComparison.Ordinal))
            {
                return await new CyberNexusShell(_application).RunAsync(false, cancellationToken);
            }

            if (channel.StartsWith("[02]", StringComparison.Ordinal))
            {
                await RunForgeAsync(cancellationToken);
                continue;
            }

            AnsiConsole.MarkupLine("[grey]FORGE LINK CLOSED.[/]");
            return 0;
        }

        return 130;
    }

    private async Task RunForgeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            RenderHeader("PACKAGE & FEATURE FORGE");
            var status = await ReadStatusAsync(cancellationToken);
            RenderStatus(status);
            var operation = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold green]SELECT FORGE OPERATION[/]")
                    .HighlightStyle(new Style(Color.Black, Color.Green1))
                    .AddChoices(
                        "[11] REFRESH INVENTORY // winget list + DISM feature inventory",
                        "[12] BUILD UNIFIED PLAN // environment + packages + features",
                        "[13] EXECUTE VERIFIED PLAN // policy gates + checkpoints + verification",
                        "[00] RETURN // back to Forge Control Fabric"));

            if (operation.StartsWith("[00]", StringComparison.Ordinal))
            {
                return;
            }

            if (operation.StartsWith("[11]", StringComparison.Ordinal))
            {
                continue;
            }

            var profile = SelectProfile();
            if (profile is null)
            {
                continue;
            }

            var plan = await ReadPlanAsync(profile, cancellationToken);
            RenderPlan(plan);
            if (!operation.StartsWith("[13]", StringComparison.Ordinal)
                || !plan.Validation.IsValid
                || !plan.IsSupported
                || plan.Plan.OrderedActions.Count == 0)
            {
                WaitForReturn();
                continue;
            }

            if (!AnsiConsole.Confirm("[bold yellow]EXECUTE THIS MULTI-PROVIDER GRAPH?[/]", false))
            {
                continue;
            }

            var allowAdmin = !plan.Plan.RequiresAdministrator
                || AnsiConsole.Confirm("[bold red]AUTHORIZE ADMINISTRATOR ACTION GROUP?[/]", false);
            var allowCritical = plan.Plan.MaximumRisk < WinState.Domain.Configuration.RiskLevel.Critical
                || AnsiConsole.Confirm("[bold red]AUTHORIZE CRITICAL ACTION GROUP?[/]", false);
            var allowIrreversible = !plan.Plan.ContainsIrreversible
                || AnsiConsole.Confirm("[bold red]AUTHORIZE IRREVERSIBLE PACKAGE ACTIONS?[/]", false);
            if (!allowAdmin || !allowCritical || !allowIrreversible)
            {
                AnsiConsole.MarkupLine("[yellow]POLICY GATE DENIED // no system mutation.[/]");
                WaitForReturn();
                continue;
            }

            ApplyEngineReport? report = null;
            Exception? failure = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("green"))
                .StartAsync("[green]checkpoint → apply → verify → seal transaction...[/]", async _ =>
                {
                    try
                    {
                        report = await _application.ApplyUnifiedAsync(
                            profile,
                            null,
                            new ApplyEngineOptions
                            {
                                AutomaticRollback = true,
                                AllowAdministrator = allowAdmin,
                                AllowCritical = allowCritical,
                                AllowIrreversible = allowIrreversible,
                                AllowReboot = false
                            },
                            allowAdmin,
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

            RenderHeader("FORGE EXECUTION TRACE");
            if (failure is not null || report is null)
            {
                AnsiConsole.MarkupLine($"[red]FAILED // {Markup.Escape(failure?.Message ?? "no report")}[/]");
            }
            else
            {
                RenderReport(report);
            }

            WaitForReturn();
        }
    }

    private async Task<SystemProvidersStatusReport> ReadStatusAsync(CancellationToken cancellationToken)
    {
        SystemProvidersStatusReport? status = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync("[green]index winget packages and optional features...[/]", async _ =>
            {
                status = await _application.GetSystemProvidersStatusAsync(cancellationToken);
            });
        return status ?? new SystemProvidersStatusReport(
            false,
            false,
            false,
            0,
            0,
            0,
            0,
            Array.Empty<WinState.Domain.Providers.ProviderDiagnostic>());
    }

    private async Task<UnifiedApplyPlanReport> ReadPlanAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        UnifiedApplyPlanReport? plan = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots2)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync("[green]discover → diff → merge provider graphs...[/]", async _ =>
            {
                plan = await _application.PlanUnifiedApplyAsync(profile, null, cancellationToken);
            });
        return plan ?? throw new InvalidOperationException("Unified plan не получен.");
    }

    private static void RenderStatus(SystemProvidersStatusReport status)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Green))
            .AddColumn("PROVIDER")
            .AddColumn("LINK")
            .AddColumn("TELEMETRY");
        table.AddRow("environment", status.EnvironmentSupported ? "[green]ONLINE[/]" : "[yellow]OFFLINE[/]", "variables + PATH");
        table.AddRow("packages.winget", status.WingetSupported ? "[green]ONLINE[/]" : "[yellow]OFFLINE[/]", $"installed={status.InstalledPackages} updates={status.PackagesWithUpdates}");
        table.AddRow("windows.features", status.FeaturesSupported ? "[green]ONLINE[/]" : "[yellow]OFFLINE[/]", $"enabled={status.EnabledFeatures} disabled={status.DisabledFeatures}");
        AnsiConsole.Write(table);
        foreach (var diagnostic in status.Diagnostics)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(diagnostic.Code)} // {Markup.Escape(diagnostic.Message)}[/]");
        }
    }

    private static void RenderPlan(UnifiedApplyPlanReport report)
    {
        RenderHeader("UNIFIED PACKAGE & FEATURE GRAPH");
        AnsiConsole.MarkupLine($"PROFILE      [white]{Markup.Escape(report.Loaded.Profile.Metadata.Name)}[/]");
        AnsiConsole.MarkupLine($"PROVIDERS    [green]{Markup.Escape(string.Join(", ", report.Plan.Providers))}[/]");
        AnsiConsole.MarkupLine($"ACTIONS      [white]{report.Plan.OrderedActions.Count}[/]");
        AnsiConsole.MarkupLine($"MAX RISK     [yellow]{report.Plan.MaximumRisk}[/]");
        AnsiConsole.MarkupLine($"ADMIN        [white]{report.Plan.RequiresAdministrator}[/]");
        AnsiConsole.MarkupLine($"REBOOT       [white]{report.Plan.RequiresReboot}[/]");
        AnsiConsole.MarkupLine($"IRREVERSIBLE [white]{report.Plan.ContainsIrreversible}[/]");
        foreach (var issue in report.Validation.Issues)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(issue.Path)} // {Markup.Escape(issue.Message)}[/]");
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("RISK")
            .AddColumn("PROVIDER")
            .AddColumn("OP")
            .AddColumn("ACTION");
        foreach (var action in report.Plan.OrderedActions)
        {
            table.AddRow(
                action.Risk.ToString(),
                Markup.Escape(action.ProviderId),
                action.Operation.ToString(),
                Markup.Escape(action.Explanation));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderReport(ApplyEngineReport report)
    {
        AnsiConsole.MarkupLine($"TRANSACTION [white]{Markup.Escape(report.TransactionId)}[/]");
        AnsiConsole.MarkupLine($"STATUS      [yellow]{report.Status}[/]");
        AnsiConsole.MarkupLine($"VERIFIED    [white]{report.Verified}[/]");
        AnsiConsole.MarkupLine($"ROLLBACK    [white]{report.RolledBack}[/]");
        AnsiConsole.MarkupLine($"REBOOT      [white]{report.RebootRequired}[/]");
        foreach (var result in report.Results)
        {
            var style = result.Status == ActionStatus.Succeeded ? "green" : "yellow";
            AnsiConsole.MarkupLine(
                $"[{style}]{result.Status,-18}[/] {Markup.Escape(result.ProviderId),-24} {Markup.Escape(result.ActionId)} // {Markup.Escape(result.Message)}");
        }
    }

    private string? SelectProfile()
    {
        var profiles = DiscoverProfiles().ToArray();
        if (profiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]YAML profiles не найдены в profiles/ или samples/.[/]");
            WaitForReturn();
            return null;
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold green]SELECT PROFILE[/]")
                .PageSize(Math.Min(12, profiles.Length))
                .HighlightStyle(new Style(Color.Black, Color.Green1))
                .AddChoices(profiles));
        return selected;
    }

    private IEnumerable<string> DiscoverProfiles()
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
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void RenderDemo()
    {
        RenderHeader("FORGE CONTROL FABRIC");
        AnsiConsole.MarkupLine("[green][[01]][/] NEXUS CONTROL FABRIC");
        AnsiConsole.MarkupLine("[green][[02]][/] PACKAGE & FEATURE FORGE");
        AnsiConsole.MarkupLine("[green][[11]][/] WINGET INVENTORY // ONLINE");
        AnsiConsole.MarkupLine("[green][[12]][/] WINDOWS OPTIONAL FEATURES // DISM LINK");
        AnsiConsole.MarkupLine("[green][[13]][/] UNIFIED APPLY GRAPH // CHECKPOINTED");
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("PROVIDER")
            .AddColumn("CAPABILITY")
            .AddColumn("ROLLBACK");
        table.AddRow("environment", "variables + PATH", "full");
        table.AddRow("packages.winget", "install / upgrade / uninstall", "install only");
        table.AddRow("windows.features", "enable / disable", "full");
        AnsiConsole.Write(table);
    }

    private static void RenderHeader(string title)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold green] WINSTATE {Markup.Escape(title)} // {WinStateApplication.Version} [/]")
            .RuleStyle("green"));
        AnsiConsole.MarkupLine("[grey]profile → provider graph → policy gates → checkpoint → apply → verify → rollback[/]");
        AnsiConsole.WriteLine();
    }

    private static void WaitForReturn()
    {
        AnsiConsole.MarkupLine("\n[grey]Press any key to return...[/]");
        _ = Console.ReadKey(true);
    }
}
