using System;
using Spectre.Console;

namespace bangkactl.Commands;


public static class ListCommand
{
    private static readonly string ServicesBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "bangkasvcs");

    public static int Load()
    {
        AnsiConsole.MarkupLine("[bold]Managed Web Services[/]\n");

        if (!Directory.Exists(ServicesBase))
        {
            AnsiConsole.MarkupLine($"[yellow]No services directory found at {ServicesBase}[/]");
            AnsiConsole.MarkupLine("[grey]Deploy at least one package first.[/]");
            return 0;
        }

        var services = Directory.GetDirectories(ServicesBase)
            .Where(d => !Path.GetFileName(d).StartsWith('.')) // skip .rollback
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();

        if (services.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services installed yet.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Service[/]")
            .AddColumn("[grey]Active[/]")
            .AddColumn("[grey]Enabled[/]")
            .AddColumn("[grey]Version[/]")
            .AddColumn("[grey]Port[/]")
            .AddColumn("[grey]Snapshots[/]");

        foreach (var svc in services)
        {
            var installPath = Path.Combine(ServicesBase, svc);
            var rollbackDir = Path.Combine(ServicesBase, ".rollback", svc);

            // systemctl status
            var (_, activeOut)  = Shell($"systemctl is-active {svc} 2>/dev/null");
            var (_, enabledOut) = Shell($"systemctl is-enabled {svc} 2>/dev/null");
            var active  = activeOut.Trim();
            var enabled = enabledOut.Trim();

            // Read metadata if present
            var metaPath = Path.Combine(installPath, "..", $"{svc}.meta");
            string version = "—";
            string port    = "—";

            // Try reading from the systemd unit file (reliable fallback)
            var (_, unitOut) = Shell($"systemctl show {svc} --property=Environment --no-pager 2>/dev/null");
            var portMatch = System.Text.RegularExpressions.Regex.Match(
                unitOut, @"ASPNETCORE_URLS=http://[^:]+:(\d+)");
            if (portMatch.Success)
                port = portMatch.Groups[1].Value;

            // Count rollback snapshots
            int snapCount = 0;
            if (Directory.Exists(rollbackDir))
                snapCount = Directory.GetDirectories(rollbackDir).Length;

            var activeColor  = active  == "active"  ? "green"  : active == "inactive" ? "yellow" : "red";
            var enabledColor = enabled == "enabled"  ? "green"  : "grey";

            table.AddRow(
                $"[bold white]{Markup.Escape(svc)}[/]",
                $"[{activeColor}]{Markup.Escape(active)}[/]",
                $"[{enabledColor}]{Markup.Escape(enabled)}[/]",
                version,
                port,
                snapCount > 0 ? $"[grey]{snapCount}[/]" : "[dim]0[/]"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{services.Count} service(s) found in {ServicesBase}[/]");

        return 0;
    }

    private static (int, string) Shell(string cmd)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "/bin/bash",
                Arguments              = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            })!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output);
        }
        catch { return (-1, string.Empty); }
    }
}
