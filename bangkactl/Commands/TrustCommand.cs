using System;
using ArgSharp;
using ArgSharp.Args;
using bangkactl.Objects;
using Spectre.Console;

namespace bangkactl.Commands;

internal class TrustCommand
{
    internal static void Load(ArgInvoke argInvoke)
    {
        ArgInvoke? verifyTrustArg = null;
        argInvoke.AddArgumentAction(["status"], () =>
         {
             Environment.Exit(ShowStatus());
         }, "Show whether trust enforcement is active").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["init"], () =>
        {
            Environment.Exit(InitTrust(force: false));
        }, "Generate server trust key pair and enable signature enforcement").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["rotate"], () =>
        {
            Environment.Exit(InitTrust(force: true));
        }, "Rotates server trust key pair (invalidates packages that uses the current trust key)").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["show"], () =>
        {
            Environment.Exit(ShowKey());
        }, "Print the trusted public key PEM (copy to master)").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["revoke"], () =>
        {
            Environment.Exit(RevokeTrust());
        }, "Disable trust enforcement (revert to unsigned-allowed mode)").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        verifyTrustArg = argInvoke.AddArgumentAction(["verify"], () =>
        {
            Environment.Exit(VerifyPackage(verifyTrustArg!));
        }, "Disable trust enforcement (revert to unsigned-allowed mode)");
        verifyTrustArg.AddArgument<string>(["--path"], "<path to file>", isRequired: true);
        verifyTrustArg.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.ShowError;

    }


    private static int ShowStatus()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Setting[/]")
            .AddColumn("[grey]Value[/]");

        if (TrustStore.IsInitialized)
        {
            var fp = TrustStore.GetFingerprint() ?? "—";
            table.AddRow("Trust enforcement", "[bold green]ACTIVE[/]");
            table.AddRow("Trusted key path", TrustStore.PubKeyPath);
            table.AddRow("Fingerprint", fp);
            table.AddRow("Effect", "[white]Packages without valid signature will be rejected[/]");
        }
        else
        {
            table.AddRow("Trust enforcement", "[yellow]INACTIVE[/]");
            table.AddRow("Trusted key path", $"[grey]{TrustStore.PubKeyPath} (not found)[/]");
            table.AddRow("Effect", "[grey]All packages accepted (signed or unsigned)[/]");
            table.AddRow("To enable", "[grey]Run: bangkactl trust init[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }
    private static int InitTrust(bool force)
    {
        var verb = force ? "Rotating" : "Initializing";
        AnsiConsole.MarkupLine($"[bold cyan]{verb} server trust key...[/]\n");

        if (TrustStore.IsInitialized && !force)
        {
            AnsiConsole.MarkupLine("[yellow]Trust key already exists.[/]");
            AnsiConsole.MarkupLine("[grey]Use [white]trust rotate[/] to replace it (this invalidates existing signed packages).[/]");
            return 1;
        }

        if (force && TrustStore.IsInitialized)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: rotating the trust key will invalidate all packages signed with the old key.[/]");
            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 0;
            }
        }

        try
        {
            var (pubPem, fingerprint) = TrustStore.GenerateKeys(force);

            AnsiConsole.Write(new Panel(
                    $"[grey]Fingerprint:[/] [white]{fingerprint}[/]")
                .Header("[bold green] Trust Key Generated [/]")
                .BorderColor(Color.Green));
            AnsiConsole.MarkupLine("\n[grey]Public key (PEM):[/]");
            AnsiConsole.MarkupLine($"[dim]{pubPem}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Steps to export:[/]");
            AnsiConsole.MarkupLine("  1. Copy the public key below to your master machine:");
            AnsiConsole.MarkupLine($"     [grey]scp root@<server>:{TrustStore.PubKeyPath} ~/.bangka/server-trusted.pub[/]");
            AnsiConsole.MarkupLine("  2. Sign packages on the master with the matching private key:");
            AnsiConsole.MarkupLine("     [grey]bangka build ... --sign --signing-key ~/.bangka/signing.key[/]");
            AnsiConsole.MarkupLine("  3. The signing.key on master must correspond to this trusted.pub on the server.");


            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]HEADS UP!:[/]");
            AnsiConsole.MarkupLine("[bold]Please keep the public key safe.[/]");

        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {ex.Message}");
            return 1;
        }

        return 0;
    }
    private static int ShowKey()
    {
        if (!TrustStore.IsInitialized)
        {
            AnsiConsole.MarkupLine("[yellow]No trust key installed. Run:[/] bangkactl trust init");
            return 1;
        }

        var pem = TrustStore.GetPublicKeyPem()!;
        var fp = TrustStore.GetFingerprint() ?? "—";

        AnsiConsole.MarkupLine($"[grey]Fingerprint:[/] [white]{fp}[/]\n");
        AnsiConsole.MarkupLine("[grey]Public key (PEM) — copy this to master as ~/.bangka/signing.pub:[/]\n");
        AnsiConsole.WriteLine(pem);
        return 0;
    }

    private static int RevokeTrust()
    {
        if (!TrustStore.IsInitialized)
        {
            AnsiConsole.MarkupLine("[grey]Trust enforcement is not active — nothing to remove.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]This will disable signature enforcement on this server.[/]");
        AnsiConsole.MarkupLine("[yellow]All packages (signed or unsigned) will be accepted after this.[/]");
        if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }

        foreach (var path in new[] { TrustStore.PubKeyPath, TrustStore.PrivKeyPath, TrustStore.FingerprintPath })
            if (File.Exists(path)) File.Delete(path);

        AnsiConsole.MarkupLine("[green]Trust enforcement removed. Server will now accept unsigned packages.[/]");
        return 0;
    }

    private static int VerifyPackage(ArgInvoke arg)
    {
        var pathArg = arg.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--path"));

        if (pathArg is null)
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] bangkactl trust verify <package.bangka>");
            return 1;
        }

        if (!TrustStore.IsInitialized)
        {
            AnsiConsole.MarkupLine("[yellow]No trust key installed — cannot verify.[/]");
            AnsiConsole.MarkupLine("[grey]Run: bangkactl trust init[/]");
            return 1;
        }

        var pkgPath = pathArg.Value;
        var sigPath = pkgPath + ".sig";

        if (!File.Exists(pkgPath))
        {
            AnsiConsole.MarkupLine($"[red]Package not found:[/] {pkgPath}");
            return 1;
        }
        if (!File.Exists(sigPath))
        {
            AnsiConsole.MarkupLine($"[red]No signature file found:[/] {sigPath}");
            return 1;
        }

        var sigJson = File.ReadAllText(sigPath);
        var (ok, message) = TrustStore.VerifySignature(pkgPath, sigJson);

        if (ok)
            AnsiConsole.MarkupLine($"[bold green]✓[/] {Markup.Escape(message)}");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/] {Markup.Escape(message)}");

        return ok ? 0 : 1;
    }

}
