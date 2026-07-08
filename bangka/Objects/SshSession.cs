using System;
using Renci.SshNet;
using Spectre.Console;

namespace bangka.Objects;

public sealed class SshSession : IDisposable
{
    private readonly SshClient _ssh;
    private readonly ScpClient _scp;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }

    private SshSession(SshClient ssh, ScpClient scp, string host, int port)
    {
        _ssh = ssh;
        _scp = scp;
        Host = host;
        Port = port;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Connect using private-key authentication (the default for deploy and all
    /// post-bootstrap commands, which authenticate as the bangka-deploy user).</summary>
    public static SshSession Connect(
        string host, int port, string user, string privateKeyPath,
        KnownHostsStore knownHosts, bool force = false)
    {
        var keyFile = new PrivateKeyFile(privateKeyPath);
        return Connect(host, port, user, knownHosts, force,
            () => new PrivateKeyAuthenticationMethod(user, keyFile));
    }

    /// <summary>Connect using password authentication. Used only by 'bangka server init'
    /// to bootstrap a fresh host as an admin/sudo user before the deploy key exists.
    /// Answers both the 'password' and 'keyboard-interactive' methods OpenSSH may negotiate.</summary>
    public static SshSession ConnectPassword(
        string host, int port, string user, string password,
        KnownHostsStore knownHosts, bool force = false)
    {
        return Connect(host, port, user, knownHosts, force, () =>
        {
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            return kbd;
        }, () => new PasswordAuthenticationMethod(user, password));
    }

    private static SshSession Connect(
        string host, int port, string user,
        KnownHostsStore knownHosts, bool force,
        params Func<AuthenticationMethod>[] authFactories)
    {
        // Each ConnectionInfo consumes its own auth-method instances.
        var connSsh = new ConnectionInfo(host, port, user,
            authFactories.Select(f => f()).ToArray());
        var connScp = new ConnectionInfo(host, port, user,
            authFactories.Select(f => f()).ToArray());

        var ssh = new SshClient(connSsh);
        var scp = new ScpClient(connScp);

        // ── Fingerprint trust check ───────────────────────────────────────────
        string? capturedFingerprint = null;

        ssh.HostKeyReceived += (_, e) =>
        {
            capturedFingerprint = BitConverter.ToString(e.FingerPrint)
                                              .Replace("-", ":").ToLowerInvariant();

            if (knownHosts.TryGet(host, port, out var trusted))
            {
                if (trusted == capturedFingerprint)
                {
                    e.CanTrust = true;
                    return;
                }

                // Fingerprint changed — very suspicious
                AnsiConsole.MarkupLine($"\n[bold red]⚠  WARNING: Host fingerprint has changed![/]");
                AnsiConsole.MarkupLine($"   [grey]Expected:[/] [red]{trusted}[/]");
                AnsiConsole.MarkupLine($"   [grey]Received:[/] [yellow]{capturedFingerprint}[/]");
                AnsiConsole.MarkupLine("[red]This could indicate a man-in-the-middle attack.[/]");

                if (!force)
                {
                    var accept = AnsiConsole.Confirm("[yellow]Accept new fingerprint anyway?[/]", defaultValue: false);
                    e.CanTrust = accept;
                    if (accept)
                        knownHosts.Trust(host, port, capturedFingerprint);
                }
                else
                {
                    e.CanTrust = false;
                }
                return;
            }

            // New / untrusted host
            AnsiConsole.MarkupLine($"\n[yellow]The authenticity of host [white]{host}:{port}[/] cannot be established.[/]");
            AnsiConsole.MarkupLine($"   [grey]Host key fingerprint:[/] [white]{capturedFingerprint}[/]");

            var trust = force
                ? true
                : AnsiConsole.Confirm("[yellow]Do you want to trust this host and continue?[/]", defaultValue: false);

            e.CanTrust = trust;
            if (trust)
                knownHosts.Trust(host, port, capturedFingerprint);
        };

        scp.HostKeyReceived += (_, e) =>
        {
            var scpFingerprint = BitConverter.ToString(e.FingerPrint)
                                            .Replace("-", ":").ToLowerInvariant();
            // If SSH already verified this exact fingerprint, trust it
            if (capturedFingerprint == scpFingerprint)
            {
                e.CanTrust = true;
                return;
            }
            // Fingerprint mismatch — reject (could be MITM on SCP channel)
            AnsiConsole.MarkupLine($"\n[bold red]⚠  WARNING: SCP host fingerprint does not match SSH fingerprint![/]");
            AnsiConsole.MarkupLine($"   [grey]SSH expected:[/] [red]{capturedFingerprint}[/]");
            AnsiConsole.MarkupLine($"   [grey]SCP received:[/] [yellow]{scpFingerprint}[/]");
            if (force)
            {
                e.CanTrust = true;
                knownHosts.Trust(host, port, scpFingerprint);
            }
            else
            {
                e.CanTrust = false;
            }
        };
        
        ssh.Connect();
        scp.Connect();

        return new SshSession(ssh, scp, host, port);
    }

    // ── Remote execution ──────────────────────────────────────────────────────

    /// <summary>Runs a command and returns (exit code, stdout, stderr).</summary>
    public (int ExitCode, string Stdout, string Stderr) Run(string command)
    {
        using var cmd = _ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(5);
        var stdout = cmd.Execute();
        return (cmd.ExitStatus ?? -1, stdout.TrimEnd(), cmd.Error.TrimEnd());
    }

    /// <summary>Runs a command and throws if exit code is non-zero.</summary>
    public string RunOrThrow(string command)
    {
        var (code, out_, err) = Run(command);
        if (code != 0)
            throw new InvalidOperationException(
                $"Remote command failed (exit {code}): {command}\nStderr: {err}");
        return out_;
    }

    // ── Privilege escalation ──────────────────────────────────────────────────

    /// <summary>
    /// True if the connected SSH user is root (uid=0).
    /// Set by DetectPrivileges() during deploy Step 2.
    /// </summary>
    public bool IsRoot { get; private set; } = false;

    public void SetIsRoot(bool value) => IsRoot = value;

    /// <summary>
    /// Runs a command, prefixing with sudo if the connected user is not root.
    /// Relies on passwordless sudo being configured (NOPASSWD in sudoers).
    /// </summary>
    public (int ExitCode, string Stdout, string Stderr) RunPrivileged(string command)
        => Run(IsRoot ? command : $"sudo {command}");

    /// <summary>RunPrivileged variant that throws on non-zero exit.</summary>
    public string RunPrivilegedOrThrow(string command)
    {
        var (code, out_, err) = RunPrivileged(command);
        if (code != 0)
            throw new InvalidOperationException(
                $"Privileged command failed (exit {code}): {command}\nStderr: {err}");
        return out_;
    }

    // ── SCP file transfer ─────────────────────────────────────────────────────

    public void Upload(string localPath, string remoteDir, Action<long, long>? progress = null)
    {
        if (progress != null)
            _scp.Uploading += (_, e) => progress(e.Uploaded, e.Size);

        _scp.Upload(new FileInfo(localPath), remoteDir);
    }

    /// <summary>Writes bytes to a local temp file and uploads them to <paramref name="remotePath"/>.</summary>
    public void UploadBytes(byte[] data, string remotePath)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, data);
            _scp.Upload(new FileInfo(tmp), remotePath);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Writes UTF-8 text to a local temp file and uploads it to <paramref name="remotePath"/>.</summary>
    public void UploadText(string text, string remotePath) =>
        UploadBytes(System.Text.Encoding.UTF8.GetBytes(text), remotePath);

    // ── Streaming execution (for `logs --follow`) ──────────────────────────────

    /// <summary>Runs a long-lived command, invoking <paramref name="onData"/> as output
    /// arrives, until the token is cancelled or the command exits.</summary>
    public void StreamCommand(string command, Action<string> onData, CancellationToken token)
    {
        using var cmd = _ssh.CreateCommand(command);
        var async = cmd.BeginExecute();
        using var reader = new StreamReader(cmd.OutputStream);
        try
        {
            while (!token.IsCancellationRequested && (!async.IsCompleted || !reader.EndOfStream))
            {
                var line = reader.ReadLine();
                if (line != null) onData(line);
                else if (async.IsCompleted) break;
                else Thread.Sleep(100);
            }
        }
        finally
        {
            try { cmd.CancelAsync(); } catch { }
        }
    }

    // ── Privilege detection ────────────────────────────────────────────────────

    /// <summary>Detects whether the connected user is root and sets <see cref="IsRoot"/>.
    /// When not root, verifies passwordless sudo for systemctl is available.
    /// Returns (isRoot, sudoOk). sudoOk is always true when root.</summary>
    public (bool IsRoot, bool SudoOk) DetectPrivileges()
    {
        var (code, uid, _) = Run("id -u");
        var root = code == 0 && uid.Trim() == "0";
        SetIsRoot(root);
        if (root) return (true, true);
        var (sudoCode, _, _) = Run("sudo -n systemctl --version 2>&1");
        return (false, sudoCode == 0);
    }

    /// <summary>Returns the remote deploy user's services base directory ($HOME/bangkasvcs).</summary>
    public string ResolveServicesBase()
    {
        var (_, home, _) = Run("echo $HOME");
        home = home.Trim();
        if (string.IsNullOrWhiteSpace(home)) home = ".";
        return $"{home}/bangkasvcs";
    }

    // ── Public key retrieval ──────────────────────────────────────────────────

    /// <summary>
    /// Reads the public key corresponding to the given private key path.
    /// Tries <keyPath>.pub first; falls back to ssh-keygen -y.
    /// </summary>
    public static string GetPublicKey(string privateKeyPath)
    {
        var pubPath = privateKeyPath + ".pub";
        if (File.Exists(pubPath))
            return File.ReadAllText(pubPath).Trim();

        // Fallback: derive public key via ssh-keygen (master OS must have it)
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-y -f \"{privateKeyPath}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var pub = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return pub;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _scp.Disconnect(); } catch { }
        try { _ssh.Disconnect(); } catch { }
        _scp.Dispose();
        _ssh.Dispose();
    }
}
