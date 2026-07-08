using ArgSharp;
using ArgSharp.Args;
using bangka.Objects;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>
/// Manages server-side signature enforcement. Unlike the old bangkactl design,
/// the signing keypair is generated and kept ON THE MASTER (~/.bangka). Only the
/// public key + fingerprint are pushed to the server's /etc/bangka. The private
/// key never touches the server, and any legacy /etc/bangka/trusted.key is removed.
/// </summary>
public static class TrustCommand
{
    private const string RemoteDir = "/etc/bangka";
    private const string RemotePub = "/etc/bangka/trusted.pub";
    private const string RemoteFp = "/etc/bangka/trusted.fingerprint";
    private const string RemoteLegacyKey = "/etc/bangka/trusted.key";

    public static void Load(ArgInvoke trustInvoke)
    {
        ArgInvoke? statusInvoke = null, initInvoke = null, rotateInvoke = null, revokeInvoke = null;

        statusInvoke = trustInvoke.AddArgumentAction(["status"],
            () => Environment.Exit(ShowStatus(statusInvoke!)),
            "Show whether trust enforcement is active on the server");
        Arguments.LoadConnectionArgs(statusInvoke);
        statusInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        initInvoke = trustInvoke.AddArgumentAction(["init"],
            () => Environment.Exit(InitTrust(initInvoke!, force: false)),
            "Generate a local signing key (if needed) and enable enforcement on the server");
        Arguments.LoadConnectionArgs(initInvoke);
        initInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        rotateInvoke = trustInvoke.AddArgumentAction(["rotate"],
            () => Environment.Exit(InitTrust(rotateInvoke!, force: true)),
            "Rotate the local signing key and re-push the public key (invalidates old signatures)");
        Arguments.LoadConnectionArgs(rotateInvoke);
        rotateInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        revokeInvoke = trustInvoke.AddArgumentAction(["revoke"],
            () => Environment.Exit(RevokeTrust(revokeInvoke!)),
            "Disable enforcement on the server (accept unsigned packages again)");
        Arguments.LoadConnectionArgs(revokeInvoke);
        revokeInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        trustInvoke.AddArgumentAction(["show"],
            () => Environment.Exit(ShowLocalKey()),
            "Print the local public key and fingerprint used for signing")
            .ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
    }

    private static int ShowStatus(ArgInvoke invoke)
    {
        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using var session = target.Connect();
        var (code, _, _) = session.Run($"test -f {RemotePub} && echo yes");
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("[grey]Setting[/]").AddColumn("[grey]Value[/]");

        if (code == 0)
        {
            var (_, fp, _) = session.Run($"cat {RemoteFp} 2>/dev/null");
            table.AddRow("Trust enforcement", "[bold green]ACTIVE[/]");
            table.AddRow("Server", Markup.Escape(target.Host));
            table.AddRow("Fingerprint", string.IsNullOrWhiteSpace(fp) ? "—" : Markup.Escape(fp.Trim()));
            table.AddRow("Effect", "Packages without a valid signature are rejected");
        }
        else
        {
            table.AddRow("Trust enforcement", "[yellow]INACTIVE[/]");
            table.AddRow("Server", Markup.Escape(target.Host));
            table.AddRow("Effect", "[grey]All packages accepted (signed or unsigned)[/]");
            table.AddRow("To enable", "[grey]bangka trust init --ssh-host " + Markup.Escape(target.Host) + "[/]");
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static int InitTrust(ArgInvoke invoke, bool force)
    {
        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        // Ensure a local signing keypair exists (generate on first use, or on rotate).
        try
        {
            if (force || !File.Exists(PackageSigner.PrivateKeyPath))
            {
                if (force && File.Exists(PackageSigner.PrivateKeyPath) &&
                    !AnsiConsole.Confirm("[yellow]Rotating invalidates all packages signed with the current key. Continue?[/]", defaultValue: false))
                {
                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                    return 0;
                }
                PackageSigner.GenerateKeys(force);
                AnsiConsole.MarkupLine($"[green]Local signing key generated:[/] {PackageSigner.PrivateKeyPath}");
            }
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Key generation failed:[/] {Markup.Escape(ex.Message)}"); return 1; }

        var pubPem = File.ReadAllText(PackageSigner.PublicKeyPath).Trim();
        var fingerprint = PackageSigner.ComputeFingerprint(pubPem);

        using var session = target.Connect();
        try
        {
            session.RunPrivilegedOrThrow($"mkdir -p {RemoteDir}");
            session.UploadText(pubPem + "\n", "/tmp/bangka-trusted.pub");
            session.RunPrivilegedOrThrow("mv /tmp/bangka-trusted.pub " + RemotePub);
            session.RunPrivileged($"chmod 644 {RemotePub}");
            session.UploadText(fingerprint + "\n", "/tmp/bangka-trusted.fp");
            session.RunPrivilegedOrThrow("mv /tmp/bangka-trusted.fp " + RemoteFp);
            // Remove any legacy server-side private key from the old bangkactl design.
            session.RunPrivileged($"rm -f {RemoteLegacyKey}");
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Failed to push trust key:[/] {Markup.Escape(ex.Message)}"); return 1; }

        AnsiConsole.Write(new Panel(
                $"[bold green]✓[/] Trust enforcement enabled on [white]{Markup.Escape(target.Host)}[/]\n" +
                $"[grey]Fingerprint:[/] [white]{Markup.Escape(fingerprint)}[/]\n\n" +
                $"Sign packages with:\n  [grey]bangka build ... --sign --signing-key {PackageSigner.PrivateKeyPath}[/]")
            .Header("[bold green] Trust Active [/]").BorderColor(Color.Green));
        return 0;
    }

    private static int RevokeTrust(ArgInvoke invoke)
    {
        RemoteTarget target;
        try { target = RemoteTarget.Resolve(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        AnsiConsole.MarkupLine($"[yellow]This disables signature enforcement on {Markup.Escape(target.Host)}.[/]");
        if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }

        using var session = target.Connect();
        session.RunPrivileged($"rm -f {RemotePub} {RemoteFp} {RemoteLegacyKey}");
        AnsiConsole.MarkupLine("[green]Trust enforcement removed. Server will now accept unsigned packages.[/]");
        return 0;
    }

    private static int ShowLocalKey()
    {
        if (!File.Exists(PackageSigner.PublicKeyPath))
        {
            AnsiConsole.MarkupLine("[yellow]No local signing key yet.[/] Run: bangka trust init --ssh-host <host>");
            return 1;
        }
        var pubPem = File.ReadAllText(PackageSigner.PublicKeyPath).Trim();
        var fp = PackageSigner.ComputeFingerprint(pubPem);
        AnsiConsole.MarkupLine("[grey]Public key (PEM):[/]");
        AnsiConsole.WriteLine(pubPem);
        AnsiConsole.MarkupLine($"\n[grey]Fingerprint:[/] [white]{Markup.Escape(fp)}[/]");
        AnsiConsole.MarkupLine($"[grey]Private key:[/] {PackageSigner.PrivateKeyPath} [grey](keep secret)[/]");
        return 0;
    }
}
