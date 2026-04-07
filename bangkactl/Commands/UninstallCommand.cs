using System;
using ArgSharp.Args;
using Spectre.Console;

namespace bangkactl.Commands;

public static class UninstallCommand
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
        argInvoke.AddArgument<string>(["--service-name"], isRequired: true, helpMsg: "Service name to uninstall");
        argInvoke.AddArgument<bool>(["--keep-snapshots"], helpMsg: "Keep rollback snapshots after uninstall");
    }

    public static int Run(ArgInvoke argInvoke)
    {
        var sn = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--service-name")) as ArgStore<string>;
        var keepSnapshots = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--keep-snapshots")) as ArgStore<bool>;

        if (sn is null)
        {
            AnsiConsole.MarkupLine("[red]Service name not specified.[/]");
            return 1;
        }

        var serviceName = sn.TypedValue;
        var servicesBases = ResolveServicesBases().ToList();

        string? foundBase = null;
        string? installPath = null;
        foreach (var baseDir in servicesBases)
        {
            var candidate = Path.Combine(baseDir, serviceName);
            if (Directory.Exists(candidate))
            {
                foundBase = baseDir;
                installPath = candidate;
                break;
            }
        }

        var serviceFile = $"/etc/systemd/system/{serviceName}.service";

        if (installPath is null && !File.Exists(serviceFile))
        {
            AnsiConsole.MarkupLine($"[yellow]Service [white]{serviceName}[/] does not appear to be installed.[/]");
            return 1;
        }

        var rollbackDir = foundBase is not null ? Path.Combine(foundBase, ".rollback", serviceName) : null;

        AnsiConsole.MarkupLine($"[bold yellow]Uninstalling [white]{serviceName}[/]...[/]\n");

        var (_, activeOut) = Shell($"systemctl is-active {serviceName} 2>/dev/null");
        if (activeOut.Trim() == "active")
        {
            AnsiConsole.MarkupLine("  [grey]Stopping service...[/]");
            Shell($"systemctl stop {serviceName}");
        }

        AnsiConsole.MarkupLine("  [grey]Disabling service...[/]");
        Shell($"systemctl disable {serviceName} 2>/dev/null");

        if (File.Exists(serviceFile))
        {
            AnsiConsole.MarkupLine("  [grey]Removing systemd unit file...[/]");
            Shell($"rm -f {serviceFile}");
            Shell("systemctl daemon-reload");
        }

        if (Directory.Exists(installPath))
        {
            AnsiConsole.MarkupLine($"[grey]Removing install directory...[/]");
            Shell($"rm -rf {installPath}");
        }

        bool shouldKeepSnapshots = keepSnapshots?.TypedValue == true;
        if (!shouldKeepSnapshots && Directory.Exists(rollbackDir))
        {
            var snapshotCount = Directory.GetDirectories(rollbackDir).Length;
            if (snapshotCount > 0)
            {
                AnsiConsole.MarkupLine($"[grey]Removing {snapshotCount} rollback snapshot(s)...[/]");
                Shell($"rm -rf {rollbackDir}");
            }
        }
        else if (Directory.Exists(rollbackDir))
        {
            AnsiConsole.MarkupLine("  [grey]Keeping rollback snapshots as requested.[/]");
        }

        var cloudflaredCfg = $"/etc/cloudflared/{serviceName}.yml";
        if (File.Exists(cloudflaredCfg))
        {
            AnsiConsole.MarkupLine("  [grey]Removing Cloudflare per-service config...[/]");
            Shell($"rm -f {cloudflaredCfg}");
            Shell("systemctl reload cloudflared 2>/dev/null || true");
        }

        var (_, stillActive) = Shell($"systemctl is-active {serviceName} 2>/dev/null");
        if (stillActive.Trim() != "inactive" && stillActive.Trim() != "failed" && stillActive.Trim() != "activating")
        {
            AnsiConsole.Write(new Panel(
                    $"[bold red]✗[/] Service unit may still be registered.\n" +
                    $"[grey]Run [white]systemctl reset-failed {serviceName}[/] manually if needed.[/]")
                .Header("[bold yellow] Uninstall Complete (with warnings) [/]")
                .BorderColor(Color.Yellow));
        }
        else
        {
            AnsiConsole.Write(new Panel(
                    $"[bold green]✓[/] [white]{serviceName}[/] has been fully uninstalled.")
                .Header("[bold green] Uninstall Complete [/]")
                .BorderColor(Color.Green));
        }

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
