using System;
using ArgSharp.Args;

namespace bangka.Commands;

public class Arguments
{
    public static void LoadBuildArgs(ArgInvoke arg)
    {
        arg.AddArgument<string>(["--name"], helpMsg: "The name of the build");
        arg.AddArgument<string>(["--version"], helpMsg: "The version of the build");
        arg.AddArgument<string>(["--publish"], helpMsg: "The path to the published output directory (e.g. bin/Release/net8.0/publish)");
        arg.AddArgument(["--port"], helpMsg: "The port the service listens on (default: 5000)", defaultValue: 5000);
        arg.AddArgument<string>(["--env"], helpMsg: "The ASPNETCORE_ENVIRONMENT value to set on the service (default: Production)");
        arg.AddArgument<string>(["--author"], helpMsg: "The author of the service (optional)");
        arg.AddArgument<string>(["--user"], helpMsg: "The user to run the service as (optional)");
        arg.AddArgument<string>(["--description"], helpMsg: "A description of the service (optional)");
        arg.AddArgument<string>(["--cf-tunnel-id"], helpMsg: "Cloudflare Tunnel ID for Cloudflare integration (optional, but requires all CF flags if used)");
        arg.AddArgument<string>(["--cf-tunnel-name"], helpMsg: "Cloudflare Tunnel Name for Cloudflare integration (optional, but requires all CF flags if used)");
        arg.AddArgument<string>(["--cf-hostname"], helpMsg: "Cloudflare Hostname for Cloudflare integration (optional, but requires all CF flags if used)");
        arg.AddArgument<string>(["--cf-credentials"], helpMsg: "Path to Cloudflare credentials file for Cloudflare integration (optional, but requires all CF flags if used)");
        arg.AddArgument<string>(["--profile"], helpMsg: "The name of a profile to load default values from (optional)");
        arg.AddArgument<string>(["--dll"], helpMsg: "The entry DLL to use (optional, auto-detected from publish dir if omitted)");
        arg.AddArgument<string>(["--env-file"], helpMsg: "Path to a LOCAL .env file with secrets (default: ./.env). Shipped out-of-band at deploy; never packaged.");
        arg.AddArgument<bool>(["--sign"], helpMsg: "Whether to sign the package (default: false)");
        arg.AddArgument<string>(["--signing-key"], helpMsg: "Path to the signing key (required if --sign is true)");
        arg.AddArgument<string>(["--out"], helpMsg: "The output directory for the generated package (default: current directory)");
        arg.AddArgument<string>(["--data-path"], helpMsg: "Persistent data directory for the service (relative to install base or absolute). Injected as DATA_PATH env var at deploy time.");
    }

    /// <summary>Declares the SSH connection flags shared by all remote control
    /// commands (list, status, logs, rollback, uninstall, trust). These are the
    /// subset of deploy args that <see cref="DeployArgs.Parse"/> reads for connecting.</summary>
    public static void LoadConnectionArgs(ArgInvoke arg)
    {
        arg.AddArgument<string>(["--ssh-host"], helpMsg: "The hostname or IP address of the target server");
        arg.AddArgument(["--ssh-port"], helpMsg: "The SSH port of the target server (default: 22)", defaultValue: 22);
        arg.AddArgument<string>(["--ssh-key"], helpMsg: "Path to the SSH private key (default: ~/.ssh/bangka-deploy)");
        arg.AddArgument<string>(["--ssh-user"], helpMsg: "SSH user to connect as (default: bangka-deploy)");
        arg.AddArgument<string>(["--profile"], helpMsg: "Load host/port/key/user from a saved profile (optional)");
        arg.AddArgument<bool>(["--force"], helpMsg: "Skip the host-fingerprint trust prompt (default: false)");
    }

    public static void LoadDeployArgs(ArgInvoke arg)
    {
        arg.AddArgument<string>(["--package"], helpMsg: "The path to the package file to deploy (e.g. MyApp-1.0.0.bangka)");
        arg.AddArgument<string>(["--ssh-host"], helpMsg: "The hostname or IP address of the target server");
        arg.AddArgument(["--ssh-port"], helpMsg: "The SSH port of the target server (default: 22)", defaultValue: 22);
        arg.AddArgument<string>(["--ssh-key"], helpMsg: "Path to the SSH private key (default: ~/.ssh/bangka-deploy)");
        arg.AddArgument<string>(["--ssh-user"], helpMsg: "SSH user to connect as (default: bangka-deploy)");
        arg.AddArgument<string>(["--profile"], helpMsg: "The name of a profile to load default values from (optional)");
        arg.AddArgument<bool>(["--force"], helpMsg: "Whether to skip the trust prompt and force deployment (default: false)");
        arg.AddArgument<int>(["--err-lines"], helpMsg: "The number of journal lines to show on deployment failure (default: 30)");
        arg.AddArgument<string>(["--pub-key"], helpMsg: "The path to a public key to use for signature verification (optional, overrides default public key)");
        arg.AddArgument<bool>(["--no-verify"], helpMsg: "Whether to skip signature verification (not recommended)");
        arg.AddArgument(["--max-snapshots"], helpMsg: $"Max rollback snapshots to retain per service (default: {bangka_lib.Constants.DefaultMaxSnapshots})", defaultValue: bangka_lib.Constants.DefaultMaxSnapshots);
    }
    // ── Build Arguments ──────────────────────────────────────────────────────────

    public class BuildArgs
    {
        // Required
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PublishDir { get; set; } = string.Empty;

        // Optional service
        public int Port { get; set; } = 5000;
        public string User { get; set; } = "www-data";
        public string Environment { get; set; } = "Production";
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Cloudflare (all optional — omit to skip CF integration)
        public string? CfTunnelId { get; set; }
        public string? CfTunnelName { get; set; }
        public string? CfHostname { get; set; }
        public string? CfCredentials { get; set; }

        // Output
        public string OutDir { get; set; } = ".";

        public string? Profile { get; set; }

        // Entry DLL override (optional — auto-detected from publish dir if omitted)
        public string? EntryDll { get; set; }

        // EnvironmentFile path on the remote (optional)
        public string? EnvFile { get; set; }

        public string? DataPath { get; set; }
        // Package signing
        public bool Sign { get; set; } = false;
        public string? SigningKeyPath { get; set; }

        public static BuildArgs Parse(ArgInvoke argInvoke)
        {
            var args = argInvoke.GetArgStoreValues();
            var a = new BuildArgs();


            // Only assign when the flag was actually provided — an unset string arg
            // comes back as "" and would otherwise clobber the class defaults
            // (e.g. User="www-data", Environment="Production").
            if (args.SingleOrDefault(a => a.Parameters.Contains("--name")) is ArgStore<string> namePf && !string.IsNullOrWhiteSpace(namePf.Value)) a.Name = namePf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--version")) is ArgStore<string> verPf && !string.IsNullOrWhiteSpace(verPf.Value)) a.Version = verPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--publish")) is ArgStore<string> publishPf && !string.IsNullOrWhiteSpace(publishPf.Value)) a.PublishDir = publishPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--port")) is ArgStore<int> portPf && portPf.TypedValue != 0) a.Port = portPf.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--env")) is ArgStore<string> envPf && !string.IsNullOrWhiteSpace(envPf.Value)) a.Environment = envPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--author")) is ArgStore<string> authorPf && !string.IsNullOrWhiteSpace(authorPf.Value)) a.Author = authorPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--user")) is ArgStore<string> userPf && !string.IsNullOrWhiteSpace(userPf.Value)) a.User = userPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--description")) is ArgStore<string> descriptionPf && !string.IsNullOrWhiteSpace(descriptionPf.Value)) a.Description = descriptionPf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--cf-tunnel-id")) is ArgStore<string> cfTid) a.CfTunnelId = cfTid.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--cf-tunnel-name")) is ArgStore<string> cfName) a.CfTunnelName = cfName.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--cf-hostname")) is ArgStore<string> cfHostName) a.CfHostname = cfHostName.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--cf-credentials")) is ArgStore<string> cfCred) a.CfCredentials = cfCred.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--profile")) is ArgStore<string> prof) a.Profile = prof.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--dll")) is ArgStore<string> dllProf) a.EntryDll = dllProf.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--env-file")) is ArgStore<string> envFile) a.EnvFile = envFile.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--sign")) is ArgStore<bool> signer) a.Sign = signer.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--signing-key")) is ArgStore<string> signKey) a.SigningKeyPath = signKey.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--out")) is ArgStore<string> outDir
                && !string.IsNullOrWhiteSpace(outDir.Value))
                a.OutDir = outDir.Value;

            if (args.SingleOrDefault(a => a.Parameters.Contains("--data-path")) is ArgStore<string> dataPath
                && !string.IsNullOrWhiteSpace(dataPath.Value))
                a.DataPath = dataPath.Value;
            if (a.Sign && string.IsNullOrWhiteSpace(a.SigningKeyPath))
                throw new ArgumentException("Signing key path must be provided when --sign is true");

            return a;
        }


        public IEnumerable<string> Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) yield return "--name is required";
            if (string.IsNullOrWhiteSpace(Version)) yield return "--version is required";
            if (string.IsNullOrWhiteSpace(PublishDir)) yield return "--publish is required";
            if (!Directory.Exists(PublishDir)) yield return $"--publish directory not found: {PublishDir}";

            // Unset string args come back as empty (not null), so test for content.
            bool cfPartial = !string.IsNullOrWhiteSpace(CfTunnelId) || !string.IsNullOrWhiteSpace(CfTunnelName) || !string.IsNullOrWhiteSpace(CfHostname);
            if (cfPartial && !HasCloudflare)
                yield return "Cloudflare flags are incomplete — provide all: --cf-tunnel-id, --cf-tunnel-name, --cf-hostname, --cf-credentials";
        }

        public bool HasCloudflare =>
            !string.IsNullOrWhiteSpace(CfTunnelId) && !string.IsNullOrWhiteSpace(CfTunnelName)
            && !string.IsNullOrWhiteSpace(CfHostname) && !string.IsNullOrWhiteSpace(CfCredentials);
    }

    // ── Deploy Arguments ─────────────────────────────────────────────────────────

    public class DeployArgs
    {
        public string PackagePath { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int SshPort { get; set; } = 22;
        public string SshUser { get; set; } = string.Empty;
        public string KeyPath { get; set; } = string.Empty;
        public string? Profile { get; set; }
        public bool Force { get; set; } = false;  // skip trust prompt
        public int ErrLines { get; set; } = 30;     // journal lines on failure
        public string? PubKeyPath { get; set; }         // override public key for verification
        public bool NoVerify { get; set; } = false;  // skip signature check (use with caution)
        public int MaxSnapshots { get; set; } = bangka_lib.Constants.DefaultMaxSnapshots;

        public static DeployArgs Parse(ArgInvoke argInvoke)
        {

            var args = argInvoke.GetArgStoreValues();
            var a = new DeployArgs();

            if (args.SingleOrDefault(a => a.Parameters.Contains("--package")) is ArgStore<string> package) a.PackagePath = package.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--ssh-host")) is ArgStore<string> host) a.Host = host.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--ssh-port")) is ArgStore<int> port) a.SshPort = port.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--profile")) is ArgStore<string> profile) a.Profile = profile.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--force")) is ArgStore<bool> force) a.Force = force.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--err-lines")) is ArgStore<int> errLines && errLines.TypedValue != 0) a.ErrLines = errLines.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--pub-key")) is ArgStore<string> pubKey) a.PubKeyPath = pubKey.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--ssh-key")) is ArgStore<string> sshKey) a.KeyPath = sshKey.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--ssh-user")) is ArgStore<string> sshUser) a.SshUser = sshUser.Value;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--no-verify")) is ArgStore<bool> noVer) a.NoVerify = noVer.TypedValue;
            if (args.SingleOrDefault(a => a.Parameters.Contains("--max-snapshots")) is ArgStore<int> maxSnap) a.MaxSnapshots = maxSnap.TypedValue;

            return a;
        }

        public IEnumerable<string> Validate()
        {
            // If a profile is supplied, missing fields will be filled in before Validate() is called
            if (string.IsNullOrWhiteSpace(PackagePath)) yield return "--package is required (or set outDir in profile)";
            if (!string.IsNullOrWhiteSpace(PackagePath) && !File.Exists(PackagePath)) yield return $"Package file not found: {PackagePath}";
            if (string.IsNullOrWhiteSpace(Host)) yield return "--ssh-host is required (or set in profile)";
            if (!string.IsNullOrWhiteSpace(KeyPath) && !File.Exists(KeyPath)) yield return $"SSH key not found: {KeyPath}";
        }
    }

}
