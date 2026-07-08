using ArgSharp.Args;
using bangka.Commands;
using bangka.Properties;
using bangka_lib;

namespace bangka.Objects;

/// <summary>
/// Resolves SSH connection parameters (host/port/user/key) for the control
/// commands (list, status, logs, rollback, uninstall, trust) from CLI flags
/// and/or a deployment profile, and opens a key-based <see cref="SshSession"/>.
/// Post-bootstrap commands connect as the bangka-deploy user by default.
/// </summary>
public sealed class RemoteTarget
{
    public string Host { get; }
    public int Port { get; }
    public string User { get; }
    public string KeyPath { get; }
    public bool Force { get; }

    private RemoteTarget(string host, int port, string user, string keyPath, bool force)
    {
        Host = host;
        Port = port;
        User = user;
        KeyPath = keyPath;
        Force = force;
    }

    public static string DefaultKeyPath =>
        Path.Combine(Constants.SSHStorage, Constants.BangkaDeployUserName);

    /// <summary>Resolve connection parameters from parsed args and an optional profile.</summary>
    public static RemoteTarget Resolve(ArgInvoke invoke, string? defaultUser = null)
    {
        var opts = Arguments.DeployArgs.Parse(invoke);

        if (!string.IsNullOrWhiteSpace(opts.Profile))
        {
            var profile = DeploymentProfile.Load(opts.Profile);
            opts = profile.ApplyToDeployArgs(opts);
        }

        if (string.IsNullOrWhiteSpace(opts.Host))
            throw new ArgumentException("--ssh-host is required (or set 'host' in a profile).");

        var user = !string.IsNullOrWhiteSpace(opts.SshUser)
            ? opts.SshUser
            : defaultUser ?? Constants.BangkaDeployUserName;

        var keyPath = !string.IsNullOrWhiteSpace(opts.KeyPath) ? opts.KeyPath : DefaultKeyPath;

        return new RemoteTarget(opts.Host, opts.SshPort, user, keyPath, opts.Force);
    }

    /// <summary>Opens a key-authenticated SSH session and runs privilege detection.</summary>
    public SshSession Connect()
    {
        if (!File.Exists(KeyPath))
            throw new FileNotFoundException(
                $"SSH key not found: {KeyPath}\nRun 'bangka server init' to provision a host, or pass --ssh-key.");

        var session = SshSession.Connect(Host, Port, User, KeyPath, new KnownHostsStore(), Force);
        session.DetectPrivileges();
        return session;
    }
}
