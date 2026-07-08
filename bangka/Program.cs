using ArgSharp;
using ArgSharp.Args;
using bangka.Commands;
using bangka.Objects;
using Spectre.Console;

namespace bangka;

public class Program
{
    public static void Main(string[] args)
    {
        ArgSharpClass.Init("bangka",
                            "Bangka Web Deployment",
                            "A web service deployment orchestrator for ASP.NET",
                            epilog:
                            "bangka is now the single tool — bangkactl has been retired and its commands\n" +
                            "run over SSH from here:\n" +
                            "  bangkactl deploy-user init   ->  bangka server init\n" +
                            "  bangkactl list / status      ->  bangka list / status\n" +
                            "  bangkactl logs / rollback    ->  bangka logs / rollback\n" +
                            "  bangkactl uninstall / trust  ->  bangka uninstall / trust\n\n" +
                            "First-time setup of a host:  bangka server init --ssh-host <host> --ssh-user <admin>");
        ArgSharpClass.IgnoreConflictArgument = true;

        ArgInvoke? profileInvoke = null;
        ArgInvoke? verifyInvoke = null;
        ArgInvoke? buildInvoke = null;
        ArgInvoke? deployInvoke = null;
        profileInvoke = ArgSharpClass.AddArgumentAction(["profile"],
                                                         null,
                                                         "Manage deployment profiles (create, list, show, delete)");
        profileInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        ProfileCommand.Load(profileInvoke!);

        verifyInvoke = ArgSharpClass.AddArgumentAction(["verify"],
                                                        () => Environment.Exit(RunVerify(verifyInvoke!)),
                                                        "Verify the signature of a .bangka file");

        verifyInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        verifyInvoke.AddArgument<string>(["--pub-key"], helpMsg: "Path to public key PEM file (optional, defaults to local trust store)", isRequired: false);
        verifyInvoke.AddArgument<string>(["--package"], helpMsg: "Package Path", isRequired: true);

        buildInvoke = ArgSharpClass.AddArgumentAction(["build"],
                                                        () =>
                                                        {
                                                            try
                                                            {
                                                                BuildCommand.Run(buildInvoke!);
                                                                Environment.Exit(0);
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                                                                Environment.Exit(1);
                                                            }
                                                        },
                                                        "Build a .bangka package from a directory");

        BuildCommand.Load(buildInvoke!);

        deployInvoke = ArgSharpClass.AddArgumentAction(["deploy"],
                                                       () =>
                                                       {
                                                           try
                                                           {
                                                               DeployCommand.Run(deployInvoke!);
                                                               Environment.Exit(0);
                                                           }
                                                           catch (Exception ex)
                                                           {
                                                               AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}\n{Markup.Escape(ex.StackTrace!)}");
                                                               Environment.Exit(1);
                                                           }
                                                       },
                                                       "Deploy a .bangka package to a remote host (builds it inline if --package is omitted)");

        DeployCommand.Load(deployInvoke!);

        // ── Remote control commands (formerly bangkactl, now over SSH) ──────
        ArgInvoke? listInvoke = null, statusInvoke = null, logsInvoke = null,
                   rollbackInvoke = null, uninstallInvoke = null;

        listInvoke = ArgSharpClass.AddArgumentAction(["list"],
            () => Environment.Exit(Guard(() => ListCommand.Run(listInvoke!))),
            "List services managed by Bangka on a remote host");
        ListCommand.Load(listInvoke);
        listInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        statusInvoke = ArgSharpClass.AddArgumentAction(["status"],
            () => Environment.Exit(Guard(() => StatusCommand.Run(statusInvoke!))),
            "Show health status of a deployed service");
        StatusCommand.Load(statusInvoke);

        logsInvoke = ArgSharpClass.AddArgumentAction(["logs"],
            () => Environment.Exit(Guard(() => LogsCommand.Run(logsInvoke!))),
            "Tail the systemd journal for a remote service");
        LogsCommand.Load(logsInvoke);

        rollbackInvoke = ArgSharpClass.AddArgumentAction(["rollback"],
            () => Environment.Exit(Guard(() => RollbackCommand.Run(rollbackInvoke!))),
            "Roll a service back to a previous snapshot");
        RollbackCommand.Load(rollbackInvoke);

        uninstallInvoke = ArgSharpClass.AddArgumentAction(["uninstall"],
            () => Environment.Exit(Guard(() => UninstallCommand.Run(uninstallInvoke!))),
            "Uninstall a deployed service from a remote host");
        UninstallCommand.Load(uninstallInvoke);

        TrustCommand.Load(ArgSharpClass.AddArgumentAction(["trust"], null,
            "Manage server-side signature enforcement"));
        ServerCommand.Load(ArgSharpClass.AddArgumentAction(["server"], null,
            "Provision and manage a remote host (create deploy user, sudoers, keys)"));

        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }

    /// <summary>Runs a command body, converting exceptions into a clean error + exit code.</summary>
    static int Guard(Func<int> body)
    {
        try { return body(); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    static int RunVerify(ArgInvoke invoke)
    {
        try
        {
            string package = invoke.GetValue<string>("--package");
            string? pubKey = invoke.GetValue<string>("--pub-key");

            var (ok, message) = PackageSigner.VerifyPackage(package, pubKey);

            if (ok)
                AnsiConsole.MarkupLine($"[bold green][[✓]][/] {Markup.Escape(message)}");
            else
                AnsiConsole.MarkupLine($"[bold red][[X]][/] {Markup.Escape(message)}");

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during verification:[/] {Markup.Escape(ex.Message)}\n{Markup.Escape(ex.StackTrace!)}");
            return 1;
        }
    }

}