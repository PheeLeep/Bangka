using ArgSharp;
using ArgSharp.Args;
using bangka.Objects;
using bangka_lib;
using Spectre.Console;

namespace bangka.Commands;

/// <summary>
/// Bootstraps and manages a remote host for Bangka deployments. Replaces the old
/// server-local 'bangkactl deploy-user'. Connects over SSH as an admin/sudo user
/// (password or key), then provisions the bangka-deploy user, installs a locally
/// generated SSH key (private key stays on the master), and writes a tightened
/// sudoers rule limited to exactly the commands deploy/rollback/uninstall need.
/// </summary>
public static class ServerCommand
{
    private const string SudoersFile = "/etc/sudoers.d/" + Constants.BangkaDeployUserName;
    private const string MarkerFile = "/etc/bangka/deploy-user";
    private static readonly string DeployHome = $"/home/{Constants.BangkaDeployUserName}";
    private static readonly string AuthKeys = $"{DeployHome}/.ssh/authorized_keys";
    private static readonly string LocalKeyPath = RemoteTarget.DefaultKeyPath;

    // Only what deploy/rollback/uninstall actually invoke privileged. No shells.
    private static readonly string[] SudoCommandNames =
        ["systemctl", "cp", "mv", "rm", "mkdir", "chmod", "chown", "tee", "touch", "sed"];

    public static void Load(ArgInvoke serverInvoke)
    {
        ArgInvoke? initInvoke = null, statusInvoke = null, rotateInvoke = null, removeInvoke = null;

        initInvoke = serverInvoke.AddArgumentAction(["init"],
            () => Environment.Exit(Init(initInvoke!, force: false)),
            "Create the bangka-deploy user, install a generated key, and configure sudoers");
        LoadAdminArgs(initInvoke);

        rotateInvoke = serverInvoke.AddArgumentAction(["rotate-key"],
            () => Environment.Exit(Init(rotateInvoke!, force: true)),
            "Generate a new deploy key and replace the one on the server");
        LoadAdminArgs(rotateInvoke);

        statusInvoke = serverInvoke.AddArgumentAction(["status"],
            () => Environment.Exit(ShowStatus(statusInvoke!)),
            "Show deploy-user / sudoers / marker state on the server");
        LoadAdminArgs(statusInvoke);

        removeInvoke = serverInvoke.AddArgumentAction(["remove"],
            () => Environment.Exit(Remove(removeInvoke!)),
            "Remove the bangka-deploy user and its sudoers rule");
        LoadAdminArgs(removeInvoke);
    }

    private static void LoadAdminArgs(ArgInvoke invoke)
    {
        invoke.AddArgument<string>(["--ssh-host"], helpMsg: "Target server hostname or IP");
        invoke.AddArgument(["--ssh-port"], helpMsg: "SSH port (default: 22)", defaultValue: 22);
        invoke.AddArgument<string>(["--ssh-user"], isRequired: true, helpMsg: "Admin/sudo user to connect as (e.g. an account with sudo)");
        invoke.AddArgument<string>(["--ssh-key"], helpMsg: "Admin SSH private key (optional; password auth is used if omitted)");
        invoke.AddArgument<string>(["--ssh-password"], helpMsg: "Admin password (optional; prompted securely if needed)");
        invoke.AddArgument<bool>(["--force"], helpMsg: "Skip the host-fingerprint trust prompt");
        invoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
    }

    // ── Admin connection ────────────────────────────────────────────────────

    private sealed class Admin : IDisposable
    {
        public required SshSession Session { get; init; }
        public required bool IsRoot { get; init; }
        public required string? Password { get; init; }
        public string Host => Session.Host;

        /// <summary>Runs a command as root: directly if already root, else via sudo -S
        /// feeding the admin password on stdin (the admin is a full sudoer here).</summary>
        public (int, string, string) Sudo(string command)
        {
            if (IsRoot) return Session.Run(command);
            return Session.Run($"echo {ShellUtil.Quote(Password)} | sudo -S -p '' {command}");
        }

        public string SudoOrThrow(string command)
        {
            var (code, outp, err) = Sudo(command);
            if (code != 0)
                throw new InvalidOperationException($"Privileged command failed (exit {code}): {command}\n{err}");
            return outp;
        }

        public void Dispose() => Session.Dispose();
    }

    private static Admin ConnectAdmin(ArgInvoke invoke)
    {
        var vals = invoke.GetArgStoreValues();
        string? Get(string p) => (vals.SingleOrDefault(a => a.Parameters.Contains(p)) as ArgStore<string>)?.Value;
        var host = Get("--ssh-host");
        var user = Get("--ssh-user");
        var key = Get("--ssh-key");
        var password = Get("--ssh-password");
        var port = (vals.SingleOrDefault(a => a.Parameters.Contains("--ssh-port")) as ArgStore<int>)?.TypedValue ?? 22;
        var force = (vals.SingleOrDefault(a => a.Parameters.Contains("--force")) as ArgStore<bool>)?.TypedValue == true;

        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("--ssh-host is required.");
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("--ssh-user is required.");

        var knownHosts = new KnownHostsStore();
        SshSession session;
        if (!string.IsNullOrWhiteSpace(key) && File.Exists(key))
        {
            session = SshSession.Connect(host, port, user, key, knownHosts, force);
        }
        else
        {
            if (string.IsNullOrEmpty(password))
                password = AnsiConsole.Prompt(new TextPrompt<string>($"[grey]Password for {user}@{host}:[/]").Secret());
            session = SshSession.ConnectPassword(host, port, user, password, knownHosts, force);
        }

        var (code, uid, _) = session.Run("id -u");
        var isRoot = code == 0 && uid.Trim() == "0";
        // Non-root admin needs a password to sudo. Prompt if we authenticated by key.
        if (!isRoot && string.IsNullOrEmpty(password))
            password = AnsiConsole.Prompt(new TextPrompt<string>($"[grey]sudo password for {user}:[/]").Secret());

        return new Admin { Session = session, IsRoot = isRoot, Password = password };
    }

    // ── init / rotate-key ───────────────────────────────────────────────────

    private static int Init(ArgInvoke invoke, bool force)
    {
        Admin admin;
        try { admin = ConnectAdmin(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using (admin)
        {
            try
            {
                AnsiConsole.MarkupLine($"[bold cyan]{(force ? "Rotating deploy key on" : "Initialising")} {Markup.Escape(admin.Host)}[/]\n");

                // ── 1. Generate the deploy keypair locally (private key stays here) ──
                var (userCode, _, _) = admin.Session.Run($"id {Constants.BangkaDeployUserName} 2>/dev/null");
                if (userCode != 0)
                {
                    AnsiConsole.MarkupLine($"- Creating user [white]{Constants.BangkaDeployUserName}[/]...");
                    admin.SudoOrThrow($"useradd -m -s /bin/bash -c 'Bangka deploy user' {Constants.BangkaDeployUserName}");
                }
                else AnsiConsole.MarkupLine($"[grey]User {Constants.BangkaDeployUserName} already exists.[/]");

                // Let the deploy user read service journals (for `bangka logs`) without sudo.
                admin.Sudo($"usermod -aG systemd-journal {Constants.BangkaDeployUserName}");

                var pubKey = GenerateLocalKey(force);
                AnsiConsole.MarkupLine($"[green]Deploy key ready:[/] {LocalKeyPath}");

                // ── 2. Install the public key into the deploy user's authorized_keys ──
                admin.SudoOrThrow($"mkdir -p {DeployHome}/.ssh");
                admin.SudoOrThrow($"chmod 700 {DeployHome}/.ssh");
                var tmpPub = $"/tmp/bangka-deploy-{Guid.NewGuid():N}.pub";
                admin.Session.UploadText(pubKey + "\n", tmpPub);
                if (force)
                    admin.SudoOrThrow($"cp {ShellUtil.Quote(tmpPub)} {AuthKeys}");
                else
                    admin.SudoOrThrow($"bash -c {ShellUtil.Quote($"touch {AuthKeys}; grep -qF {ShellUtil.Quote(pubKey)} {AuthKeys} || cat {tmpPub} >> {AuthKeys}")}");
                admin.SudoOrThrow($"chmod 600 {AuthKeys}");
                admin.SudoOrThrow($"chown -R {Constants.BangkaDeployUserName}:{Constants.BangkaDeployUserName} {DeployHome}/.ssh");
                admin.Session.Run($"rm -f {ShellUtil.Quote(tmpPub)}");

                // ── 3. Write and validate the tightened sudoers rule ──
                WriteSudoers(admin);

                // ── 4. Marker file ──
                admin.SudoOrThrow("mkdir -p /etc/bangka");
                var marker = $"username={Constants.BangkaDeployUserName}\ncreated={DateTime.UtcNow:O}\n";
                var tmpMarker = $"/tmp/bangka-marker-{Guid.NewGuid():N}";
                admin.Session.UploadText(marker, tmpMarker);
                admin.SudoOrThrow($"mv {ShellUtil.Quote(tmpMarker)} {MarkerFile}");
                admin.SudoOrThrow($"chmod 600 {MarkerFile}");

                AnsiConsole.Write(new Panel(
                        $"[bold green]✓[/] [white]{Constants.BangkaDeployUserName}[/] is ready on [white]{Markup.Escape(admin.Host)}[/].\n\n" +
                        $"[grey]Private key:[/] [white]{LocalKeyPath}[/] (kept on this machine)\n" +
                        $"[grey]Sudoers:   [/] [white]{SudoersFile}[/] (validated)\n\n" +
                        $"Deploy with:\n  [grey]bangka deploy --ssh-host {Markup.Escape(admin.Host)} --package <pkg>[/]")
                    .Header("[bold green] Server Ready [/]").BorderColor(Color.Green));
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Server init failed:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }
    }

    private static void WriteSudoers(Admin admin)
    {
        // Resolve real binary paths on the remote so the sudoers entry matches
        // exactly (paths differ across distros; usrmerge etc.).
        var paths = new List<string>();
        foreach (var name in SudoCommandNames)
        {
            var (code, p, _) = admin.Session.Run($"command -v {name}");
            var resolved = p.Trim();
            if (code == 0 && !string.IsNullOrWhiteSpace(resolved)) paths.Add(resolved);
        }
        if (paths.Count == 0) throw new InvalidOperationException("Could not resolve any sudo command paths on the remote.");

        var line = $"{Constants.BangkaDeployUserName} ALL=(ALL) NOPASSWD: {string.Join(", ", paths)}";
        var content = $"# bangka deploy user — managed by 'bangka server'\n# DO NOT EDIT MANUALLY\n# Generated: {DateTime.UtcNow:O}\n\n{line}\n";

        var tmp = $"/tmp/bangka-sudoers-{Guid.NewGuid():N}";
        admin.Session.UploadText(content, tmp);
        admin.SudoOrThrow($"cp {ShellUtil.Quote(tmp)} {SudoersFile}");
        admin.SudoOrThrow($"chmod 440 {SudoersFile}");
        admin.SudoOrThrow($"chown root:root {SudoersFile}");
        admin.Session.Run($"rm -f {ShellUtil.Quote(tmp)}");

        var (vc, _, verr) = admin.Sudo($"visudo -c -f {SudoersFile}");
        if (vc != 0)
        {
            admin.Sudo($"rm -f {SudoersFile}");
            throw new InvalidOperationException($"sudoers validation failed, rule removed: {verr}");
        }
        AnsiConsole.MarkupLine("[green]Sudoers rule validated.[/]");
    }

    /// <summary>Generates an ed25519 keypair on the master via ssh-keygen and returns the public key.</summary>
    private static string GenerateLocalKey(bool force)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalKeyPath)!);

        // Reuse an existing private key unless rotating. If its public half is
        // missing (e.g. a key copied in by hand), derive it rather than failing.
        if (File.Exists(LocalKeyPath) && !force)
        {
            if (File.Exists(LocalKeyPath + ".pub"))
                return File.ReadAllText(LocalKeyPath + ".pub").Trim();

            var pub = RunKeygen($"-y -f \"{LocalKeyPath}\"", captureStdout: true);
            File.WriteAllText(LocalKeyPath + ".pub", pub + "\n");
            return pub;
        }

        foreach (var f in new[] { LocalKeyPath, LocalKeyPath + ".pub" })
            if (File.Exists(f)) File.Delete(f);

        RunKeygen($"-t ed25519 -N \"\" -C {Constants.BangkaDeployUserName} -f \"{LocalKeyPath}\"", captureStdout: false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(LocalKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return File.ReadAllText(LocalKeyPath + ".pub").Trim();
    }

    private static string RunKeygen(string args, bool captureStdout)
    {
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ssh-keygen failed: {stderr}");
        return stdout.Trim();
    }

    // ── status ──────────────────────────────────────────────────────────────

    private static int ShowStatus(ArgInvoke invoke)
    {
        Admin admin;
        try { admin = ConnectAdmin(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using (admin)
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
                .AddColumn("[grey]Item[/]").AddColumn("[grey]Status[/]");

            var (userCode, _, _) = admin.Session.Run($"id {Constants.BangkaDeployUserName} 2>/dev/null");
            table.AddRow("Deploy user", userCode == 0 ? "[green]exists[/]" : "[red]missing[/]");

            var (akCode, _, _) = admin.Sudo($"test -f {AuthKeys}");
            table.AddRow("authorized_keys", akCode == 0 ? "[green]present[/]" : "[yellow]missing[/]");

            var (sdCode, _, _) = admin.Sudo($"test -f {SudoersFile}");
            table.AddRow("sudoers rule", sdCode == 0 ? "[green]present[/]" : "[yellow]missing[/]");
            if (sdCode == 0)
            {
                var (vc, _, _) = admin.Sudo($"visudo -c -f {SudoersFile}");
                table.AddRow("sudoers valid", vc == 0 ? "[green]yes[/]" : "[red]INVALID[/]");
            }

            var (localKey, _, _) = (File.Exists(LocalKeyPath) ? (0, "", "") : (1, "", ""));
            table.AddRow("Local deploy key", localKey == 0 ? $"[green]{LocalKeyPath}[/]" : "[yellow]not found on this machine[/]");

            AnsiConsole.Write(table);
            return 0;
        }
    }

    // ── remove ──────────────────────────────────────────────────────────────

    private static int Remove(ArgInvoke invoke)
    {
        Admin admin;
        try { admin = ConnectAdmin(invoke); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]"); return 1; }

        using (admin)
        {
            AnsiConsole.MarkupLine($"[yellow]This removes user {Constants.BangkaDeployUserName} and its sudoers rule from {Markup.Escape(admin.Host)}.[/]");
            if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 0;
            }
            admin.Sudo($"rm -f {SudoersFile}");
            admin.Sudo($"rm -f {MarkerFile}");
            var (delCode, _, delErr) = admin.Sudo($"userdel -r {Constants.BangkaDeployUserName} 2>&1");
            if (delCode != 0) AnsiConsole.MarkupLine($"[yellow]User deletion warning:[/] {Markup.Escape(delErr)}");
            else AnsiConsole.MarkupLine($"[green]User {Constants.BangkaDeployUserName} removed.[/]");
            return 0;
        }
    }
}
