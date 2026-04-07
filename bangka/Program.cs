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
                            "A web service deployment orchestrator for ASP.NET");
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
                                                       "Deploy a .bangka package to a remote host");

        DeployCommand.Load(deployInvoke!);

        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }

    static int RunVerify(ArgInvoke invoke)
    {
        string package = invoke.GetValue<string>("--package");
        string? pubKey = invoke.GetValue<string>("--pub-key");

        var (ok, message) = PackageSigner.VerifyPackage(package, pubKey);

        if (ok)
            AnsiConsole.MarkupLine($"[bold green]✓[/] {Markup.Escape(message)}");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/] {Markup.Escape(message)}");

        return ok ? 0 : 1;
    }

}