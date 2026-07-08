using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>Tails a service's systemd journal on a remote host (over SSH).</summary>
public static class LogsCommand
{
    public static void Load(ArgInvoke invoke)
    {
        Arguments.LoadConnectionArgs(invoke);
        invoke.AddArgument(["--lines", "-n"], defaultValue: 50, helpMsg: "Number of log lines to show.");
        invoke.AddArgument(["--follow", "-f"], defaultValue: false, helpMsg: "Stream live log output.");
        invoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name");
    }

    public static int Run(ArgInvoke invoke)
    {
        var vals = invoke.GetArgStoreValues();
        var snArg = vals.SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        var follow = vals.SingleOrDefault(a => a.Parameters.Contains("--follow") || a.Parameters.Contains("-f")) as ArgStore<bool>;
        var lines = vals.SingleOrDefault(a => a.Parameters.Contains("--lines") || a.Parameters.Contains("-n")) as ArgStore<int>;

        if (snArg is null || string.IsNullOrWhiteSpace(snArg.TypedValue))
        {
            AnsiConsole.MarkupLine("[red]--service-name is required.[/]");
            return 1;
        }
        var serviceName = snArg.TypedValue;
        try { ShellUtil.ValidateServiceName(serviceName); }
        catch (ArgumentException ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }
        var n = lines?.TypedValue ?? 50;

        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using var session = target.Connect();
        var q = ShellUtil.Quote(serviceName);

        var (checkCode, _, _) = session.Run($"systemctl status {q} --no-pager 2>/dev/null | head -1");
        if (checkCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Service not found:[/] {Markup.Escape(serviceName)}");
            AnsiConsole.MarkupLine("[grey]Run [white]bangka list[/] to see available services.[/]");
            return 1;
        }

        AnsiConsole.Write(new Rule($"[bold]Journal — [green]{Markup.Escape(serviceName)}[/][/]").RuleStyle("grey"));

        if (follow?.TypedValue == true)
        {
            AnsiConsole.MarkupLine("Following journal output. Press Ctrl+C to stop.\n");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            session.StreamCommand(
                $"journalctl -u {q} -f -n {n} --no-pager --output short-precise",
                line => AnsiConsole.MarkupLine(Colorize(line)),
                cts.Token);
            return 0;
        }

        var (_, output, _) = session.Run(
            $"journalctl -u {q} -n {n} --no-pager --output short-precise 2>&1");
        if (string.IsNullOrWhiteSpace(output))
        {
            AnsiConsole.MarkupLine("[grey]No journal entries found for this service.[/]");
        }
        else
        {
            foreach (var line in output.Split('\n'))
                AnsiConsole.MarkupLine(Colorize(line));
        }
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.MarkupLine($"Showing last {n} lines. Use --follow / -f to stream live output.");
        return 0;
    }

    private static string Colorize(string line)
    {
        var esc = Markup.Escape(line);
        return line switch
        {
            _ when line.Contains("ERROR") || line.Contains("error") || line.Contains("Failed") || line.Contains("fail") => $"[red]{esc}[/]",
            _ when line.Contains("WARN") || line.Contains("warn") => $"[yellow]{esc}[/]",
            _ when line.Contains("Started") || line.Contains("started") => $"[green]{esc}[/]",
            _ => $"[grey]{esc}[/]"
        };
    }
}
