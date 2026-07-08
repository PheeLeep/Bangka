using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using bangka_lib.Objects;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>Lists services managed by Bangka on a remote host (over SSH).</summary>
public static class ListCommand
{
    public static void Load(ArgInvoke invoke) => Arguments.LoadConnectionArgs(invoke);

    public static int Run(ArgInvoke invoke)
    {
        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using var session = target.Connect();
        var basePath = session.ResolveServicesBase();

        AnsiConsole.MarkupLine($"[bold]Managed Web Services[/] [grey]on {Markup.Escape(target.Host)}[/]\n");

        var (lsCode, lsOut, _) = session.Run(
            $"ls -1 {ShellUtil.Quote(basePath)} 2>/dev/null");
        var services = lsOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith('.'))
            .OrderBy(s => s)
            .ToList();

        if (lsCode != 0 || services.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services installed yet.[/]");
            AnsiConsole.MarkupLine("[grey]Deploy a package first with [white]bangka deploy[/].[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Service[/]")
            .AddColumn("[grey]Active[/]")
            .AddColumn("[grey]Enabled[/]")
            .AddColumn("[grey]Version[/]")
            .AddColumn("[grey]Snapshots[/]");

        foreach (var svc in services)
        {
            var installPath = $"{basePath}/{svc}";
            var rollbackDir = $"{basePath}/.rollback/{svc}";

            var (_, active, _) = session.Run($"systemctl is-active {ShellUtil.Quote(svc)} 2>/dev/null");
            var (_, enabled, _) = session.Run($"systemctl is-enabled {ShellUtil.Quote(svc)} 2>/dev/null");
            active = active.Trim(); enabled = enabled.Trim();

            string version = "—";
            var (metaCode, metaXml, _) = session.Run(
                $"cat {ShellUtil.Quote($"{installPath}/.bangka-meta.xml")} 2>/dev/null");
            if (metaCode == 0 && !string.IsNullOrWhiteSpace(metaXml))
            {
                try
                {
                    var tmp = Path.GetTempFileName();
                    File.WriteAllText(tmp, metaXml);
                    version = PackageMetadata.Deserialize(tmp).Version;
                    File.Delete(tmp);
                    if (string.IsNullOrWhiteSpace(version)) version = "—";
                }
                catch { /* leave as — */ }
            }

            var (_, snapOut, _) = session.Run(
                $"ls -1 {ShellUtil.Quote(rollbackDir)} 2>/dev/null | wc -l");
            var snapCount = int.TryParse(snapOut.Trim(), out var n) ? n : 0;

            var activeColor = active == "active" ? "green" : active == "inactive" ? "yellow" : "red";
            var enabledColor = enabled == "enabled" ? "green" : "grey";

            table.AddRow(
                $"[bold white]{Markup.Escape(svc)}[/]",
                $"[{activeColor}]{Markup.Escape(active)}[/]",
                $"[{enabledColor}]{Markup.Escape(enabled)}[/]",
                Markup.Escape(version),
                snapCount > 0 ? $"[grey]{snapCount}[/]" : "[dim]0[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]{services.Count} service(s) in {Markup.Escape(basePath)}[/]");
        return 0;
    }
}
