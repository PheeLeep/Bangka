using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>
/// Rolls a service back to a previous snapshot on a remote host (over SSH).
/// Unlike the old bangkactl implementation, every step runs sequentially and
/// synchronously — no fire-and-forget task that gets killed before the restore
/// commands execute.
/// </summary>
public static class RollbackCommand
{
    public static void Load(ArgInvoke invoke)
    {
        Arguments.LoadConnectionArgs(invoke);
        invoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name");
        invoke.AddArgument<string>(["--snapshot"], helpMsg: "Snapshot to restore (yyyyMMdd-HHmmss). Omit to pick interactively.");
    }

    public static int Run(ArgInvoke invoke)
    {
        var vals = invoke.GetArgStoreValues();
        var snArg = vals.SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        var snapArg = vals.SingleOrDefault(a => a.Parameters.Contains("--snapshot")) as ArgStore<string>;

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

        var (instCode, _, _) = session.Run($"test -d {ShellUtil.Quote(installPath)} && echo yes");
        if (instCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Service [white]{Markup.Escape(serviceName)}[/] is not installed on {Markup.Escape(target.Host)}.[/]");
            return 1;
        }

        // ── Enumerate snapshots (newest first, ordered by name) ───────────────
        var (_, lsOut, _) = session.Run($"ls -1 {ShellUtil.Quote(rollbackDir)} 2>/dev/null");
        var snapshots = lsOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .ToList();

        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No rollback snapshots found for[/] [white]{Markup.Escape(serviceName)}[/].");
            AnsiConsole.MarkupLine("[grey]Snapshots are created automatically during each deploy.[/]");
            return 1;
        }

        // ── Resolve target snapshot ───────────────────────────────────────────
        string chosen;
        var requested = snapArg?.TypedValue;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!snapshots.Contains(requested))
            {
                AnsiConsole.MarkupLine($"[red]Snapshot not found:[/] {Markup.Escape(requested)}");
                AnsiConsole.MarkupLine("[grey]Available snapshots:[/]");
                foreach (var s in snapshots) AnsiConsole.MarkupLine($"[white]{Markup.Escape(s)}[/]");
                return 1;
            }
            chosen = requested;
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]Available snapshots for [green]{Markup.Escape(serviceName)}[/]:[/]\n");
            var table = new Table()
                .Border(TableBorder.Rounded).BorderColor(Color.Grey)
                .AddColumn("[grey]Snapshot[/]").AddColumn("[grey]Approx. date[/]").AddColumn("[grey]Size[/]");

            foreach (var snap in snapshots)
            {
                var (_, duOut, _) = session.Run(
                    $"du -sm {ShellUtil.Quote($"{rollbackDir}/{snap}")} 2>/dev/null | cut -f1");
                var size = int.TryParse(duOut.Trim(), out var mb) ? $"{mb} MB" : "—";
                var friendly = DateTime.TryParseExact(snap, Constants.SnapshotStampFormat,
                    null, System.Globalization.DateTimeStyles.None, out var dt)
                    ? dt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : snap;
                table.AddRow($"[white]{Markup.Escape(snap)}[/]", Markup.Escape(friendly), size);
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            chosen = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select snapshot to restore:[/]")
                    .AddChoices(snapshots)
                    .HighlightStyle(Style.Parse("cyan bold")));
        }

        var chosenPath = $"{rollbackDir}/{chosen}";

        AnsiConsole.MarkupLine($"\n[yellow]This will stop [white]{Markup.Escape(serviceName)}[/], replace the current install[/]");
        AnsiConsole.MarkupLine($"[yellow]with snapshot [white]{Markup.Escape(chosen)}[/], and restart the service.[/]\n");
        if (!AnsiConsole.Confirm("[yellow]Proceed with rollback?[/]", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Rollback cancelled.[/]");
            return 0;
        }

        // ── Execute — sequential, synchronous, with restore-on-failure ────────
        var prevPath = $"{installPath}.prev";
        bool restored = false;
        try
        {
            AnsiConsole.MarkupLine("[grey]Stopping service...[/]");
            session.RunPrivileged($"systemctl stop {q}");

            AnsiConsole.MarkupLine("[grey]Replacing install directory...[/]");
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(prevPath)}");
            session.RunPrivilegedOrThrow($"mv {ShellUtil.Quote(installPath)} {ShellUtil.Quote(prevPath)}");
            var (cpCode, _, cpErr) = session.RunPrivileged(
                $"cp -a {ShellUtil.Quote(chosenPath)} {ShellUtil.Quote(installPath)}");
            if (cpCode != 0)
            {
                // Restore the previous install and abort.
                session.RunPrivileged($"rm -rf {ShellUtil.Quote(installPath)}");
                session.RunPrivileged($"mv {ShellUtil.Quote(prevPath)} {ShellUtil.Quote(installPath)}");
                throw new InvalidOperationException($"Snapshot copy failed: {cpErr}");
            }
            restored = true;

            AnsiConsole.MarkupLine("[grey]Reloading systemd and starting service...[/]");
            session.RunPrivileged("systemctl daemon-reload");
            session.RunPrivileged($"systemctl start {q}");
        }
        catch (Exception ex)
        {
            AnsiConsole.Write(new Panel($"[bold red]✗[/] {Markup.Escape(ex.Message)}")
                .Header("[bold red] Rollback Failed [/]").BorderColor(Color.Red));
            AuditLog.Record(new AuditRecord
            {
                Action = "rollback", PackageName = serviceName, Host = target.Host, Result = "failed", Note = ex.Message
            });
            return 1;
        }

        // Clean up the previous copy now that the new one is in place.
        if (restored)
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(prevPath)}");

        // ── Verify service is back up (poll, like deploy does) ────────────────
        bool healthy = false;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            var (_, activeOut, _) = session.Run($"systemctl is-active {q}");
            var status = activeOut.Trim();
            if (status == "active") { healthy = true; break; }
            if (status == "failed" || status == "inactive") break;
            Thread.Sleep(1500);
        }

        AuditLog.Record(new AuditRecord
        {
            Action = "rollback", PackageName = serviceName, Host = target.Host,
            Result = healthy ? "success" : "failed", Note = $"snapshot {chosen}"
        });

        if (healthy)
        {
            AnsiConsole.Write(new Panel(
                    $"[bold green]✓[/] [white]{Markup.Escape(serviceName)}[/] rolled back to [white]{Markup.Escape(chosen)}[/] and is now active.")
                .Header("[bold green] Rollback Successful [/]").BorderColor(Color.Green));
            return 0;
        }

        AnsiConsole.Write(new Panel(
                "[bold red]✗[/] Service did not return to active state after rollback.\n" +
                $"[grey]Check logs: [white]bangka logs --service-name {Markup.Escape(serviceName)}[/][/]")
            .Header("[bold red] Rollback Warning [/]").BorderColor(Color.Red));
        return 1;
    }
}
