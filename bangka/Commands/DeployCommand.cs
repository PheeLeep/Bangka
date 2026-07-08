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
        // Also accept build inputs so `deploy` can build the package inline when
        // --package is omitted (ArgSharp.IgnoreConflictArgument tolerates the
        // --profile/--out flags shared with the deploy arg set).
        LoadBuildArgs(invoke);
        invoke.AddArgument<bool>(["--rebuild"], helpMsg: "Rebuild the package before deploying even if it already exists");
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
                AnsiConsole.MarkupLine($"Profile loaded: [bold]{opts.Profile}[/]");

                // Auto-resolve package path from profile outDir + name if not supplied
                if (string.IsNullOrWhiteSpace(opts.PackagePath) && profile != null)
                {
                    var pkgDir = string.IsNullOrWhiteSpace(profile.OutDir) ? "." : profile.OutDir;
                    var pkgName = $"{profile.Name}-{profile.Version}.bangka";
                    var pkgPath = Path.Combine(pkgDir, pkgName);
                    if (File.Exists(pkgPath))
                    {
                        opts.PackagePath = pkgPath;
                        AnsiConsole.MarkupLine($"Package resolved from profile: [bold]{pkgPath}[/]");
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                AnsiConsole.MarkupLine($"[red]Profile error:[/] {ex.Message}");
                return 1;
            }
        }

        // ── Build the package inline when one wasn't supplied (or --rebuild) ──
        var rebuild = (invoke.GetArgStoreValues()
            .SingleOrDefault(a => a.Parameters.Contains("--rebuild")) as ArgStore<bool>)?.TypedValue == true;
        var signRequested = (invoke.GetArgStoreValues()
            .SingleOrDefault(a => a.Parameters.Contains("--sign")) as ArgStore<bool>)?.TypedValue == true;
        var signingKey = (invoke.GetArgStoreValues()
            .SingleOrDefault(a => a.Parameters.Contains("--signing-key")) as ArgStore<string>)?.Value;
        bool builtInline = false;
        if (string.IsNullOrWhiteSpace(opts.PackagePath) || rebuild)
        {
            BuildArgs? bopts = null;
            try
            {
                bopts = BuildArgs.Parse(invoke);
                if (profile != null) bopts = profile.ApplyToBuildArgs(bopts);
            }
            catch (ArgumentException) { /* e.g. --sign without key; BuildCommand.Run will report */ }

            bool canBuild = bopts != null
                && !string.IsNullOrWhiteSpace(bopts.Name)
                && !string.IsNullOrWhiteSpace(bopts.Version)
                && !string.IsNullOrWhiteSpace(bopts.PublishDir);

            if (rebuild && !canBuild)
            {
                AnsiConsole.MarkupLine("[red]--rebuild requires build inputs (--publish/--name/--version, or a profile).[/]");
                return 1;
            }

            if (canBuild)
            {
                var outDir = string.IsNullOrWhiteSpace(bopts!.OutDir) ? "." : bopts.OutDir;
                var expectedPkg = Path.Combine(outDir, $"{bopts.Name}-{bopts.Version}.bangka");
                if (rebuild || !File.Exists(expectedPkg))
                {
                    AnsiConsole.MarkupLine("[grey]No package supplied — building inline...[/]\n");
                    var rc = BuildCommand.Run(invoke);
                    if (rc != 0) return rc;
                    builtInline = true;   // BuildCommand already signed it if --sign was set
                    AnsiConsole.WriteLine();
                }
                else
                {
                    AnsiConsole.MarkupLine($"[grey]Reusing existing package: [white]{expectedPkg}[/] (use --rebuild to force).[/]");
                }
                opts.PackagePath = expectedPkg;
            }
        }

        // ── Sign the resolved package if --sign was requested but the package
        //    wasn't just built-and-signed inline (e.g. a profile-resolved or
        //    explicitly-passed --package). Without this, --sign was silently ignored.
        if (signRequested && !builtInline && !string.IsNullOrWhiteSpace(opts.PackagePath) && File.Exists(opts.PackagePath))
        {
            if (string.IsNullOrWhiteSpace(signingKey))
            {
                AnsiConsole.MarkupLine("[red]--sign requires --signing-key <path>.[/]");
                return 1;
            }
            if (!File.Exists(signingKey))
            {
                AnsiConsole.MarkupLine($"[red]Signing key not found:[/] {Markup.Escape(signingKey)}");
                return 1;
            }
            try
            {
                PackageSigner.SignPackage(opts.PackagePath, signingKey);
                AnsiConsole.MarkupLine($"[green]✓[/] Package signed → {Path.GetFileName(opts.PackagePath)}.sig");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Signing failed:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        var errors = opts.Validate().ToList();
        if (errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Validation failed:[/]");
            foreach (var e in errors)
                AnsiConsole.MarkupLine($"[red]•[/] {e}");
            return 1;
        }

        // Resolve the local .env (secrets) to ship out-of-band, if any.
        var localEnvPath = ResolveLocalEnvFile(invoke);
        if (localEnvPath != null)
            AnsiConsole.MarkupLine($"Environment file: [bold]{localEnvPath}[/] [grey](shipped out-of-band, not packaged)[/]");

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

        // The package name becomes the systemd unit name and is interpolated into
        // many remote commands — reject anything that isn't a safe unit name.
        try { ShellUtil.ValidateServiceName(meta.Name); }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Signature verification is handled in Step 2.5 after SSH connection,
        // using the server's own trusted.pub as the authority if it exists.
        // Pre-connection: only show a warning if the package is unsigned.
        if (!File.Exists(opts.PackagePath + ".sig") && !opts.NoVerify && string.IsNullOrWhiteSpace(opts.PubKeyPath))
            AnsiConsole.MarkupLine("[yellow]⚠  Package is unsigned. Build with --sign for security.[/]\n");
        else if (!File.Exists(opts.PackagePath + ".sig") && !string.IsNullOrWhiteSpace(opts.PubKeyPath))
        {
            AnsiConsole.MarkupLine("[bold red]✗ --pub-key specified but no .sig file found.[/]");
            AnsiConsole.MarkupLine($"[red]Expected: {opts.PackagePath}.sig[/]\n");
            return 1;
        }

        AnsiConsole.Write(new Rule(
                $"[bold cyan]Deploying [white]{Markup.Escape(meta.Name)}[/] v[white]{Markup.Escape(meta.Version)}[/] to [white]{Markup.Escape(opts.Host)}:{Markup.Escape(opts.SshPort.ToString())}[/][/]")
            .RuleStyle("cyan"));
        AnsiConsole.WriteLine();

        int tabCount = 0;
        // ── STEP 1 — SSH connect + fingerprint check ──────────────────────────
        MakeTitle("Establishing SSH connection");
        tabCount++;

        string sshKey = "";
        if (!string.IsNullOrWhiteSpace(opts.KeyPath))
        {
            sshKey = opts.KeyPath;
        }
        else
        {
            sshKey = Path.Combine(Constants.SSHStorage, $"{Constants.BangkaDeployUserName}");
            TabWrite(tabCount, $"[yellow][[!]][/] No SSH key specified with --key. Attempting default path: [bold]{sshKey}[/]");
        }
        if (!File.Exists(sshKey))
        {
            Fail($"SSH key not found.\n\nMake sure you have a private key from the server's deploy-user init command, or specify its path if you have one with --ssh-key.");
            return 1;
        }

        var knownHosts = new KnownHostsStore();
        SshSession session;
        try
        {
            WriteWithIcon(tabCount, IconType.Verbose, $"Authenticating...");
            session = SshSession.Connect(
                opts.Host,
                opts.SshPort,
                Constants.BangkaDeployUserName,
                sshKey,
                knownHosts,
                opts.Force);

        }
        catch (Exception ex)
        {
            Fail($"SSH connection failed: {ex.Message}");
            AnsiConsole.MarkupLine($"Make sure you run [bold]bangkactl deploy-user init[/] on the server " +
                                    $"if it's not initiated. Or the SSH key is valid.");
            return 1;
        }

        Pass(tabCount, "SSH connection established.");
        string dataPath = string.Empty;
        using (session)
        {
            var pubKey = SshSession.GetPublicKey(sshKey);
            session.Run("mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
            var (_, authKeys, _) = session.Run("cat ~/.ssh/authorized_keys");
            if (!authKeys.Contains(pubKey.Split(' ')[1]))
            {
                session.Run($"echo '{pubKey}' >> ~/.ssh/authorized_keys");
                WriteWithIcon(tabCount, IconType.Info, "Public key added to remote authorized_keys.");
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Info, "Public key already present on remote.");
            }
            Pass(tabCount, "SSH key was registered.");

            // Env vars (profile envVars + local .env) are written to a Bangka-managed
            // env file later in this deploy — see the "environment file" step. Nothing
            // to push here.

            // ── STEP 2 — Privilege check ──────────────────────────────────────
            MakeTitle("\nVerifying remote privileges");
            var (whoCode, whoOut, _) = session.Run("id -u");
            var isRoot = whoCode == 0 && whoOut.Trim() == "0";
            session.SetIsRoot(isRoot);

            if (!isRoot)
            {
                // Non-root — verify passwordless sudo is available for systemctl
                WriteWithIcon(tabCount, IconType.Info, $"Connected as non-root user '{Constants.BangkaDeployUserName}' — checking sudo...");
                var (sudoCode, _, sudoErr) = session.Run("sudo -n systemctl --version 2>&1");
                if (sudoCode != 0)
                {
                    Fail($"User '{Constants.BangkaDeployUserName}' is not root and passwordless sudo is not configured.\n" +
                         $"  Run on the server: bangkactl deploy-user init\n" +
                         $"  Then deploy with: --key <private-key>");
                    return 1;
                }
                WriteWithIcon(tabCount, IconType.Info, "Passwordless sudo confirmed.");
                Pass(tabCount, $"User '{Constants.BangkaDeployUserName}' has passwordless sudo — proceeding.");
            }
            else
            {
                Pass(tabCount, "Remote is running as root — service management permitted.");
            }

            // ── STEP 2.5 — Query client trust enforcement ────────────────────
            MakeTitle("\nChecking server trust enforcement");

            bool serverTrustActive = false;
            var trustPubPath = "/etc/bangka/trusted.pub";
            var (trustExists, _, _) = session.Run($"test -f {trustPubPath} && echo yes");

            if (trustExists == 0)
            {
                // Server has a trust key — package MUST be signed and verifiable
                serverTrustActive = true;
                WriteWithIcon(tabCount, IconType.Info, "Server trust enforcement is [green]ACTIVE.[/]");

                var (_, serverPubPem, _) = session.Run($"cat {trustPubPath}");
                var (_, serverFp, _) = session.Run("cat /etc/bangka/trusted.fingerprint 2>/dev/null || echo unknown");

                Console.WriteLine();
                AnsiConsole.Write(new Panel(new Markup($"{Markup.Escape(serverFp.Trim())}"))
                    .Header("Server Fingerprint", Justify.Center)
                    .BorderStyle(Style.Parse("grey")));
                Console.WriteLine();

                // Check the package is signed
                var sigPath = opts.PackagePath + ".sig";
                if (!File.Exists(sigPath))
                {
                    Fail("Server requires signed packages but no .sig file found.\n" +
                         $"Expected: {sigPath}\n" +
                         "Build with --sign and ensure the signing key matches the server's trusted.pub.");
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
                             $"{sigMsg}\n" +
                             "Ensure the package was signed with the key matching this server's trusted.pub.");
                        return 1;
                    }
                    WriteWithIcon(tabCount, IconType.Info, $"{Markup.Escape(sigMsg)}");
                }
                finally
                {
                    File.Delete(tmpPub);
                }
                Pass(tabCount, "Package signature verified against server trust key.");
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Warning, "No trust key on server — enforcement inactive.");

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
                    WriteWithIcon(tabCount, IconType.Info, $"{Markup.Escape(sigMsg)}");
                }
                Pass(tabCount, "Trust check complete (enforcement not configured on server).");
            }



            MakeTitle("\nRemote Prerequisite Check");
            var prereqFailed = false;

            WriteWithIcon(tabCount, IconType.Verbose, "Checking for required software on remote...");
            tabCount++;
            // dotnet runtime
            var (dotnetCode, dotnetVer, _) = session.Run("dotnet --list-runtimes 2>/dev/null | head -1");
            if (dotnetCode != 0 || string.IsNullOrWhiteSpace(dotnetVer))
            {
                WriteWithIcon(tabCount, IconType.Error, "dotnet runtime not found.");
                prereqFailed = true;
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Success, $"dotnet: [bold]{Markup.Escape(dotnetVer.Trim())}[/]");
            }

            // unzip
            var (unzipCode, _, _) = session.Run("which unzip");
            if (unzipCode != 0)
            {
                WriteWithIcon(tabCount, IconType.Error, "unzip not found.");
                prereqFailed = true;
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Success, "unzip: [bold]present[/]");
            }

            // python3 (reliable extraction fallback)
            var (py3Code, py3Ver, _) = session.Run("python3 --version 2>&1");
            WriteWithIcon(tabCount, IconType.Info, py3Code == 0
                ? $"python3: [bold]{Markup.Escape(py3Ver.Trim())}[/]"
                : "python3 not found — will use unzip only.");

            // sha512sum
            var (shaCode, _, _) = session.Run("which sha512sum");
            if (shaCode != 0)
            {
                WriteWithIcon(tabCount, IconType.Error, "sha512sum not found.");
                prereqFailed = true;
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Success, "sha512sum: [bold]present[/]");
            }

            // systemctl
            var (sdCode, _, _) = session.Run("which systemctl");
            if (sdCode != 0)
            {
                WriteWithIcon(tabCount, IconType.Error, "systemctl not found — is this a systemd system?");
                prereqFailed = true;
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Success, "systemctl: [bold]present[/]");
            }

            // The environment file is now written by Bangka from the local .env
            // (see the "environment file" step below), so there's no remote file to
            // pre-check here. Missing required keys are surfaced there as a warning.

            if (prereqFailed)
            {
                Fail("One or more prerequisites are missing on the remote host. Install them and retry.");
                return 1;
            }
            tabCount--;
            Pass(tabCount, "All prerequisites satisfied.");




            MakeTitle("\nTransferring package");
            var remoteTemp = $"/tmp/{meta.Name}-{meta.Version}-{Guid.NewGuid():N}.aspkg";
            long fileSize = new FileInfo(opts.PackagePath).Length;

            WriteWithIcon(tabCount, IconType.Info, $"Package size: {ConvertBytesToHumanReadable(fileSize)}");

            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new TransferSpeedColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var uploadTask = ctx.AddTask($"{new string(' ', tabCount * 4)}   Uploading via SCP", maxValue: fileSize);
                    await Task.Run(() =>
                        session.Upload(opts.PackagePath, remoteTemp, (uploaded, total) =>
                        {
                            uploadTask.Value = uploaded;
                        }));
                    uploadTask.Value = fileSize;
                    uploadTask.StopTask();
                });
            Pass(tabCount, $"Package uploaded ");

            // ── STEP 5 — SHA-512 checksum ─────────────────────────────────────
            MakeTitle("\nVerifying SHA-512 checksum");
            var (_, remoteHash, _) = session.Run($"sha512sum {remoteTemp} | awk '{{print $1}}'");
            remoteHash = remoteHash.Trim();

            var localHash = ChecksumHelper.ComputeSha512(opts.PackagePath);

            string localDisplay = localHash.Length >= 32 ? localHash[..32] + "..." : localHash;
            string remoteDisplay = remoteHash.Length >= 32 ? remoteHash[..32] + "..." : remoteHash;

            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                session.Run($"rm -f {ShellUtil.Quote(remoteTemp)}");
                Fail($"Checksum mismatch — transfer may be corrupt.\n\n");
                return 1;
            }
            Pass(tabCount, "SHA-512 checksum verified — package integrity confirmed.");

            // ── STEP 5.5 — Server-side signature verification ─────────────────
            // When the server enforces trust, verify the signature ON THE SERVER
            // with openssl against /etc/bangka/trusted.pub, so a tampered client
            // cannot bypass the check. The local check in Step 2.5 already passed.
            if (serverTrustActive)
            {
                MakeTitle("\nVerifying signature on server");
                var (opensslCode, _, _) = session.Run("which openssl");
                if (opensslCode != 0)
                {
                    WriteWithIcon(tabCount, IconType.Warning,
                        "openssl not found on server — relying on local signature check only.");
                }
                else
                {
                    var sigDoc = System.Text.Json.JsonSerializer.Deserialize<SignatureDocument>(
                        File.ReadAllText(opts.PackagePath + ".sig"))!;
                    var derBytes = Convert.FromBase64String(sigDoc.Signature);
                    var remoteSig = $"/tmp/bangka-sig-{Guid.NewGuid():N}.der";
                    session.UploadBytes(derBytes, remoteSig);

                    var (vfyCode, vfyOut, _) = session.Run(
                        $"openssl dgst -sha512 -verify {trustPubPath} " +
                        $"-signature {ShellUtil.Quote(remoteSig)} {ShellUtil.Quote(remoteTemp)}");
                    session.Run($"rm -f {ShellUtil.Quote(remoteSig)}");

                    if (vfyCode != 0 || !vfyOut.Contains("Verified OK"))
                    {
                        session.Run($"rm -f {ShellUtil.Quote(remoteTemp)}");
                        Fail("Server-side signature verification failed.\n" +
                             "The uploaded package does not verify against the server's trusted.pub.");
                        return 1;
                    }
                    Pass(tabCount, "Server-side signature verified with openssl.");
                }
            }

            // ── STEP 6 — Rollback snapshot + service registration ─────────────
            MakeTitle("\nCreating rollback snapshot and service registration");

            var (_, remoteHome, _) = session.Run("echo $HOME");
            remoteHome = remoteHome.Trim();
            if (string.IsNullOrWhiteSpace(remoteHome)) remoteHome = $"/home/{opts.SshUser}";

            var installBase = $"{remoteHome}/bangkasvcs";
            var installPath = $"{installBase}/{meta.Name}";
            var serviceFile = $"/etc/systemd/system/{meta.Name}.service";
            var rollbackDir = $"{installBase}/.rollback/{meta.Name}";
            dataPath = !string.IsNullOrWhiteSpace(meta.DataPath)
                   ? (Path.IsPathRooted(meta.DataPath) ? meta.DataPath : $"{installBase}/{meta.DataPath}")
                   : $"{installBase}/.data/{meta.Name}";

            // Bangka owns the environment file: written from the local .env at deploy
            // time (never bundled in the package), and wired into the unit here.
            var envFilePath = $"{installBase}/.env/{meta.Name}.env";
            svcConfig.EnvironmentFile = envFilePath;

            session.RunPrivileged($"mkdir -p {ShellUtil.Quote(rollbackDir)}");

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
                            WriteWithIcon(tabCount, IconType.Warning, $"[yellow]Same package already deployed:[/] [white]{meta.Name}[/] v[white]{meta.Version}[/] (checksum matches).");
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
                                AnsiConsole.MarkupLine("Deployment cancelled.");
                                return 0;
                            }
                        }
                        else if (sameVersion && !sameChecksum)
                        {
                            WriteWithIcon(tabCount, IconType.Warning, $"Version matches but checksum differs — package was rebuilt.");
                        }
                    }
                    catch { /* ignore parse errors on remote meta */ }
                }
            }

            var (existCode, _, _) = session.Run($"test -d {ShellUtil.Quote(installPath)} && echo 'exists'");
            if (existCode == 0)
            {
                var stamp = DateTime.UtcNow.ToString(Constants.SnapshotStampFormat);
                var snap = $"{rollbackDir}/{stamp}";
                var (snapCode, _, snapErr) = session.RunPrivileged(
                    $"cp -a {ShellUtil.Quote(installPath)} {ShellUtil.Quote(snap)}");
                if (snapCode != 0)
                    WriteWithIcon(tabCount, IconType.Warning, $"Snapshot creation failed: {Markup.Escape(snapErr)}");
                else
                    WriteWithIcon(tabCount, IconType.Info, $"Rollback snapshot created: [bold]{snap}[/]");

                // Enforce retention so snapshots don't grow without bound.
                PruneSnapshots(session, rollbackDir, opts.MaxSnapshots);
            }

            session.RunPrivileged($"mkdir -p {installPath}");

            var (svcExists, _, _) = session.Run($"test -f {serviceFile} && echo 'exists'");
            if (svcExists != 0)
                WriteWithIcon(tabCount, IconType.Info, $"No existing service unit — creating {serviceFile}");
            else
            {
                WriteWithIcon(tabCount, IconType.Info, $"Existing service unit found — will overwrite.");
                session.RunPrivileged($"systemctl stop {meta.Name} 2>/dev/null || true");
            }

            // Resolve dotnet on remote
            var (_, dotnetPath, _) = session.Run("which dotnet");
            var dotnetExe = dotnetPath.Trim();
            if (string.IsNullOrEmpty(dotnetExe)) dotnetExe = "/usr/bin/dotnet";

            svcConfig.WorkingDirectory = installPath;
            svcConfig.ExecStart = $"{dotnetExe} {installPath}/{meta.EntryDll}";
            WriteWithIcon(tabCount, IconType.Info, $"ExecStart: {svcConfig.ExecStart}");

            if (!string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
                WriteWithIcon(tabCount, IconType.Info, $"EnvironmentFile: {svcConfig.EnvironmentFile}");

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
            Pass(tabCount, "Rollback snapshot created and systemd unit registered.");

            // ── STEP 7 — Cloudflare prerequisites ────────────────────────────
            MakeTitle("\nChecking Cloudflare Tunnel prerequisites");
            if (meta.HasCloudflare && cfConfig != null)
            {
                var (cfInstalled, _, _) = session.Run("which cloudflared");
                if (cfInstalled != 0)
                {
                    await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                    Fail("cloudflared is not installed on the remote host.");
                    return 1;
                }
                var (cfRunning, _, _) = session.Run("systemctl is-active cloudflared");
                if (cfRunning != 0)
                {
                    await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                    Fail("cloudflared service is not active on the remote host. Start it first.");
                    return 1;
                }
                Pass(tabCount, "cloudflared is installed and running.");
            }
            else
            {
                WriteWithIcon(tabCount, IconType.Info, "No Cloudflare config in package — skipping...");
                Pass(tabCount, "Cloudflare check skipped (not configured).");
            }

            // ── STEP 8 — Extract + install files ─────────────────────────────
            MakeTitle($"\nInstalling to {installPath}");

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
                await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                Fail("Extraction failed — data/ directory not found in archive.");
                return 1;
            }

            // Atomic-ish install swap: move the current install aside, copy the new
            // files in, and only remove the previous copy on success. If the copy
            // fails, restore the previous install so we never leave a half-written dir.
            var prevPath = $"{installPath}.prev";
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(prevPath)}");
            session.RunPrivileged($"test -d {ShellUtil.Quote(installPath)} && mv {ShellUtil.Quote(installPath)} {ShellUtil.Quote(prevPath)} || true");
            session.RunPrivilegedOrThrow($"mkdir -p {ShellUtil.Quote(installPath)}");
            var (cpCode, _, cpErr) = session.RunPrivileged(
                $"cp -r {ShellUtil.Quote(remoteExtract + "/data/.")} {ShellUtil.Quote(installPath + "/")}");
            if (cpCode != 0)
            {
                // Restore the previous install directory.
                session.RunPrivileged($"rm -rf {ShellUtil.Quote(installPath)}");
                session.RunPrivileged($"test -d {ShellUtil.Quote(prevPath)} && mv {ShellUtil.Quote(prevPath)} {ShellUtil.Quote(installPath)} || true");
                session.Run($"rm -rf {ShellUtil.Quote(remoteExtract)} {ShellUtil.Quote(remoteTemp)}");
                await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                Fail($"Failed to copy files into install directory: {Markup.Escape(cpErr)}");
                return 1;
            }
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(prevPath)}");

            // Verify entry DLL landed
            var (dllCode, dllOut, _) = session.Run(
                $"test -f {installPath}/{meta.EntryDll} && echo 'found' || echo 'MISSING'");
            var dllStatus = dllOut.Trim();
            WriteWithIcon(tabCount, IconType.Info, $"Entry DLL ({Markup.Escape(meta.EntryDll)}): " +
                $"{(dllStatus == "found" ? "[green]found[/]" : "[red]MISSING[/]")}");

            if (dllStatus != "found")
            {
                session.Run($"rm -rf {remoteExtract} {remoteTemp}");
                await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
                Fail($"Entry DLL '{meta.EntryDll}' was not found after extraction. Check your --dll flag.");
                return 1;
            }

            // Write cloudflared config if applicable
            if (meta.HasCloudflare && cfConfig != null)
            {
                session.RunPrivileged("mkdir -p /etc/cloudflared");
                var cfMainConfig = "/etc/cloudflared/config.yml";
                var cfCfgPath = $"/etc/cloudflared/{meta.Name}.yml";

                // Check if a global cloudflared config already exists
                var (mainExists, _, _) = session.RunPrivileged($"test -f {cfMainConfig} && echo yes");
                if (mainExists == 0)
                {
                    // Append-mode: check if this hostname is already registered
                    var (_, cfYamlContent, _) = session.RunPrivileged($"cat {cfMainConfig}");
                    if (cfYamlContent.Contains(cfConfig.Hostname))
                    {
                        AnsiConsole.MarkupLine($"[grey]Cloudflare: hostname {cfConfig.Hostname} already present in {cfMainConfig} — skipping.[/]");
                    }
                    else
                    {
                        // Insert new ingress rule before the catch-all http_status:404 line
                        var ingressRule = EscapeForShell(cfConfig.ToIngressRule());
                        session.Run(
                            $"sed -i '/  - service: http_status:404/i {cfConfig.ToIngressRule().Replace("/", @"\/")}' {cfMainConfig} 2>/dev/null " +
                            $"|| printf '\n{cfConfig.ToIngressRule()}\n' >> {cfMainConfig}");
                        WriteWithIcon(tabCount, IconType.Info, $"Cloudflare: ingress rule for {cfConfig.Hostname} appended to {cfMainConfig}");
                    }
                }
                else
                {
                    // No global config — write a per-service config file
                    var cfYaml = EscapeForShell(cfConfig.ToConfigYaml());
                    session.Run($"printf '%s' {cfYaml} > {cfCfgPath}");
                    WriteWithIcon(tabCount, IconType.Info, $"Cloudflare: new config written to {cfCfgPath}");
                }

                // Reload cloudflared to pick up the change
                session.Run("systemctl reload cloudflared 2>/dev/null || systemctl restart cloudflared 2>/dev/null || true");
                WriteWithIcon(tabCount, IconType.Info, "Cloudflare: cloudflared reloaded.");
            }

            // ── Data directory setup ──────────────────────────────────────────
            session.RunPrivileged($"mkdir -p {ShellUtil.Quote(dataPath)}");
            session.RunPrivileged($"chown -R {ShellUtil.Quote($"{svcConfig.User}:{svcConfig.User}")} {ShellUtil.Quote(dataPath)}");
            session.RunPrivileged($"chmod 750 {ShellUtil.Quote(dataPath)}");
            WriteWithIcon(tabCount, IconType.Info, $"Data directory ready: [bold]{dataPath}[/]");

            // ── Environment file (secrets) ────────────────────────────────────
            // Merge order: profile envVars, then local .env (wins on conflict),
            // then DATA_PATH (always set by Bangka). Written out-of-band to a
            // root:root 0600 managed file — never bundled in the package. systemd
            // (root) reads it and injects the vars into the service process.
            var envMap = new List<KeyValuePair<string, string>>();
            var envIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            void SetEnv(string k, string v)
            {
                if (envIndex.TryGetValue(k, out var idx)) envMap[idx] = new(k, v);
                else { envIndex[k] = envMap.Count; envMap.Add(new(k, v)); }
            }
            if (profile != null)
                foreach (var e in profile.EnvVars) SetEnv(e.Key, e.Value);
            if (localEnvPath != null)
            {
                foreach (var line in File.ReadAllLines(localEnvPath))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#') || !line.Contains('=')) continue;
                    var eq = line.IndexOf('=');
                    var k = line[..eq].Trim();
                    if (k.Length > 0) SetEnv(k, line[(eq + 1)..]);
                }
            }
            SetEnv("DATA_PATH", dataPath);

            if (meta.RequiredEnvKeys.Count > 0)
            {
                var missing = meta.RequiredEnvKeys.Where(k => !envIndex.ContainsKey(k)).ToList();
                if (missing.Count > 0)
                    WriteWithIcon(tabCount, IconType.Warning,
                        $"Keys required by the package but missing from the shipped .env: {Markup.Escape(string.Join(", ", missing))}");
            }

            var envContent = "# Generated by Bangka — managed file, do not edit by hand\n"
                + string.Join("\n", envMap.Select(kv => $"{kv.Key}={kv.Value}")) + "\n";
            var envDir = envFilePath[..envFilePath.LastIndexOf('/')];
            session.RunPrivilegedOrThrow($"mkdir -p {ShellUtil.Quote(envDir)}");
            var tmpEnv = $"/tmp/bangka-env-{Guid.NewGuid():N}.tmp";
            session.UploadText(envContent, tmpEnv);
            session.RunPrivilegedOrThrow($"mv {ShellUtil.Quote(tmpEnv)} {ShellUtil.Quote(envFilePath)}");
            session.RunPrivileged($"chown root:root {ShellUtil.Quote(envFilePath)}");
            session.RunPrivileged($"chmod 600 {ShellUtil.Quote(envFilePath)}");
            WriteWithIcon(tabCount, IconType.Info,
                $"Environment file written: [bold]{envFilePath}[/] ({envMap.Count} var(s), 0600 root:root)");

            session.RunPrivileged("systemctl daemon-reload");
            var localMetaTmp = Path.GetTempFileName();
            try
            {
                // Serialize the full metadata (correct <name> tag, unlike the old
                // hand-built XML that wrote <n>) so the same-version guard can read it back.
                meta.Serialize(localMetaTmp);
                var remoteMetaTmp = $"/tmp/{meta.Name}-meta-{Guid.NewGuid():N}.xml";
                session.Upload(localMetaTmp, remoteMetaTmp);
                session.RunPrivilegedOrThrow($"mv {ShellUtil.Quote(remoteMetaTmp)} {ShellUtil.Quote($"{installPath}/.bangka-meta.xml")}");
            }
            finally
            {
                File.Delete(localMetaTmp);
            }

            session.Run($"rm -rf {remoteExtract} {remoteTemp}");
            Pass(tabCount, $"Files installed to {installPath}.");

            // ── STEP 9 — Start service and health check ───────────────────────
            MakeTitle("\nStarting service and verifying health");

            session.RunPrivileged($"systemctl enable {meta.Name}");
            session.RunPrivileged($"systemctl start {meta.Name}");

            bool healthy = false;
            const int maxAttempts = 8;
            Thread.Sleep(4000); // initial grace period for process to spawn
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var (_, activeOut, _) = session.Run($"systemctl is-active {meta.Name}");
                var status = activeOut.Trim();
                WriteWithIcon(tabCount, attempt > 1 ? IconType.Warning : IconType.Info, $"Attempt {attempt}/{maxAttempts} — status: {status}");
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
                WriteWithIcon(tabCount, IconType.Error, "Service failed to start — gathering diagnostics...");

                // ── systemctl status (human-friendly summary) ─────────────────
                var (_, statusOut, _) = session.Run($"systemctl status {meta.Name} --no-pager -l");
                AnsiConsole.Write(new Rule("[red]systemctl status[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"{Markup.Escape(statusOut)}");

                // ── journal lines (configurable via --err-lines) ──────────────
                var (_, journalOut, _) = session.RunPrivileged(
                    $"journalctl -u {meta.Name} --no-pager -n {opts.ErrLines} --output short-precise");
                AnsiConsole.Write(new Rule($"[red]Journal (last {opts.ErrLines} lines)[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"{Markup.Escape(journalOut)}");

                // ── unit file (so user can see exactly what was written) ───────
                var (_, unitOut, _) = session.Run($"cat {serviceFile}");
                AnsiConsole.Write(new Rule("[red]Unit file[/]").RuleStyle("red"));
                AnsiConsole.MarkupLine($"{Markup.Escape(unitOut)}");

                // ── EnvironmentFile hint if set but missing ───────────────────
                if (!string.IsNullOrWhiteSpace(svcConfig.EnvironmentFile))
                {
                    var (envMissing, _, _) = session.Run(
                        $"test -f {svcConfig.EnvironmentFile} && echo ok || echo missing");
                    if (envMissing != 0)
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
                WriteWithIcon(tabCount, IconType.Warning, "Initiating rollback...");
                await RollbackAsync(session, meta.Name, installPath, rollbackDir, serviceFile);
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

            Pass(tabCount, "Service is active and healthy.");
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
                $"[bold green]✓[/] [bold white]{meta.Name}[/] v[white]{meta.Version}[/] deployed successfully to [white]{opts.Host}[/]\n" +
                $"[grey]Data:[/]  [white]{dataPath}[/]")
            .Header("[bold green] Deployment Complete [/]")
            .BorderColor(Color.Green));

        return 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task RollbackAsync(
        SshSession session, string serviceName,
        string installPath, string rollbackDir, string serviceFile)
    {
        WriteWithIcon(0, IconType.Warning, "Rolling back...");
        session.RunPrivileged($"systemctl stop {ShellUtil.Quote(serviceName)} 2>/dev/null || true");
        session.RunPrivileged($"systemctl disable {ShellUtil.Quote(serviceName)} 2>/dev/null || true");

        // Pick the newest snapshot BY NAME — the yyyyMMdd-HHmmss stamp sorts
        // chronologically. Do NOT use `ls -t`: cp -a preserves source mtimes,
        // so mtime order is meaningless and would restore the wrong snapshot.
        var (_, snapList, _) = session.Run(
            $"ls -1 {ShellUtil.Quote(rollbackDir)} 2>/dev/null | sort -r | head -1");
        var latestSnap = snapList.Trim();
        if (!string.IsNullOrEmpty(latestSnap))
        {
            var snapPath = $"{rollbackDir}/{latestSnap}";
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(installPath)}");
            var (cpCode, _, cpErr) = session.RunPrivileged(
                $"cp -a {ShellUtil.Quote(snapPath)} {ShellUtil.Quote(installPath)}");
            if (cpCode != 0)
                WriteWithIcon(0, IconType.Error, $"Snapshot restore copy failed: {Markup.Escape(cpErr)}");
            session.RunPrivileged($"systemctl start {ShellUtil.Quote(serviceName)} 2>/dev/null || true");
            WriteWithIcon(0, IconType.Info, $"Restored snapshot: {latestSnap}");
        }
        else
        {
            session.RunPrivileged($"rm -rf {ShellUtil.Quote(installPath)}");
            session.RunPrivileged($"rm -f {ShellUtil.Quote(serviceFile)}");
            session.RunPrivileged("systemctl daemon-reload");
            WriteWithIcon(0, IconType.Info, "No prior snapshot — installation fully removed.");
        }
        return Task.CompletedTask;
    }

    /// <summary>Keeps only the newest <paramref name="max"/> snapshots (by name) for a service.</summary>
    private static void PruneSnapshots(SshSession session, string rollbackDir, int max)
    {
        if (max < 1) max = 1;
        // List oldest-first, drop the newest `max`, remove the rest.
        var (_, list, _) = session.Run($"ls -1 {ShellUtil.Quote(rollbackDir)} 2>/dev/null | sort");
        var snaps = list.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
        if (snaps.Count <= max) return;
        foreach (var old in snaps.Take(snaps.Count - max))
            session.RunPrivileged($"rm -rf {ShellUtil.Quote($"{rollbackDir}/{old}")}");
    }

    /// <summary>
    /// Resolves the local .env file to ship out-of-band: an explicit --env-file
    /// (now a LOCAL path) if given, otherwise ./.env by convention. Returns null
    /// when there's no env file to ship.
    /// </summary>
    private static string? ResolveLocalEnvFile(ArgInvoke invoke)
    {
        var explicitVal = (invoke.GetArgStoreValues()
            .SingleOrDefault(a => a.Parameters.Contains("--env-file")) as ArgStore<string>)?.Value;
        if (!string.IsNullOrWhiteSpace(explicitVal))
        {
            if (File.Exists(explicitVal)) return Path.GetFullPath(explicitVal);
            AnsiConsole.MarkupLine($"[yellow]--env-file '{Markup.Escape(explicitVal)}' not found — skipping.[/]");
            return null;
        }
        return File.Exists(".env") ? Path.GetFullPath(".env") : null;
    }

    private static string EscapeForShell(string content)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        return $"\"$(echo {b64} | base64 -d)\"";
    }

    private enum IconType
    {
        Verbose,
        Success,
        Info,
        Warning,
        Error,
    }

    private static void WriteWithIcon(int tabCount, IconType type, string message)
    {
        string icon = type switch
        {
            IconType.Verbose => "[bold grey][[·]][/]",
            IconType.Success => "[bold green][[✓]][/]",
            IconType.Warning => "[bold yellow][[!]][/]",
            IconType.Error => "[bold red][[X]][/]",
            IconType.Info => "[bold blue][[i]][/]",
            _ => "   "
        };
        TabWrite(tabCount, $"{icon} {message}");
    }

    private static void TabWrite(int tabCount, string value)
    {
        var tabs = new string(' ', tabCount * 4);
        AnsiConsole.MarkupLine($"{tabs}{value}");
    }
    private static void MakeTitle(string description) =>
        AnsiConsole.MarkupLine($"[bold white]{description}[/]");

    private static void Pass(int tabCount, string msg) =>
        WriteWithIcon(tabCount, IconType.Success, msg);

    private static void Fail(string msg)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel($"{Markup.Escape(msg)}")
            .BorderColor(Color.Red)
            .Header("[bold red][[X]] ERROR [/]")
        );
    }

    private static string ConvertBytesToHumanReadable(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
