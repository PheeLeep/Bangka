using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>Shows systemd health for a service on a remote host (over SSH).</summary>
public static class StatusCommand
{
    public static void Load(ArgInvoke invoke)
    {
        Arguments.LoadConnectionArgs(invoke);
        invoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name");
    }

    public static int Run(ArgInvoke invoke)
    {
        var snArg = invoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
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

        AnsiConsole.MarkupLine($"[bold]Service status:[/] [green]{Markup.Escape(serviceName)}[/] [grey]on {Markup.Escape(target.Host)}[/]\n");

        var (_, activeOut, _) = session.Run($"systemctl is-active {q}");
        var (_, enabledOut, _) = session.Run($"systemctl is-enabled {q}");
        var (_, showOut, _) = session.Run(
            $"systemctl show {q} --property=MainPID --property=MemoryCurrent --property=ActiveEnterTimestamp --property=NRestarts --no-pager");
        var props = ParseProperties(showOut);

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

        var basePath = session.ResolveServicesBase();
        var installPath = $"{basePath}/{serviceName}";
        var (instCode, _, _) = session.Run($"test -d {ShellUtil.Quote(installPath)} && echo yes");
        if (instCode == 0)
        {
            AnsiConsole.MarkupLine($"\n[grey]Install path:[/] [white]{Markup.Escape(installPath)}[/]");
            var (_, snapOut, _) = session.Run(
                $"ls -1 {ShellUtil.Quote($"{basePath}/.rollback/{serviceName}")} 2>/dev/null | sort -r | head -3");
            if (!string.IsNullOrWhiteSpace(snapOut))
                AnsiConsole.MarkupLine($"[grey]Latest snapshots:[/]\n{Markup.Escape(snapOut.Trim())}");
        }
        return 0;
    }

    private static Dictionary<string, string> ParseProperties(string raw)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) result[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static string FormatMemory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "[not set]") return "—";
        if (!ulong.TryParse(raw, out var bytes)) return "—";
        if (bytes == ulong.MaxValue) return "[grey](accounting disabled)[/]";
        if (bytes == 0) return "0 B";
        return bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB" : $"{bytes / 1024.0:F1} KB";
    }
}
