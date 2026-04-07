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

private static void WaitThenRedact(int seconds, string fingerprint)
    {
        using var cts = new CancellationTokenSource();

        // Ctrl+C handler — triggers immediate redact
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // don't terminate the process
            cts.Cancel();
        };

        // Countdown display + keypress listener
        var countdown = Task.Run(() =>
        {
            for (int i = seconds; i > 0; i--)
            {
                if (cts.Token.IsCancellationRequested) break;
                Console.Write($"\r[grey]Clearing in {i}s... (press any key to clear now)[/]  ");
                Thread.Sleep(1000);
            }
        }, cts.Token);

        var keypress = Task.Run(() =>
        {
            if (Console.KeyAvailable) Console.ReadKey(intercept: true); // flush any buffered key
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(50);
            }
        }, cts.Token);

        try { Task.WhenAny(countdown, keypress).Wait(); } catch { }
        cts.Cancel(); // ensure both tasks stop

        Console.WriteLine();
        Console.Clear();
        AnsiConsole.MarkupLine("[green]Screen cleared.[/]");
        AnsiConsole.MarkupLine($"[grey]Fingerprint: {fingerprint}[/]");
        AnsiConsole.MarkupLine("[grey]Keys are stored at:[/]");
        AnsiConsole.MarkupLine($"[grey]  Private: {TrustStore.PrivKeyPath}[/]");
        AnsiConsole.MarkupLine($"[grey]  Public:  {TrustStore.PubKeyPath}[/]");
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
            var privPem = TrustStore.GetPrivateKeyPem()!;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Copy this keys to your master machine:[/]");
            AnsiConsole.MarkupLine("\n[grey]Private key (PEM) — for signing on master:[/]");
            AnsiConsole.MarkupLine($"[yellow]{privPem}[/]");
            AnsiConsole.MarkupLine("\n[grey]Public key (PEM) — for verification on master:[/]");
            AnsiConsole.MarkupLine($"[dim]{pubPem}[/]");
            AnsiConsole.MarkupLine($"\n[grey]Fingerprint:[/] [white]{fingerprint}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Usage on master:[/]");
            AnsiConsole.MarkupLine("  1. Save the private key above as ~/.bangka/signing.key on your master machine");
            AnsiConsole.MarkupLine("  2. Save the public key above as ~/.bangka/signing.pub on your master machine");
            AnsiConsole.MarkupLine("  3. Sign packages on the master with:");
            AnsiConsole.MarkupLine("     [grey]bangka build ... --sign --signing-key ~/.bangka/signing.key[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold red]WARNING:[/]");
            AnsiConsole.MarkupLine("[bold red]Keep the private key secret and secure![/]");
            AnsiConsole.MarkupLine("[grey]Screen will clear in 30 seconds, or press any key / Ctrl+C to clear immediately.[/]");

            WaitThenRedact(30, fingerprint);

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

        var pubPem = TrustStore.GetPublicKeyPem()!;
        var privPem = TrustStore.GetPrivateKeyPem()!;
        var fp = TrustStore.GetFingerprint() ?? "—";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Copy these keys to your master machine:[/]");
        AnsiConsole.MarkupLine("\n[grey]Private key (PEM) — for signing on master:[/]");
        AnsiConsole.MarkupLine($"[yellow]{privPem}[/]");
        AnsiConsole.MarkupLine("\n[grey]Public key (PEM) — for verification on master:[/]");
        AnsiConsole.MarkupLine($"[dim]{pubPem}[/]");
        AnsiConsole.MarkupLine($"\n[grey]Fingerprint:[/] [white]{fp}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold red]WARNING:[/]");
        AnsiConsole.MarkupLine("[bold red]Keep the private key secret and secure![/]");
        AnsiConsole.MarkupLine("[grey]Screen will clear in 30 seconds, or press any key / Ctrl+C to clear immediately.[/]");

        WaitThenRedact(30, fp);
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
            AnsiConsole.MarkupLine("[red]Usage:[/] bangkactl trust verify --path <package.bangka>");
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
