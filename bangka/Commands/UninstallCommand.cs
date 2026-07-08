using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>Uninstalls a service from a remote host (over SSH).</summary>
public static class UninstallCommand
{
    public static void Load(ArgInvoke invoke)
    {
        Arguments.LoadConnectionArgs(invoke);
        invoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name to uninstall");
        invoke.AddArgument<bool>(["--keep-snapshots"], helpMsg: "Keep rollback snapshots after uninstall");
        invoke.AddArgument<bool>(["--keep-data"], helpMsg: "Keep the service data directory after uninstall");
    }

    public static int Run(ArgInvoke invoke)
    {
        var vals = invoke.GetArgStoreValues();
        var snArg = vals.SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        var keepSnapshots = (vals.SingleOrDefault(a => a.Parameters.Contains("--keep-snapshots")) as ArgStore<bool>)?.TypedValue == true;
        var keepData = (vals.SingleOrDefault(a => a.Parameters.Contains("--keep-data")) as ArgStore<bool>)?.TypedValue == true;

        if (snArg is null || string.IsNullOrWhiteSpace(snArg.TypedValue))
        {
            AnsiConsole.MarkupLine("[red]--service-name is required.[/]");
            return 1;
        }
        var serviceName = snArg.TypedValue;
        try { ShellUtil.ValidateServiceName(serviceName); }
        catch (ArgumentException ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using var session = target.Connect();
        var q = ShellUtil.Quote(serviceName);
        var basePath = session.ResolveServicesBase();
        var installPath = $"{basePath}/{serviceName}";
        var rollbackDir = $"{basePath}/.rollback/{serviceName}";
        var serviceFile = $"/etc/systemd/system/{serviceName}.service";

        var (instCode, _, _) = session.Run($"test -d {ShellUtil.Quote(installPath)} && echo yes");
        var (svcCode, _, _) = session.Run($"test -f {ShellUtil.Quote(serviceFile)} && echo yes");
        if (instCode != 0 && svcCode != 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Service [white]{Markup.Escape(serviceName)}[/] does not appear to be installed.[/]");
            return 1;
        }

        if (!AnsiConsole.Confirm($"[yellow]Uninstall [white]{Markup.Escape(serviceName)}[/] from {Markup.Escape(target.Host)}?[/]", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }
        AnsiConsole.WriteLine();

        // Resolve the data directory from the unit's DATA_PATH before we tear it down.
        string? dataDir = null;
        if (!keepData)
        {
            var (_, envOut, _) = session.Run($"systemctl show {q} --property=Environment --no-pager 2>/dev/null");
            var m = System.Text.RegularExpressions.Regex.Match(envOut, @"DATA_PATH=([^\s]+)");
            if (m.Success) dataDir = m.Groups[1].Value;
        }

        var (_, activeOut, _) = session.Run($"systemctl is-active {q} 2>/dev/null");
        if (activeOut.Trim() == "active")
        {
            AnsiConsole.MarkupLine("  [grey]Stopping service...[/]");
            session.RunPrivileged($"systemctl stop {q}");
        }

        AnsiConsole.MarkupLine("  [grey]Disabling service...[/]");
        session.RunPrivileged($"systemctl disable {q} 2>/dev/null");

        if (svcCode == 0)
        {
            AnsiConsole.MarkupLine("  [grey]Removing systemd unit file...[/]");
            session.RunPrivileged($"rm -f {ShellUtil.Quote(serviceFile)}");
            session.RunPrivileged("systemctl daemon-reload");
            session.RunPrivileged($"systemctl reset-failed {q} 2>/dev/null || true");
        }

        if (instCode == 0)
        {
            AnsiConsole.MarkupLine("  [grey]Removing install directory...[/]");
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(installPath)}");
        }

        if (!keepSnapshots)
        {
            var (rbCode, _, _) = session.Run($"test -d {ShellUtil.Quote(rollbackDir)} && echo yes");
            if (rbCode == 0)
            {
                AnsiConsole.MarkupLine("  [grey]Removing rollback snapshots...[/]");
                session.RunPrivileged($"rm -rf {ShellUtil.Quote(rollbackDir)}");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]Keeping rollback snapshots as requested.[/]");
        }

        if (!keepData && !string.IsNullOrWhiteSpace(dataDir))
        {
            AnsiConsole.MarkupLine($"  [grey]Removing data directory: {Markup.Escape(dataDir)}[/]");
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(dataDir)}");
        }
        else if (keepData)
        {
            AnsiConsole.MarkupLine("  [grey]Keeping data directory as requested (--keep-data).[/]");
        }

        var cloudflaredCfg = $"/etc/cloudflared/{serviceName}.yml";
        var (cfCode, _, _) = session.RunPrivileged($"test -f {ShellUtil.Quote(cloudflaredCfg)} && echo yes");
        if (cfCode == 0)
        {
            AnsiConsole.MarkupLine("  [grey]Removing Cloudflare per-service config...[/]");
            session.RunPrivileged($"rm -f {ShellUtil.Quote(cloudflaredCfg)}");
            session.RunPrivileged("systemctl reload cloudflared 2>/dev/null || true");
        }

        AnsiConsole.Write(new Panel(
                $"[bold green]✓[/] [white]{Markup.Escape(serviceName)}[/] has been uninstalled.")
            .Header("[bold green] Uninstall Complete [/]").BorderColor(Color.Green));
        return 0;
    }
}
