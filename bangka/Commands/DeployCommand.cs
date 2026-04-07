using System;
using System.IO.Compression;
using ArgSharp.Args;
using bangka.Objects;
using bangka.Properties;
using bangka_lib;
using bangka_lib.Objects;
using Spectre.Console;
using static bangka.Commands.Arguments;

namespace bangka.Commands;

public static class DeployCommand
{
    public static void Load(ArgInvoke invoke)
    {
        LoadDeployArgs(invoke);
    }
    public static int Run(ArgInvoke invoke)
    {
        return RunAsync(invoke).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(ArgInvoke invoke)
    {
        DeployArgs opts;
        try { opts = DeployArgs.Parse(invoke); }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Argument error:[/] {ex.Message}");
            return 1;
        }

        // ── Load profile if supplied, CLI flags override profile values ─────
        DeploymentProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(opts.Profile))
        {
            try
            {
                profile = DeploymentProfile.Load(opts.Profile);
                opts = profile.ApplyToDeployArgs(opts);
                AnsiConsole.MarkupLine($"[grey]Profile loaded: {opts.Profile}[/]");

                // Auto-resolve package path from profile outDir + name if not supplied
                if (string.IsNullOrWhiteSpace(opts.PackagePath) && profile != null)
                {
                    var pkgDir = string.IsNullOrWhiteSpace(profile.OutDir) ? "." : profile.OutDir;
                    var pkgName = $"{profile.Name}-{profile.Version}.aspkg";
                    var pkgPath = Path.Combine(pkgDir, pkgName);
                    if (File.Exists(pkgPath))
                    {
                        opts.PackagePath = pkgPath;
                        AnsiConsole.MarkupLine($"[grey]Package resolved from profile: {pkgPath}[/]");
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                AnsiConsole.MarkupLine($"[red]Profile error:[/] {ex.Message}");
                return 1;
            }
        }

        var errors = opts.Validate().ToList();
        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Validation failed:[/]");
            foreach (var e in errors)
                AnsiConsole.MarkupLine($"  [red]•[/] {e}");
            return 1;
        }

        // ── Extract metadata from the package before connecting ───────────────
        PackageMetadata meta;
        SystemdServiceConfig svcConfig;
        CloudflaredConfig? cfConfig = null;

        var extractDir = Path.Combine(Path.GetTempPath(), $"bangka-pre-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(opts.PackagePath, extractDir);
            meta = PackageMetadata.Deserialize(Path.Combine(extractDir, "metadata.xml"));
            svcConfig = SystemdServiceConfig.Deserialize(Path.Combine(extractDir, "systemdservice.xml"));

            var cfXml = Path.Combine(extractDir, "cloudflared.xml");
            if (meta.HasCloudflare && File.Exists(cfXml))
                cfConfig = CloudflaredConfig.Deserialize(cfXml);
        }
        finally
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
        }

        // Signature verification is handled in Step 2.5 after SSH connection,
        // using the server's own trusted.pub as the authority if it exists.
        // Pre-connection: only show a warning if the package is unsigned.
        if (!File.Exists(opts.PackagePath + ".sig") && !opts.NoVerify && string.IsNullOrWhiteSpace(opts.PubKeyPath))
            AnsiConsole.MarkupLine("[yellow]⚠  Package is unsigned. Build with --sign for security.[/]\n");
        else if (!File.Exists(opts.PackagePath + ".sig") && !string.IsNullOrWhiteSpace(opts.PubKeyPath))
        {
            AnsiConsole.MarkupLine("[bold red]✗ --pub-key specified but no .sig file found.[/]");
            AnsiConsole.MarkupLine($"  [red]Expected: {opts.PackagePath}.sig[/]\n");
            return 1;
        }

        AnsiConsole.Write(new Rule(
                $"[bold cyan]Deploying [white]{Markup.Escape(meta.Name)}[/] v[white]{Markup.Escape(meta.Version)}[/] to [white]{Markup.Escape(opts.Host)}:{Markup.Escape(opts.SshPort.ToString())}[/][/]")
            .RuleStyle("cyan"));
        AnsiConsole.WriteLine();

        // ── STEP 1 — SSH connect + fingerprint check ──────────────────────────
        BeginStep(1, "Establishing SSH connection");
        var knownHosts = new KnownHostsStore();
        SshSession session;
        try
        {
            session = SshSession.Connect(
                opts.Host, opts.SshPort, opts.SshUser, opts.KeyPath,
                knownHosts, opts.Force);
        }
        catch (Exception ex)
        {
            Fail($"SSH connection failed: {ex.Message}");
            return 1;
        }

        using (session)
        {
            var pubKey = SshSession.GetPublicKey(opts.KeyPath);
            session.Run("mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            var (_, authKeys, _) = session.Run("cat ~/.ssh/authorized_keys");
            if (!authKeys.Contains(pubKey.Split(' ')[1]))
            {
                session.Run($"echo '{pubKey}' >> ~/.ssh/authorized_keys");
                AnsiConsole.MarkupLine("  [grey]Public key added to remote authorized_keys.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]Public key already present on remote.[/]");
            }
            Pass("SSH connection established and key registered.");

            // ── Env file push (if profile has envVars defined) ────────────────
            if (profile != null && profile.EnvVars.Count > 0 &&
                !string.IsNullOrWhiteSpace(profile.EnvFile))
            {
                AnsiConsole.MarkupLine($"  [grey]Pushing {profile.EnvVars.Count} env var(s) to {profile.EnvFile}...[/]");
                var envContent = profile.ToEnvFileContent();
                var envB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(envContent));
                var envDir = System.IO.Path.GetDirectoryName(profile.EnvFile)!;
                session.Run($"mkdir -p {envDir}");
                session.RunOrThrow($"echo {envB64} | base64 -d > {profile.EnvFile}");
                session.Run($"chmod 600 {profile.EnvFile}");
                AnsiConsole.MarkupLine($"  [grey]Env file written to {profile.EnvFile}[/]");
            }

            // ── STEP 2 — Privilege check ──────────────────────────────────────
            BeginStep(2, "Verifying remote privileges");
            var (whoCode, whoOut, _) = session.Run("id -u");
            var isRoot = whoCode == 0 && whoOut.Trim() == "0";
            session.SetIsRoot(isRoot);

            if (!isRoot)
            {
                // Non-root — verify passwordless sudo is available for systemctl
                AnsiConsole.MarkupLine($"  [grey]Connected as non-root user '{opts.SshUser}' — checking sudo...[/]");
                var (sudoCode, _, sudoErr) = session.Run("sudo -n systemctl --version 2>&1");
                if (sudoCode != 0)
                {
                    Fail($"User '{opts.SshUser}' is not root and passwordless sudo is not configured.\n" +
                         $"  Run on the server: bangkactl deploy-user init\n" +
                         $"  Then deploy with:  --user bangka --key <private-key>");
                    return 1;
                }
                AnsiConsole.MarkupLine("  [grey]Passwordless sudo confirmed.[/]");
                Pass($"User '{opts.SshUser}' has passwordless sudo — proceeding.");
            }
            else
            {
                Pass("Remote is running as root — service management permitted.");
            }

            // ── STEP 2.5 — Query client trust enforcement ────────────────────
            BeginStep(3, "Checking server trust enforcement");

            var trustPubPath = "/etc/bangka/trusted.pub";
            var (trustExists, _, _) = session.Run($"test -f {trustPubPath} && echo yes");

            if (trustExists == 0)
            {
                // Server has a trust key — package MUST be signed and verifiable
                AnsiConsole.MarkupLine("  [cyan]Server trust enforcement is ACTIVE.[/]");

                var (_, serverPubPem, _) = session.Run($"cat {trustPubPath}");
                var (_, serverFp, _) = session.Run("cat /etc/bangka/trusted.fingerprint 2>/dev/null || echo unknown");
                AnsiConsole.MarkupLine($"  [grey]Server key fingerprint: {Markup.Escape(serverFp.Trim())}[/]");

                // Check the package is signed
                var sigPath = opts.PackagePath + ".sig";
                if (!File.Exists(sigPath))
                {
                    Fail("Server requires signed packages but no .sig file found.\n" +
                         $"  Expected: {sigPath}\n" +
                         "  Build with --sign and ensure the signing key matches the server's trusted.pub.");
                    return 1;
                }

                // Write the server's public key to a temp file and verify locally
                var tmpPub = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(tmpPub, serverPubPem.Trim());
                    var (sigOk, sigMsg) = PackageSigner.VerifyPackage(opts.PackagePath, tmpPub);
                    if (!sigOk)
                    {
                        Fail($"Package signature does not satisfy server trust key.\n" +
                             $"  {sigMsg}\n" +
                             "  Ensure the package was signed with the key matching this server's trusted.pub.");
                        return 1;
                    }
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(sigMsg)}[/]");
                }
                finally
                {
                    File.Delete(tmpPub);
                }
                Pass("Package signature verified against server trust key.");
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]No trust key on server — enforcement inactive.[/]");

                // Still run the local sig check from the previous step (warn only)
                var sigPath = opts.PackagePath + ".sig";
                if (File.Exists(sigPath) && !opts.NoVerify)
                {
                    var (sigOk, sigMsg) = PackageSigner.VerifyPackage(opts.PackagePath, opts.PubKeyPath);
                    if (!sigOk)
                    {
                        Fail($"Local signature check failed: {sigMsg}");
                        return 1;
                    }
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(sigMsg)}[/]");
                }
                Pass("Trust check complete (enforcement not configured on server).");
            }

            // ── STEP 3 — Remote prerequisite checks ───────────────────────────
            BeginStep(4, "Checking remote prerequisites");
            var prereqFailed = false;

            // dotnet runtime
            var (dotnetCode, dotnetVer, _) = session.Run("dotnet --list-runtimes 2>/dev/null | head -1");
            if (dotnetCode != 0 || string.IsNullOrWhiteSpace(dotnetVer))
            {
                AnsiConsole.MarkupLine("  [red]✗ dotnet runtime not found.[/] Install with: [grey]sudo apt install -y dotnet-runtime-8.0[/]");
                prereqFailed = true;
            }
            else
            {
                AnsiConsole.MarkupLine($"  [grey]dotnet: {Markup.Escape(dotnetVer.Trim())}[/]");
            }

            // unzip
            var (unzipCode, _, _) = session.Run("which unzip");
            if (unzipCode != 0)
            {
                AnsiConsole.MarkupLine("  [red]✗ unzip not found.[/] Install with: [grey]sudo apt install -y unzip[/]");
                prereqFailed = true;
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]unzip: present[/]");
            }

            // python3 (reliable extraction fallback)
            var (py3Code, py3Ver, _) = session.Run("python3 --version 2>&1");
            AnsiConsole.MarkupLine(py3Code == 0
                ? $"  [grey]python3: {Markup.Escape(py3Ver.Trim())}[/]"
                : "  [yellow]! python3 not found — will use unzip only.[/]");

            // sha512sum
            var (shaCode, _, _) = session.Run("which sha512sum");
            if (shaCode != 0)
            {
                AnsiConsole.MarkupLine("  [red]✗ sha512sum not found.[/] Install with: [grey]sudo apt install -y coreutils[/]");
                prereqFailed = true;
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]sha512sum: present[/]");
            }

            // systemctl
            var (sdCode, _, _) = session.Run("which systemctl");
            if (sdCode != 0)
            {
                AnsiConsole.MarkupLine("  [red]✗ systemctl not found — is this a systemd system?[/]");
                prereqFailed = true;
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]systemctl: present[/]");
            }

            // EnvironmentFile existence check (if package requires one)
            if (!string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
            {
                var (envFileCode, _, _) = session.Run($"test -f {svcConfig.EnvironmentFile} && echo yes");
                if (envFileCode != 0)
                {
                    AnsiConsole.MarkupLine($"  [yellow]! EnvironmentFile '{svcConfig.EnvironmentFile}' does not exist on remote.[/]");
                    AnsiConsole.MarkupLine("  [yellow]  Service may fail to start. Create it before deploying.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [grey]EnvironmentFile: {svcConfig.EnvironmentFile} (found)[/]");
                }
            }

            // ── Env file key diff check ──────────────────────────────────────
            if (meta.RequiredEnvKeys.Count > 0 && !string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
            {
                var (envExists2, _, _) = session.Run($"test -f {svcConfig.EnvironmentFile} && echo yes");
                if (envExists2 == 0)
                {
                    var (_, remoteEnvContent, _) = session.Run($"cat {svcConfig.EnvironmentFile}");
                    var remoteKeys = remoteEnvContent
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !l.TrimStart().StartsWith('#') && l.Contains('='))
                        .Select(l => l.Split('=')[0].Trim())
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .ToHashSet();

                    var missingKeys = meta.RequiredEnvKeys.Where(k => !remoteKeys.Contains(k)).ToList();
                    var extraKeys = remoteKeys.Where(k => !meta.RequiredEnvKeys.Contains(k)).ToList();

                    if (missingKeys.Count > 0 || extraKeys.Count > 0)
                    {
                        AnsiConsole.MarkupLine("\n  [red]Environment file key mismatch detected:[/]");

                        if (missingKeys.Count > 0)
                        {
                            AnsiConsole.MarkupLine("  [red]Missing on remote (required by package):[/]");
                            foreach (var k in missingKeys)
                                AnsiConsole.MarkupLine($"    [red]- {Markup.Escape(k)}[/]");
                        }

                        if (extraKeys.Count > 0)
                        {
                            AnsiConsole.MarkupLine("  [yellow]Extra on remote (not in package):[/]");
                            foreach (var k in extraKeys)
                                AnsiConsole.MarkupLine($"    [yellow]+ {Markup.Escape(k)}[/]");
                        }

                        AnsiConsole.MarkupLine("\n  [grey]Aborting — update the remote env file to match the required keys and retry.[/]");
                        prereqFailed = true;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [grey]Env file keys: all {meta.RequiredEnvKeys.Count} required key(s) present.[/]");
                    }
                }
            }

            if (prereqFailed)
            {
                Fail("One or more prerequisites are missing on the remote host. Install them and retry.");
                return 1;
            }
            Pass("All prerequisites satisfied.");

            // ── STEP 4 — File transfer ────────────────────────────────────────
            BeginStep(5, "Transferring package");
            var remoteTemp = $"/tmp/{meta.Name}-{meta.Version}-{Guid.NewGuid():N}.aspkg";
            long fileSize = new FileInfo(opts.PackagePath).Length;
            AnsiConsole.MarkupLine($"  [grey]Package size: {fileSize / 1024.0:F1} KB[/]");

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new TransferSpeedColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var uploadTask = ctx.AddTask("[cyan]Uploading via SCP[/]", maxValue: fileSize);
                    await Task.Run(() =>
                        session.Upload(opts.PackagePath, remoteTemp, (uploaded, total) =>
                        {
                            uploadTask.Value = uploaded;
                        }));
                    uploadTask.Value = fileSize;
                    uploadTask.StopTask();
                });
            Pass($"Package uploaded to {remoteTemp}");

            // ── STEP 5 — SHA-512 checksum ─────────────────────────────────────
            BeginStep(6, "Verifying SHA-512 checksum");
            var (_, remoteHash, _) = session.Run($"sha512sum {remoteTemp} | awk '{{print $1}}'");
            remoteHash = remoteHash.Trim();

            AnsiConsole.MarkupLine($"  [grey]Expected: {meta.Checksum[..32]}...[/]");
            AnsiConsole.MarkupLine($"  [grey]Received: {(remoteHash.Length >= 32 ? remoteHash[..32] : remoteHash)}...[/]");

            var localHash = ChecksumHelper.ComputeSha512(opts.PackagePath);
            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                session.Run($"rm -f {remoteTemp}");
                Fail($"Checksum mismatch — transfer may be corrupt.");
                return 1;
            }
            Pass("SHA-512 checksum verified — package integrity confirmed.");

            // ── STEP 6 — Rollback snapshot + service registration ─────────────
            BeginStep(7, "Creating rollback snapshot and service registration");

            var (_, remoteHome, _) = session.Run("echo $HOME");
            remoteHome = remoteHome.Trim();
            if (string.IsNullOrWhiteSpace(remoteHome)) remoteHome = $"/home/{opts.SshUser}";
            AnsiConsole.MarkupLine($"  [grey]Remote home: {remoteHome}[/]");

            var installBase = $"{remoteHome}/bangkasvcs";
            var installPath = $"{installBase}/{meta.Name}";
            var serviceFile = $"/etc/systemd/system/{meta.Name}.service";
            var rollbackDir = $"{installBase}/.rollback/{meta.Name}";

            session.RunPrivileged($"mkdir -p {rollbackDir}");

            // ── Same-version / same-checksum guard ────────────────────────────
            var remoteMetaPath = $"{installPath}/.bangka-meta.xml";
            var (metaExists, _, _) = session.Run($"test -f {remoteMetaPath} && echo yes");
            if (metaExists == 0)
            {
                var (_, remoteMetaXml, _) = session.Run($"cat {remoteMetaPath}");
                if (!string.IsNullOrWhiteSpace(remoteMetaXml))
                {
                    try
                    {
                        var tmpMeta = Path.GetTempFileName();
                        File.WriteAllText(tmpMeta, remoteMetaXml);
                        var installedMeta = PackageMetadata.Deserialize(tmpMeta);
                        File.Delete(tmpMeta);

                        bool sameVersion = installedMeta.Version == meta.Version;
                        bool sameChecksum = installedMeta.Checksum == meta.Checksum;

                        if (sameVersion && sameChecksum)
                        {
                            AnsiConsole.MarkupLine($"  [yellow]Same package already deployed:[/] [white]{meta.Name}[/] v[white]{meta.Version}[/] (checksum matches).");
                            var redeploy = AnsiConsole.Confirm("  Redeploy anyway?", defaultValue: false);
                            if (!redeploy)
                            {
                                AuditLog.Record(new AuditRecord
                                {
                                    Action = "deploy",
                                    PackageName = meta.Name,
                                    Version = meta.Version,
                                    Checksum = meta.Checksum,
                                    Host = opts.Host,
                                    SshUser = opts.SshUser,
                                    KeyPath = opts.KeyPath,
                                    Signed = File.Exists(opts.PackagePath + ".sig"),
                                    Result = "cancelled",
                                    Note = "Same version already deployed"
                                });
                                AnsiConsole.MarkupLine("[grey]  Deployment cancelled.[/]");
                                return 0;
                            }
                        }
                        else if (sameVersion && !sameChecksum)
                        {
                            AnsiConsole.MarkupLine($"  [yellow]Version matches but checksum differs — package was rebuilt.[/]");
                        }
                    }
                    catch { /* ignore parse errors on remote meta */ }
                }
            }

            var (existCode, _, _) = session.Run($"test -d {installPath} && echo 'exists'");
            if (existCode == 0)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var snap = $"{rollbackDir}/{stamp}";
                session.RunPrivileged($"cp -a {installPath} {snap}");
                AnsiConsole.MarkupLine($"  [grey]Rollback snapshot created: {snap}[/]");
            }

            session.RunPrivileged($"mkdir -p {installPath}");

            var (svcExists, _, _) = session.Run($"test -f {serviceFile} && echo 'exists'");
            if (svcExists != 0)
                AnsiConsole.MarkupLine($"  [grey]No existing service unit — creating {serviceFile}[/]");
            else
            {
                AnsiConsole.MarkupLine("  [grey]Existing service unit found — will overwrite.[/]");
                session.RunPrivileged($"systemctl stop {meta.Name} 2>/dev/null || true");
            }

            // Resolve dotnet on remote
            var (_, dotnetPath, _) = session.Run("which dotnet");
            var dotnetExe = dotnetPath.Trim();
            if (string.IsNullOrEmpty(dotnetExe)) dotnetExe = "/usr/bin/dotnet";

            svcConfig.WorkingDirectory = installPath;
            svcConfig.ExecStart = $"{dotnetExe} {installPath}/{meta.EntryDll}";
            AnsiConsole.MarkupLine($"  [grey]ExecStart: {svcConfig.ExecStart}[/]");

            if (!string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
                AnsiConsole.MarkupLine($"  [grey]EnvironmentFile: {svcConfig.EnvironmentFile}[/]");

            var localUnitTmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(localUnitTmp, svcConfig.ToUnitFileContent());
                var remoteUnitTmp = $"/tmp/{meta.Name}-{Guid.NewGuid():N}.service";
                session.Upload(localUnitTmp, remoteUnitTmp);
                session.RunPrivilegedOrThrow($"mv {remoteUnitTmp} {serviceFile}");
            }
            finally
            {
                File.Delete(localUnitTmp);
            }
            session.RunPrivileged($"chmod 644 {serviceFile}");
            session.RunPrivileged("systemctl daemon-reload");
            Pass("Rollback snapshot created and systemd unit registered.");

            // ── STEP 7 — Cloudflare prerequisites ────────────────────────────
            BeginStep(8, "Checking Cloudflare Tunnel prerequisites");
            if (meta.HasCloudflare && cfConfig != null)
            {
                var (cfInstalled, _, _) = session.Run("which cloudflared");
                if (cfInstalled != 0)
                {
                    RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                    Fail("cloudflared is not installed on the remote host.");
                    return 1;
                }
                var (cfRunning, _, _) = session.Run("systemctl is-active cloudflared");
                if (cfRunning != 0)
                {
                    RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                    Fail("cloudflared service is not active on the remote host. Start it first.");
                    return 1;
                }
                Pass("cloudflared is installed and running.");
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]No Cloudflare config in package — skipping.[/]");
                Pass("Cloudflare check skipped (not configured).");
            }

            // ── STEP 8 — Extract + install files ─────────────────────────────
            BeginStep(9, $"Installing to {installPath}");

            var remoteExtract = $"/tmp/bangka-extract-{Guid.NewGuid():N}";
            session.Run($"mkdir -p {remoteExtract}");

            // Prefer python3 for extraction (avoids unzip PATH issues over non-login SSH)
            var (py3Avail, _, _) = session.Run("which python3");
            if (py3Avail == 0)
            {
                session.RunOrThrow(
                    $"python3 -c \"import zipfile; z=zipfile.ZipFile('{remoteTemp}'); " +
                    $"[z.extract(m,'{remoteExtract}') for m in z.namelist() if m.startswith('data/')]; z.close()\"");
            }
            else
            {
                session.RunOrThrow($"unzip -q {remoteTemp} -d {remoteExtract}");
            }

            // Verify data/ dir landed
            var (dataCheck, _, _) = session.Run($"test -d {remoteExtract}/data && echo yes");
            if (dataCheck != 0)
            {
                session.Run($"rm -rf {remoteExtract} {remoteTemp}");
                RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                Fail("Extraction failed — data/ directory not found in archive.");
                return 1;
            }

            session.RunPrivilegedOrThrow($"rm -rf {installPath}");
            session.RunPrivilegedOrThrow($"mkdir -p {installPath}");
            session.RunPrivilegedOrThrow($"cp -r {remoteExtract}/data/. {installPath}/");

            // Verify entry DLL landed
            var (dllCode, dllOut, _) = session.Run(
                $"test -f {installPath}/{meta.EntryDll} && echo 'found' || echo 'MISSING'");
            var dllStatus = dllOut.Trim();
            AnsiConsole.MarkupLine(
                $"  [grey]Entry DLL ({Markup.Escape(meta.EntryDll)}): " +
                $"{(dllStatus == "found" ? "[green]found[/]" : "[red]MISSING[/]")}[/]");

            if (dllStatus != "found")
            {
                session.Run($"rm -rf {remoteExtract} {remoteTemp}");
                RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                Fail($"Entry DLL '{meta.EntryDll}' was not found after extraction. Check your --dll flag.");
                return 1;
            }

            // Write cloudflared config if applicable
            if (meta.HasCloudflare && cfConfig != null)
            {
                session.Run("mkdir -p /etc/cloudflared");
                var cfMainConfig = "/etc/cloudflared/config.yml";
                var cfCfgPath = $"/etc/cloudflared/{meta.Name}.yml";

                // Check if a global cloudflared config already exists
                var (mainExists, _, _) = session.Run($"test -f {cfMainConfig} && echo yes");
                if (mainExists == 0)
                {
                    // Append-mode: check if this hostname is already registered
                    var (_, cfYamlContent, _) = session.Run($"cat {cfMainConfig}");
                    if (cfYamlContent.Contains(cfConfig.Hostname))
                    {
                        AnsiConsole.MarkupLine($"  [grey]Cloudflare: hostname {cfConfig.Hostname} already present in {cfMainConfig} — skipping.[/]");
                    }
                    else
                    {
                        // Insert new ingress rule before the catch-all http_status:404 line
                        var ingressRule = EscapeForShell(cfConfig.ToIngressRule());
                        session.Run(
                            $"sed -i '/  - service: http_status:404/i {cfConfig.ToIngressRule().Replace("/", @"\/")}' {cfMainConfig} 2>/dev/null " +
                            $"|| printf '\n{cfConfig.ToIngressRule()}\n' >> {cfMainConfig}");
                        AnsiConsole.MarkupLine($"  [grey]Cloudflare: ingress rule for {cfConfig.Hostname} appended to {cfMainConfig}[/]");
                    }
                }
                else
                {
                    // No global config — write a per-service config file
                    var cfYaml = EscapeForShell(cfConfig.ToConfigYaml());
                    session.Run($"printf '%s' {cfYaml} > {cfCfgPath}");
                    AnsiConsole.MarkupLine($"  [grey]Cloudflare: new config written to {cfCfgPath}[/]");
                }

                // Reload cloudflared to pick up the change
                session.Run("systemctl reload cloudflared 2>/dev/null || systemctl restart cloudflared 2>/dev/null || true");
                AnsiConsole.MarkupLine("  [grey]Cloudflare: cloudflared reloaded.[/]");
            }

            session.RunPrivileged($"chown -R {svcConfig.User}:{svcConfig.User} {installPath}");
            session.RunPrivileged($"chmod -R o+rX {installPath}");
            session.RunPrivileged($"chmod +x {installPath}/{meta.Name} 2>/dev/null || true");

            // Persist metadata to install dir for future same-version checks
            // Persist metadata to install dir for future same-version checks
            var metaXml = $"""<?xml version="1.0"?><metadata><n>{meta.Name}</n><version>{meta.Version}</version><checksum>{meta.Checksum}</checksum><builtAt>{meta.BuiltAt}</builtAt><entryDll>{meta.EntryDll}</entryDll></metadata>""";
            var localMetaTmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(localMetaTmp, metaXml);
                var remoteMetaTmp = $"/tmp/{meta.Name}-meta-{Guid.NewGuid():N}.xml";
                session.Upload(localMetaTmp, remoteMetaTmp);
                session.RunPrivilegedOrThrow($"mv {remoteMetaTmp} {installPath}/.bangka-meta.xml");
            }
            finally
            {
                File.Delete(localMetaTmp);
            }

            session.Run($"rm -rf {remoteExtract} {remoteTemp}");
            Pass($"Files installed to {installPath}.");

            // ── STEP 9 — Start service and health check ───────────────────────
            BeginStep(10, "Starting service and verifying health");

            session.RunPrivileged($"systemctl enable {meta.Name}");
            session.RunPrivileged($"systemctl start {meta.Name}");

            bool healthy = false;
            const int maxAttempts = 8;
            Thread.Sleep(4000); // initial grace period for process to spawn
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var (_, activeOut, _) = session.Run($"systemctl is-active {meta.Name}");
                var status = activeOut.Trim();
                AnsiConsole.MarkupLine($"  [grey]Attempt {attempt}/{maxAttempts} — status: {status}[/]");
                if (status == "active")
                {
                    healthy = true;
                    break;
                }
                if (status == "failed" || status == "inactive")
                    break; // definitive failure states — no point waiting
                // activating = still starting, keep polling
                Thread.Sleep(3000);
            }

            if (!healthy)
            {
                AnsiConsole.MarkupLine("\n[red]Service failed to start — gathering diagnostics...[/]\n");

                // ── systemctl status (human-friendly summary) ─────────────────
                var (_, statusOut, _) = session.Run($"systemctl status {meta.Name} --no-pager -l");
                AnsiConsole.Write(new Rule("[red]systemctl status[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(statusOut)}[/]");

                // ── journal lines (configurable via --err-lines) ──────────────
                var (_, journalOut, _) = session.RunPrivileged(
                    $"journalctl -u {meta.Name} --no-pager -n {opts.ErrLines} --output short-precise");
                AnsiConsole.Write(new Rule($"[red]Journal (last {opts.ErrLines} lines)[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(journalOut)}[/]");

                // ── unit file (so user can see exactly what was written) ───────
                var (_, unitOut, _) = session.Run($"cat {serviceFile}");
                AnsiConsole.Write(new Rule("[red]Unit file[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(unitOut)}[/]");

                // ── EnvironmentFile hint if set but missing ───────────────────
                if (!string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
                {
                    var (envMissing, _, _) = session.Run(
                        $"test -f {svcConfig.EnvironmentFile} && echo ok || echo missing");
                    if (envMissing != 0 || true) // re-check at failure time
                    {
                        var (_, envStatus, _) = session.Run(
                            $"test -f {svcConfig.EnvironmentFile} && echo ok || echo missing");
                        if (envStatus.Trim() == "missing")
                            AnsiConsole.MarkupLine(
                                $"\n[yellow]Hint:[/] EnvironmentFile [white]{svcConfig.EnvironmentFile}[/] " +
                                $"does not exist on the remote — this is likely the cause of startup failure.\n" +
                                $"Create it with the required variables and run [cyan]systemctl start {meta.Name}[/] manually.");
                    }
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Initiating rollback...[/]");
                RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                AuditLog.Record(new AuditRecord
                {
                    Action = "deploy",
                    PackageName = meta.Name,
                    Version = meta.Version,
                    Checksum = meta.Checksum,
                    Host = opts.Host,
                    SshUser = opts.SshUser,
                    KeyPath = opts.KeyPath,
                    Signed = File.Exists(opts.PackagePath + ".sig"),
                    Result = "failed",
                    Note = "Service did not reach active state"
                });
                Fail("Service did not reach 'active' state. Rollback complete.");
                return 1;
            }

            Pass("Service is active and healthy.");
        }

        AuditLog.Record(new AuditRecord
        {
            Action = "deploy",
            PackageName = meta.Name,
            Version = meta.Version,
            Checksum = meta.Checksum,
            Host = opts.Host,
            SshUser = opts.SshUser,
            KeyPath = opts.KeyPath,
            Signed = File.Exists(opts.PackagePath + ".sig"),
            Result = "success"
        });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"[bold green]✓[/] [bold white]{meta.Name}[/] v[white]{meta.Version}[/] deployed successfully to [white]{opts.Host}[/]")
            .Header("[bold green] Deployment Complete [/]")
            .BorderColor(Color.Green));

        return 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RollbackAsync(
        SshSession session, string serviceName,
        string installPath, string rollbackDir, string serviceFile)
    {
        AnsiConsole.MarkupLine("[yellow]Rolling back...[/]");
        session.RunPrivileged($"systemctl stop {serviceName} 2>/dev/null || true");
        session.RunPrivileged($"systemctl disable {serviceName} 2>/dev/null || true");

        var (_, snapList, _) = session.Run($"ls -1t {rollbackDir} 2>/dev/null | head -1");
        var latestSnap = snapList.Trim();
        if (!string.IsNullOrEmpty(latestSnap))
        {
            session.Run($"rm -rf {installPath}");
            session.Run($"cp -a {rollbackDir}/{latestSnap} {installPath}");
            session.RunPrivileged($"systemctl start {serviceName} 2>/dev/null || true");
            AnsiConsole.MarkupLine($"  [grey]Restored snapshot: {latestSnap}[/]");
        }
        else
        {
            session.Run($"rm -rf {installPath}");
            session.Run($"rm -f {serviceFile}");
            session.RunPrivileged("systemctl daemon-reload");
            AnsiConsole.MarkupLine("  [grey]No prior snapshot — installation fully removed.[/]");
        }

    }

    private static string EscapeForShell(string content)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        return $"\"$(echo {b64} | base64 -d)\"";
    }

    private static void BeginStep(int n, string description) =>
        AnsiConsole.MarkupLine($"[bold cyan]Step {n}[/] [white]{description}[/]");

    private static void Pass(string msg) =>
        AnsiConsole.MarkupLine($"  [bold green]✓[/] {Markup.Escape(msg)}\n");

    private static void Fail(string msg) =>
        AnsiConsole.MarkupLine($"  [bold red]✗[/] {Markup.Escape(msg)}\n");

    static void PrintUsage()
    {
        AnsiConsole.MarkupLine("\n[bold]Usage:[/] bangka deploy [[options]]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[cyan]Flag[/]")
            .AddColumn("[grey]Required[/]")
            .AddColumn("[grey]Description[/]");

        table.AddRow("--package", "[red]yes[/]", "Path to .aspkg file");
        table.AddRow("--host", "[red]yes[/]", "Remote hostname or Tailscale IP");
        table.AddRow("--user", "[red]yes[/]", "SSH username (must be root on the remote)");
        table.AddRow("--key", "[red]yes[/]", "Path to SSH private key");
        table.AddRow("--port", "no", "SSH port (default: 22)");
        table.AddRow("--force", "no", "Skip fingerprint trust prompts");
        table.AddRow("--err-lines", "no", "Journal lines shown on failure (default: 30)");

        AnsiConsole.Write(table);
    }
}
