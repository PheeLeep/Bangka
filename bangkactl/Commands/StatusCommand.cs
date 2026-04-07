using System;
using ArgSharp.Args;
using Spectre.Console;

namespace bangkactl.Commands;

public static class StatusCommand
{
    public static void Load(ArgInvoke argInvoke)
    {
        argInvoke.AddArgument(["--service-name"], defaultValue: "", isRequired: true, helpMsg: "Service name");
    }

    public static int Run(ArgInvoke argInvoke)
    {
        if (argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--service-name")) is not ArgStore<string> sn
            || string.IsNullOrWhiteSpace(sn.TypedValue))
        {
            AnsiConsole.MarkupLine("[red]Service name not found[/]");
            return 1;
        }

        var serviceName = sn.TypedValue;
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            AnsiConsole.MarkupLine("[red]Service name cannot be empty[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Service status:[/] [green]{serviceName}[/]\n");

        // Gather systemd status info
        var (isActive, activeOut) = RunShell($"systemctl is-active {serviceName}");
        var (isEnabled, enabledOut) = RunShell($"systemctl is-enabled {serviceName}");
        var (_, statusOut) = RunShell($"systemctl show {serviceName} --property=MainPID --property=MemoryCurrent --property=ActiveEnterTimestamp --property=NRestarts --no-pager");

        var props = ParseProperties(statusOut);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Property[/]")
            .AddColumn("[grey]Value[/]");

        var activeColor = activeOut.Trim() == "active" ? "green" : "red";
        table.AddRow("Active", $"[{activeColor}]{Markup.Escape(activeOut.Trim())}[/]");
        table.AddRow("Enabled", Markup.Escape(enabledOut.Trim()));
        table.AddRow("PID", props.GetValueOrDefault("MainPID", "—"));
        table.AddRow("Restarts", props.GetValueOrDefault("NRestarts", "—"));
        table.AddRow("Memory", FormatMemory(props.GetValueOrDefault("MemoryCurrent", "")));
        table.AddRow("Started at", props.GetValueOrDefault("ActiveEnterTimestamp", "—"));

        AnsiConsole.Write(table);

        // Show install path info if available — search across all known bases
        var installPath = ResolveServicesBases()
            .Select(b => Path.Combine(b, serviceName))
            .FirstOrDefault(Directory.Exists);

        if (installPath != null)
        {
            AnsiConsole.MarkupLine($"\n[grey]Install path:[/] [white]{installPath}[/]");

            var rollbackDir = Path.Combine(installPath, "..", ".rollback", serviceName);
            var (_, metaOut) = RunShell($"ls -1t {rollbackDir} 2>/dev/null | head -3");
            if (!string.IsNullOrWhiteSpace(metaOut))
                AnsiConsole.MarkupLine($"[grey]Snapshots available:[/]\n{Markup.Escape(metaOut)}");
        }

        return 0;
    }

    private static IEnumerable<string> ResolveServicesBases()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "bangkasvcs"));

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser))
            candidates.Add($"/home/{sudoUser}/bangkasvcs");

        if (Directory.Exists("/home"))
            foreach (var d in Directory.GetDirectories("/home"))
                candidates.Add(Path.Combine(d, "bangkasvcs"));

        return candidates.Where(Directory.Exists);
    }

    private static (int, string) RunShell(string cmd)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static Dictionary<string, string> ParseProperties(string raw)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                result[line[..eq]] = line[(eq + 1)..];
        }
        return result;
    }

    private static string FormatMemory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "[not set]")
            return "—";
        if (!ulong.TryParse(raw, out var bytes))
            return "—";
        // uint64 max means memory accounting is not enabled
        if (bytes == ulong.MaxValue)
            return "[grey](accounting disabled)[/]";
        if (bytes == 0)
            return "0 B";
        return bytes >= 1_048_576
            ? $"{bytes / 1_048_576.0:F1} MB"
            : $"{bytes / 1024.0:F1} KB";
    }
}

