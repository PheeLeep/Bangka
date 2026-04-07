using System;
using Spectre.Console;

namespace bangkactl.Commands;


public static class ListCommand
{
    private static IEnumerable<string> ResolveServicesBases()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always include the current user's own dir
        var ownDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "bangkasvcs");
        candidates.Add(ownDir);

        // If running as root / sudo, also check the invoking user's dir
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser))
            candidates.Add($"/home/{sudoUser}/bangkasvcs");

        // Always scan all /home/*/bangkasvcs that exist
        if (Directory.Exists("/home"))
        {
            foreach (var d in Directory.GetDirectories("/home"))
                candidates.Add(Path.Combine(d, "bangkasvcs"));
        }

        return candidates.Where(Directory.Exists);
    }

    public static int Load()
    {
        AnsiConsole.MarkupLine("[bold]Managed Web Services[/]\n");

        var servicesBases = ResolveServicesBases().ToList();
        if (servicesBases.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services directory found.[/]");
            AnsiConsole.MarkupLine("[grey]Deploy at least one package first.[/]");
            return 0;
        }

        // Collect all services across all bases, keyed by service name to avoid duplicates
        var services = servicesBases
            .SelectMany(base_ => Directory.GetDirectories(base_)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Select(d => (Base: base_, Dir: d, Name: Path.GetFileName(d)!)))
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()) // deduplicate by name
            .OrderBy(s => s.Name)
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

        foreach (var (servicesBase, _, svc) in services)
        {
            var installPath = Path.Combine(servicesBase, svc);
            var rollbackDir = Path.Combine(servicesBase, ".rollback", svc);

            // systemctl status
            var (_, activeOut) = Shell($"systemctl is-active {svc} 2>/dev/null");
            var (_, enabledOut) = Shell($"systemctl is-enabled {svc} 2>/dev/null");
            var active = activeOut.Trim();
            var enabled = enabledOut.Trim();

            // Read metadata if present
            var metaPath = Path.Combine(installPath, "..", $"{svc}.meta");
            string version = "—";
            string port = "—";

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

            var activeColor = active == "active" ? "green" : active == "inactive" ? "yellow" : "red";
            var enabledColor = enabled == "enabled" ? "green" : "grey";

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
        var basesDisplay = string.Join(", ", servicesBases);
        AnsiConsole.MarkupLine($"\n[grey]{services.Count} service(s) found across: {basesDisplay}[/]");

        return 0;
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
