using System;

namespace bangkactl.Commands;

using ArgSharp.Args;
using Spectre.Console;
public static class LogsCommand
{
    internal static void Load(ArgInvoke argInvoke)
    {
        argInvoke.AddArgument(["--lines", "-n"], defaultValue: 50);
        argInvoke.AddArgument(["--follow", "-f"], defaultValue: false);
        argInvoke.AddArgument(["--service-name"], defaultValue: "", isRequired: true);
    }

    internal static void Run(ArgInvoke argInvoke)
    {
        var paramS = argInvoke.GetArgStoreValues();
        var follower = paramS.SingleOrDefault(a => a.Parameters.Contains("--follow") || a.Parameters.Contains("-f")) as ArgStore<bool>;
        var lines = paramS.SingleOrDefault(a => a.Parameters.Contains("--lines") || a.Parameters.Contains("-n")) as ArgStore<int>;
        if (paramS.SingleOrDefault(a => a.Parameters.Contains("--service-name")) is ArgStore<string> sn)
        {
            var (checkCode, _) = Shell($"systemctl status {sn.TypedValue} --no-pager 2>/dev/null | head -1");
            if (checkCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Service not found:[/] {Markup.Escape(sn.TypedValue)}");
                AnsiConsole.MarkupLine("[grey]Run [white]bangkactl list[/] to see available services.[/]");
                Environment.Exit(1);
                return;
            }

            AnsiConsole.Write(new Rule($"[bold]Journal — [green]{Markup.Escape(sn.TypedValue)}[/][/]").RuleStyle("grey"));

            if (follower != null && follower.TypedValue)
            {
                AnsiConsole.MarkupLine("[grey]Following journal output. Press Ctrl+C to stop.[/]\n");

                // Stream follow mode — hand off to journalctl process directly
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "journalctl",
                    Arguments = $"-u {sn.TypedValue} -f -n {lines?.TypedValue} --no-pager --output short-precise",
                    UseShellExecute = false
                })!;

                // Respect Ctrl+C gracefully
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    try { proc.Kill(); } catch { }
                };

                proc.WaitForExit();
            }
            else
            {
                var (_, output) = Shell(
                    $"journalctl -u {sn.TypedValue} -n {lines?.TypedValue} --no-pager --output short-precise 2>&1");

                if (string.IsNullOrWhiteSpace(output))
                {
                    AnsiConsole.MarkupLine("[grey]No journal entries found for this service.[/]");
                }
                else
                {
                    // Colorize log levels inline
                    foreach (var line in output.Split('\n'))
                    {
                        var colored = line switch
                        {
                            var l when l.Contains("ERROR") || l.Contains("error") => $"[red]{Markup.Escape(l)}[/]",
                            var l when l.Contains("WARN") || l.Contains("warn") => $"[yellow]{Markup.Escape(l)}[/]",
                            var l when l.Contains("INFO") || l.Contains("info") => $"[white]{Markup.Escape(l)}[/]",
                            var l when l.Contains("DEBUG") || l.Contains("debug") => $"[grey]{Markup.Escape(l)}[/]",
                            var l when l.Contains("fail") || l.Contains("Failed") => $"[red]{Markup.Escape(l)}[/]",
                            var l when l.Contains("started") || l.Contains("Started") => $"[green]{Markup.Escape(l)}[/]",
                            _ => $"[grey]{Markup.Escape(line)}[/]"
                        };
                        AnsiConsole.MarkupLine(colored);
                    }
                }

                AnsiConsole.Write(new Rule().RuleStyle("grey"));
                AnsiConsole.MarkupLine($"[grey]Showing last {lines?.TypedValue} lines. Use --follow / -f to stream live output.[/]");
            }

        }
        else
        {
            Environment.Exit(1);
            return;
        }
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

