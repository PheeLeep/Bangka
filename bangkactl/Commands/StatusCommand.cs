using System;
using ArgSharp.Args;
using Spectre.Console;

namespace bangkactl.Commands;

public static class StatusCommand
{
    public static void Load(ArgInvoke argInvoke)
    {
        argInvoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name");
    }

    public static int Run(ArgInvoke argInvoke)
    {
        var sn = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        if (sn is null)
        {
            AnsiConsole.MarkupLine("[red]Service name not found[/]");
            return 1;
        }


        AnsiConsole.MarkupLine($"[bold]Service status:[/] [green]{sn.Value}[/]\n");


        // Gather systemd status info
        var (isActive, activeOut) = RunShell($"systemctl is-active {sn.Value}");
        var (isEnabled, enabledOut) = RunShell($"systemctl is-enabled {sn.Value}");
        var (_, statusOut) = RunShell($"systemctl show {sn.Value} --property=MainPID,MemoryCurrent,ActiveEnterTimestamp,NRestarts --no-pager");

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

        // Show install path info if available
        var installPath = $"/home/root/bangkasvcs/{sn.Value}";
        var (pathExists, _) = RunShell($"test -d {installPath} && echo 'yes'");
        if (pathExists == 0)
        {
            AnsiConsole.MarkupLine($"\n[grey]Install path:[/] [white]{installPath}[/]");

            var (_, metaOut) = RunShell($"cat {installPath}/../.rollback/{sn.Value} 2>/dev/null | ls -1t | head -3");
            if (!string.IsNullOrWhiteSpace(metaOut))
                AnsiConsole.MarkupLine($"[grey]Snapshots available:[/]\n{Markup.Escape(metaOut)}");
        }

        return 0;
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
        if (string.IsNullOrWhiteSpace(raw) || !ulong.TryParse(raw, out var bytes))
            return "—";
        return bytes >= 1_048_576
            ? $"{bytes / 1_048_576.0:F1} MB"
            : $"{bytes / 1024.0:F1} KB";
    }
}

