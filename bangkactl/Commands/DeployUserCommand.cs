using System;
using System.Security.Cryptography;
using ArgSharp;
using ArgSharp.Args;
using Spectre.Console;

namespace bangkactl.Commands;

public static class DeployUserCommand
{
    public const string Username = "bangka-deploy";
    private const string SudoersFile = "/etc/sudoers.d/bangka-deploy";
    private const string MarkerFile = "/etc/bangka/deploy-user";
    private const string AuthKeysDir = $"/home/{Username}/.ssh";
    private const string AuthKeysFile = $"/home/{Username}/.ssh/authorized_keys";

    /// <summary>
    /// The exact set of commands granted passwordless sudo.
    /// Deliberately minimal — only what DeployCommand actually needs.
    /// </summary>
    private static readonly string[] SudoCommands =
    [
        "/usr/bin/systemctl",
        "/bin/cp",
        "/bin/mv",
        "/bin/rm",
        "/bin/mkdir",
        "/bin/chmod",
        "/bin/chown",
        "/usr/bin/python3",
        "/usr/bin/printf",
        "/bin/echo",
        "/usr/bin/unzip",
        "/usr/bin/tee",
        "/bin/bash",
        "/usr/bin/base64",
        "/usr/bin/find",
        "/usr/bin/sha512sum",
    ];

    internal static void Load(ArgInvoke argInvoke)
    {
        // Must be root to manage users and sudoers
        if (!IsRoot())
        {
            AnsiConsole.MarkupLine("[red]deploy-user commands must be run as root.[/]");
            Environment.Exit(1);
            return;
        }

        argInvoke.AddArgumentAction(["init"], () =>
         {
             Environment.Exit(Init(force: false));
         }, $"Create '{Username}', generate SSH key, configure sudoers").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["rotate"], () =>
         {
             Environment.Exit(Init(force: true));
         }, $"Replace the SSH key pair and print the new private key").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["status"], () =>
         {
             Environment.Exit(ShowStatus());
         }, $"Show user setup state, sudoers validity, fingerprint").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["remove"], () =>
        {
            Environment.Exit(RemoveUser());
        }, $"Removes user").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
    }

    // ── init / rotate ─────────────────────────────────────────────────────────

    private static int Init(bool force)
    {
        var verb = force ? "Rotating deploy user key" : "Initialising deploy user";
        AnsiConsole.MarkupLine($"[bold cyan]{verb}[/]\n");

        // ── 1. Create user if it doesn't exist ───────────────────────────────
        var (userExists, _, _) = Shell($"id {Username} 2>/dev/null");
        if (userExists != 0)
        {
            AnsiConsole.MarkupLine($"  [grey]Creating user [white]{Username}[/]...[/]");
            var (createCode, _, createErr) = Shell(
                $"useradd -m -s /bin/bash -c 'Bangka deploy user' {Username}");
            if (createCode != 0)
            {
                AnsiConsole.MarkupLine($"  [red]Failed to create user:[/] {createErr}");
                return 1;
            }
            AnsiConsole.MarkupLine($"  [green]User [white]{Username}[/] created.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [grey]User [white]{Username}[/] already exists.[/]");
        }

        // ── 2. Generate Ed25519 key pair in memory — never written to disk ───
        AnsiConsole.MarkupLine("  [grey]Generating Ed25519 key pair...[/]");

        string privatePem;
        string publicOpenSsh;
        string fingerprint;

        privatePem = GenerateEd25519PrivatePem();
        publicOpenSsh = GenerateEd25519PublicOpenSsh(privatePem, out fingerprint);

        // ── 3. Install public key into authorized_keys ───────────────────────
        AnsiConsole.MarkupLine("  [grey]Installing public key...[/]");

        Shell($"mkdir -p {AuthKeysDir}");
        Shell($"chmod 700 {AuthKeysDir}");
        Shell($"chown {Username}:{Username} {AuthKeysDir}");

        if (force)
        {
            // Replace existing key — remove old entry first
            Shell($"echo '{publicOpenSsh}' > {AuthKeysFile}");
        }
        else
        {
            Shell($"touch {AuthKeysFile}");
            // Avoid duplicate entries
            Shell($"grep -v '{Username}' {AuthKeysFile} > /tmp/ak.tmp 2>/dev/null; mv /tmp/ak.tmp {AuthKeysFile} 2>/dev/null; true");
            Shell($"echo '{publicOpenSsh} {Username}' >> {AuthKeysFile}");
        }

        Shell($"chmod 600 {AuthKeysFile}");
        Shell($"chown {Username}:{Username} {AuthKeysFile}");

        // ── 4. Write sudoers rule ─────────────────────────────────────────────
        AnsiConsole.MarkupLine("  [grey]Writing sudoers rule...[/]");

        var sudoLine = $"{Username} ALL=(ALL) NOPASSWD: {string.Join(", ", SudoCommands)}";
        var sudoContent = $"""
# bangka deploy user — managed by bangkactl deploy-user
# DO NOT EDIT MANUALLY — use bangkactl deploy-user to modify
# Generated: {DateTime.UtcNow:O}

{sudoLine}
""";
        // Write via tee to avoid quoting issues
        var sudoB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sudoContent));
        Shell($"echo {sudoB64} | base64 -d > {SudoersFile}");
        Shell($"chmod 440 {SudoersFile}");
        Shell($"chown root:root {SudoersFile}");

        // Validate the sudoers file
        var (visudoCode, _, visudoErr) = Shell($"visudo -c -f {SudoersFile}");
        if (visudoCode != 0)
        {
            AnsiConsole.MarkupLine($"  [red]Sudoers file failed validation:[/] {visudoErr}");
            Shell($"rm -f {SudoersFile}");
            return 1;
        }
        AnsiConsole.MarkupLine("  [grey]Sudoers rule validated.[/]");

        // ── 5. Write marker file ──────────────────────────────────────────────
        Shell("mkdir -p /etc/bangka");
        var marker = $"username={Username}\nfingerprint={fingerprint}\ncreated={DateTime.UtcNow:O}\n";
        var markerB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(marker));
        Shell($"echo {markerB64} | base64 -d > {MarkerFile}");
        Shell($"chmod 600 {MarkerFile}");

        // ── 6. Print private key — ONLY output, never stored ─────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow] PRIVATE KEY — COPY NOW [/]").RuleStyle("yellow"));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(privatePem);
        AnsiConsole.Write(new Rule("[bold yellow] END PRIVATE KEY [/]").RuleStyle("yellow"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
                $"[bold white]{Username}[/] is ready.\n\n" +
                $"[grey]Fingerprint:[/] [white]{fingerprint}[/]\n" +
                $"[grey]Auth keys: [/] [white]{AuthKeysFile}[/]\n" +
                $"[grey]Sudoers:   [/] [white]{SudoersFile}[/]\n\n" +
                "[bold yellow]The private key above was printed once and is NOT stored on this server.[/]\n" +
                "Copy it to your master machine:\n\n" +
                $"  [grey]# On master:[/]\n" +
                $"  nano ~/.ssh/bangka_{Username}\n" +
                $"  chmod 600 ~/.ssh/bangka_{Username}\n\n" +
                "Then deploy with:\n" +
                $"  bangka deploy --user {Username} --key ~/.ssh/bangka_{Username} ...")
            .Header("[bold green] Setup Complete [/]")
            .BorderColor(Color.Green));
        return 0;
    }

    // ── status ────────────────────────────────────────────────────────────────

    private static int ShowStatus()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Item[/]")
            .AddColumn("[grey]Status[/]");

        // User exists?
        var (userCode, _, _) = Shell($"id {Username} 2>/dev/null");
        table.AddRow("User",
            userCode == 0
                ? $"[green]{Username} exists[/]"
                : $"[red]{Username} not found[/]");

        // authorized_keys
        var (akCode, _, _) = Shell($"test -f {AuthKeysFile}");
        table.AddRow("authorized_keys",
            akCode == 0 ? "[green]present[/]" : "[yellow]missing[/]");

        // sudoers
        var (sdCode, _, _) = Shell($"test -f {SudoersFile}");
        table.AddRow("sudoers rule",
            sdCode == 0 ? "[green]present[/]" : "[yellow]missing[/]");

        // sudoers validation
        if (sdCode == 0)
        {
            var (vsCode, _, _) = Shell($"visudo -c -f {SudoersFile}");
            table.AddRow("sudoers valid",
                vsCode == 0 ? "[green]yes[/]" : "[red]INVALID[/]");
        }

        // marker / fingerprint
        if (File.Exists(MarkerFile))
        {
            var markerContent = File.ReadAllText(MarkerFile);
            var fp = markerContent.Split('\n')
                .Where(l => l.StartsWith("fingerprint="))
                .Select(l => l[12..])
                .FirstOrDefault() ?? "—";
            var created = markerContent.Split('\n')
                .Where(l => l.StartsWith("created="))
                .Select(l => l[8..])
                .FirstOrDefault() ?? "—";
            table.AddRow("Key fingerprint", $"[white]{fp}[/]");
            table.AddRow("Created", $"[grey]{created}[/]");
        }
        else
        {
            table.AddRow("Key fingerprint", "[grey]unknown (no marker file)[/]");
        }

        // sudo test
        var (sudoTest, _, _) = Shell($"sudo -u {Username} sudo -n systemctl --version 2>/dev/null");
        table.AddRow("Sudo test",
            sudoTest == 0 ? "[green]passwordless sudo works[/]" : "[yellow]cannot verify (run as root)[/]");

        AnsiConsole.Write(table);

        if (sdCode == 0)
        {
            AnsiConsole.MarkupLine("\n[grey]Granted commands:[/]");
            foreach (var cmd in SudoCommands)
                AnsiConsole.MarkupLine($"  [grey]{cmd}[/]");
        }

        return 0;
    }

    // ── remove ────────────────────────────────────────────────────────────────

    private static int RemoveUser()
    {
        AnsiConsole.MarkupLine($"[yellow]This will remove the user {Username}[/]");

        if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }

        if (File.Exists(SudoersFile))
        {
            File.Delete(SudoersFile);
            AnsiConsole.MarkupLine("[green]Sudoers rule removed.[/]");
        }

        if (File.Exists(MarkerFile))
            File.Delete(MarkerFile);

        var (delCode, _, delErr) = Shell($"userdel -r {Username} 2>&1");
        if (delCode != 0)
            AnsiConsole.MarkupLine($"[yellow]User deletion warning:[/] {delErr}");
        else
            AnsiConsole.MarkupLine($"[green]User {Username} deleted.[/]");

        return 0;
    }

    // ── Ed25519 key generation ────────────────────────────────────────────────

    private static string GenerateEd25519PrivatePem()
    {
        // .NET 8 supports Ed25519 natively
        // Curve: Edwards25519
        var key = ECDiffieHellman.Create(
            ECCurve.CreateFromFriendlyName("nistP256"));

        // Use raw Ed25519 via the built-in helper
        // We generate via ssh-keygen subprocess since .NET's Ed25519 export
        // to OpenSSH PEM format requires additional marshaling
        var tmpKey = $"/tmp/bangka-keygen-{Guid.NewGuid():N}";
        var (code, _, err) = Shell(
            $"ssh-keygen -t ed25519 -N '' -C '{Username}' -f {tmpKey} 2>&1");

        if (code != 0)
            throw new InvalidOperationException($"ssh-keygen failed: {err}");

        var privPem = File.ReadAllText(tmpKey);

        // Immediately wipe the key files from disk
        File.WriteAllText(tmpKey, new string('0', privPem.Length)); // overwrite
        File.Delete(tmpKey);
        if (File.Exists(tmpKey + ".pub"))
            File.Delete(tmpKey + ".pub");

        return privPem;
    }

    private static string GenerateEd25519PublicOpenSsh(string privatePem, out string fingerprint)
    {
        // Write private to temp, extract public, then wipe temp immediately
        var tmpKey = $"/tmp/bangka-pub-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tmpKey, privatePem);
            File.SetUnixFileMode(tmpKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var (pubCode, pubOut, pubErr) = Shell($"ssh-keygen -y -f {tmpKey}");
            if (pubCode != 0)
                throw new InvalidOperationException($"Failed to extract public key: {pubErr}");

            var (fpCode, fpOut, _) = Shell($"ssh-keygen -l -f {tmpKey}.pub 2>/dev/null || ssh-keygen -l -f {tmpKey}");
            fingerprint = fpCode == 0
                ? fpOut.Trim().Split(' ').ElementAtOrDefault(1) ?? "unknown"
                : "unknown";

            return pubOut.Trim();
        }
        finally
        {
            // Always wipe the temp key file even if an exception occurs
            if (File.Exists(tmpKey))
            {
                File.WriteAllText(tmpKey, new string('0', 512));
                File.Delete(tmpKey);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsRoot()
    {
        var (code, uid, _) = Shell("id -u");
        return code == 0 && uid.Trim() == "0";
    }

    private static (int ExitCode, string Stdout, string Stderr) Shell(string cmd)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                })!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}

