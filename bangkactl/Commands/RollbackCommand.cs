using System;
using ArgSharp.Args;
using Spectre.Console;

namespace bangkactl.Commands;

public static class RollbackCommand
{
    private static IEnumerable<string> ResolveServicesBases()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ownDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "bangkasvcs");
        candidates.Add(ownDir);

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser))
            candidates.Add($"/home/{sudoUser}/bangkasvcs");

        if (Directory.Exists("/home"))
        {
            foreach (var d in Directory.GetDirectories("/home"))
                candidates.Add(Path.Combine(d, "bangkasvcs"));
        }

        return candidates.Where(Directory.Exists);
    }

    public static void Load(ArgInvoke argInvoke)
    {
        argInvoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name");
        argInvoke.AddArgument<string>(["--snapshot"], helpMsg: "Snapshot name to move. Leaving it blank will revert to latest.");
    }

    public static int Run(ArgInvoke argInvoke)
    {
        var sn = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        var targetSnap = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--snapshot")) as ArgStore<string>;

        if (sn is null)
        {
            AnsiConsole.MarkupLine("[red]Service name not found[/]");
            return 1;
        }

        var servicesBases = ResolveServicesBases().ToList();

        string? foundBase = null;
        string? installPath = null;
        foreach (var baseDir in servicesBases)
        {
            var candidate = Path.Combine(baseDir, sn.TypedValue);
            if (Directory.Exists(candidate))
            {
                foundBase = baseDir;
                installPath = candidate;
                break;
            }
        }

        if (foundBase is null)
        {
            AnsiConsole.MarkupLine($"[red]Service [white]{sn.TypedValue}[/] not found in any services directory.[/]");
            return 1;
        }

        var rollbackDir = Path.Combine(foundBase, ".rollback", sn.TypedValue);

        if (!Directory.Exists(rollbackDir))
        {
            AnsiConsole.MarkupLine($"[red]No rollback snapshots found for[/] [white]{sn.TypedValue}[/].");
            AnsiConsole.MarkupLine("[grey]Snapshots are created automatically during each deploy.[/]");
            return 1;
        }

        var snapshots = Directory.GetDirectories(rollbackDir)
            .Select(Path.GetFileName)
            .Where(s => s != null)
            .Cast<string>()
            .OrderByDescending(s => s) // newest first (yyyyMMdd-HHmmss sorts lexicographically)
            .ToList();

        if (snapshots.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Snapshot directory exists but is empty for[/] [white]{sn.TypedValue}[/].");
            return 1;
        }

        // ── Resolve target snapshot ───────────────────────────────────────────
        string chosen;
        if (targetSnap != null)
        {
            if (!snapshots.Contains(targetSnap.TypedValue))
            {
                AnsiConsole.MarkupLine($"[red]Snapshot not found:[/] {targetSnap.TypedValue}");
                AnsiConsole.MarkupLine("[grey]Available snapshots:[/]");
                foreach (var s in snapshots)
                    AnsiConsole.MarkupLine($"[white]{s}[/]");
                return 1;
            }
            chosen = targetSnap.TypedValue;
        }
        else
        {
            // Interactive picker
            AnsiConsole.MarkupLine($"[bold]Available snapshots for [green]{sn.Value}[/]:[/]\n");

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]#[/]")
                .AddColumn("[grey]Snapshot[/]")
                .AddColumn("[grey]Approx. date[/]")
                .AddColumn("[grey]Size[/]");

            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var snapDir = Path.Combine(rollbackDir, snap);
                long sizeMb = DirSize(snapDir) / 1024 / 1024;

                // Parse stamp: yyyyMMdd-HHmmss
                string friendly = snap;
                if (DateTime.TryParseExact(snap, "yyyyMMdd-HHmmss",
                    null, System.Globalization.DateTimeStyles.None, out var dt))
                    friendly = dt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

                table.AddRow(
                    $"[grey]{i + 1}[/]",
                    $"[white]{snap}[/]",
                    friendly,
                    $"{sizeMb} MB"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            chosen = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select snapshot to restore:[/]")
                    .AddChoices(snapshots)
                    .HighlightStyle(Style.Parse("cyan bold")));
        }

        var chosenPath = Path.Combine(rollbackDir, chosen);

        // ── Confirm ───────────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"\n[yellow]This will stop [white]{sn.Value}[/], replace the current install[/]");
        AnsiConsole.MarkupLine($"[yellow]with snapshot [white]{chosen}[/], and restart the service.[/]\n");

        var confirm = AnsiConsole.Confirm("[yellow]Proceed with rollback?[/]", defaultValue: false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Rollback cancelled.[/]");
            return 0;
        }

        // ── Execute rollback ──────────────────────────────────────────────────
        AnsiConsole.Status()
           .Spinner(Spinner.Known.Dots)
           .SpinnerStyle(Style.Parse("yellow"))
           .StartAsync("Rolling back...", async ctx =>
           {
               ctx.Status("Stopping service...");
               Shell($"systemctl stop {sn.Value}");
               await Task.Delay(1500);

               ctx.Status("Replacing install directory...");
               Shell($"rm -rf {installPath}");
               Shell($"cp -a {chosenPath} {installPath}");
               await Task.Delay(500);

               ctx.Status("Reloading systemd and starting service...");
               Shell("systemctl daemon-reload");
               Shell($"systemctl start {sn.Value}");
               await Task.Delay(3000);
           });

        // ── Verify service came back up ───────────────────────────────────────
        var (_, activeOut) = Shell($"systemctl is-active {sn.Value}");
        var isActive = activeOut.Trim() == "active";

        if (isActive)
        {
            AnsiConsole.Write(new Panel(
                    $"[bold green]✓[/] [white]{sn.Value}[/] rolled back to [white]{chosen}[/] and is now active.")
                .Header("[bold green] Rollback Successful [/]")
                .BorderColor(Color.Green));
        }
        else
        {
            AnsiConsole.Write(new Panel(
                    $"[bold red]✗[/] Service did not return to active state after rollback.\n")
                .Header("[bold red] Rollback Warning [/]")
                .BorderColor(Color.Red));
            return 1;
        }

        return 0;
    }

    private static long DirSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
    }

    private static (int, string) Shell(string cmd)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output);
        }
        catch { return (-1, string.Empty); }
    }
}

