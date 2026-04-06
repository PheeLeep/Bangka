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

public static class BuildCommand
{

    public static void Load(ArgInvoke invoke)
    {
        LoadBuildArgs(invoke);
    }


    public static int Run(ArgInvoke invoke)
    {
        BuildArgs opts;
        try
        {
            opts = BuildArgs.Parse(invoke);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Argument error:[/] {ex.Message}");
            return 1;
        }

        // ── Load profile if supplied, CLI flags override profile values ─────
        if (!string.IsNullOrWhiteSpace(opts.Profile))
        {
            try
            {
                var profile = DeploymentProfile.Load(opts.Profile);
                opts = profile.ApplyToBuildArgs(opts);
                AnsiConsole.MarkupLine($"[grey]Profile loaded: {opts.Profile}[/]");
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
            PrintUsage();
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Building package[/] [white]{opts.Name}[/] v[white]{opts.Version}[/]\n");

        var stagingDir = Path.Combine(Path.GetTempPath(), $"aspimport-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            AnsiConsole.Progress()
               .AutoRefresh(true)
               .Columns(
                   new TaskDescriptionColumn(),
                   new ProgressBarColumn(),
                   new SpinnerColumn())
               .StartAsync(async ctx =>
               {
                   // ── Step 1: Copy publish output ──────────────────────────
                   var copyTask = ctx.AddTask("[cyan]Copying publish output[/]");
                   var dataDir = Path.Combine(stagingDir, "data");
                   Directory.CreateDirectory(dataDir);
                   await CopyDirectoryAsync(opts.PublishDir, dataDir, copyTask);
                   copyTask.Value = 100;

                   // ── Step 2: Write metadata.xml ───────────────────────────────────────
                   var metaTask = ctx.AddTask("[cyan]Writing metadata.xml[/]");
                   var installPath = $"/home/{opts.User}/bangkasvcs/{opts.Name}";

                   // Resolve entry DLL — explicit flag wins, otherwise find via .runtimeconfig.json
                   string entryDll;
                   if (!string.IsNullOrWhiteSpace(opts.EntryDll))
                   {
                       // User provided it explicitly — verify it exists in the publish dir
                       var explicitPath = Path.Combine(opts.PublishDir, opts.EntryDll);
                       if (!File.Exists(explicitPath))
                           throw new FileNotFoundException(
                               $"--dll '{opts.EntryDll}' not found in publish directory: {opts.PublishDir}");
                       entryDll = opts.EntryDll;
                       AnsiConsole.MarkupLine($"  [grey]Entry DLL (explicit): {entryDll}[/]");
                   }
                   else
                   {
                       // Auto-detect via .runtimeconfig.json — most reliable indicator of entry assembly
                       var runtimeConfigs = Directory.GetFiles(opts.PublishDir, "*.runtimeconfig.json",
                           SearchOption.TopDirectoryOnly);
                       if (runtimeConfigs.Length == 1)
                       {
                           entryDll = Path.GetFileName(runtimeConfigs[0]).Replace(".runtimeconfig.json", ".dll");
                           AnsiConsole.MarkupLine($"  [grey]Entry DLL (auto-detected): {entryDll}[/]");
                       }
                       else if (runtimeConfigs.Length > 1)
                       {
                           // Multiple runtimeconfigs — pick the one matching meta.Name, else ask user
                           var nameMatch = runtimeConfigs.FirstOrDefault(r =>
                               Path.GetFileName(r).StartsWith(opts.Name, StringComparison.OrdinalIgnoreCase));
                           if (nameMatch != null)
                           {
                               entryDll = Path.GetFileName(nameMatch).Replace(".runtimeconfig.json", ".dll");
                               AnsiConsole.MarkupLine($"  [grey]Entry DLL (matched by name): {entryDll}[/]");
                           }
                           else
                           {
                               // Ambiguous — list them and prompt
                               AnsiConsole.MarkupLine("[yellow]Multiple entry points found. Pick the correct DLL:[/]");
                               var choices = runtimeConfigs
                                   .Select(r => Path.GetFileName(r).Replace(".runtimeconfig.json", ".dll"))
                                   .ToList();
                               entryDll = AnsiConsole.Prompt(
                                   new SelectionPrompt<string>()
                                       .Title("[cyan]Which is the entry DLL?[/]")
                                       .AddChoices(choices));
                               AnsiConsole.MarkupLine($"  [grey]Entry DLL (selected): {entryDll}[/]");
                           }
                       }
                       else
                       {
                           // No runtimeconfig found — fall back to meta.Name.dll with a warning
                           entryDll = $"{opts.Name}.dll";
                           AnsiConsole.MarkupLine($"  [yellow]No .runtimeconfig.json found — defaulting to {entryDll}[/]");
                       }
                   }

                   // Extract env keys from local env file if provided
                   var requiredEnvKeys = new List<string>();
                   if (!string.IsNullOrWhiteSpace(opts.EnvFile))
                   {
                       var localEnvPath = opts.EnvFile;
                       // The EnvFile is a remote path — also check if a local copy exists
                       // by looking for a file with the same name in the publish dir or cwd
                       var localCopy = Path.Combine(opts.PublishDir, Path.GetFileName(localEnvPath));
                       if (!File.Exists(localCopy)) localCopy = Path.GetFileName(localEnvPath);
                       if (File.Exists(localCopy))
                       {
                           requiredEnvKeys = File.ReadAllLines(localCopy)
                               .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && l.Contains('='))
                               .Select(l => l.Split('=')[0].Trim())
                               .Where(k => !string.IsNullOrWhiteSpace(k))
                               .ToList();
                           AnsiConsole.MarkupLine($"  [grey]Embedded {requiredEnvKeys.Count} env key(s) from local env file.[/]");
                       }
                       else
                       {
                           AnsiConsole.MarkupLine($"  [yellow]No local env file found at '{localCopy}' — env key verification will be skipped at deploy time.[/]");
                       }
                   }

                   var meta = new PackageMetadata
                   {
                       Name = opts.Name,
                       Version = opts.Version,
                       Description = opts.Description,
                       Author = opts.Author,
                       AspPort = opts.Port,
                       AspEnvironment = opts.Environment,
                       HasCloudflare = opts.HasCloudflare,
                       EntryDll = entryDll,
                       BuiltAt = DateTime.UtcNow.ToString("O"),
                       RequiredEnvKeys = requiredEnvKeys
                   };
                   meta.Serialize(Path.Combine(stagingDir, "metadata.xml"));
                   metaTask.Value = 100;

                   // ── Step 3: Write systemdservice.xml ─────────────────────
                   var svcTask = ctx.AddTask("[cyan]Writing systemdservice.xml[/]");
                   var svc = new SystemdServiceConfig
                   {
                       ServiceName = opts.Name,
                       Description = opts.Description.Length > 0 ? opts.Description : $"{opts.Name} ASP.NET service",
                       User = opts.User,
                       WorkingDirectory = string.Empty,   // filled in at deploy time
                       ExecStart = string.Empty,   // filled in at deploy time
                       Environment = opts.Environment,
                       AspNetPort = opts.Port,
                       EnvironmentFile = opts.EnvFile ?? string.Empty
                   };

                   svc.Serialize(Path.Combine(stagingDir, "systemdservice.xml"));
                   svcTask.Value = 100;

                   // ── Step 4: Write cloudflared.xml (optional) ─────────────
                   if (opts.HasCloudflare)
                   {
                       var cfTask = ctx.AddTask("[cyan]Writing cloudflared.xml[/]");
                       var cf = new CloudflaredConfig
                       {
                           TunnelId = opts.CfTunnelId!,
                           TunnelName = opts.CfTunnelName!,
                           Hostname = opts.CfHostname!,
                           ServiceUrl = $"http://localhost:{opts.Port}",
                           CredentialsFile = opts.CfCredentials!
                       };
                       cf.Serialize(Path.Combine(stagingDir, "cloudflared.xml"));
                       cfTask.Value = 100;
                   }

                   // ── Step 5: Compute checksum of data dir ──────────────────
                   var cksumTask = ctx.AddTask("[cyan]Computing SHA-512 checksum[/]");
                   var dataZipTemp = Path.Combine(Path.GetTempPath(), $"data-{Guid.NewGuid():N}.zip");
                   ZipFile.CreateFromDirectory(dataDir, dataZipTemp);
                   var checksum = ChecksumHelper.ComputeSha512(dataZipTemp);
                   File.Delete(dataZipTemp);

                   // Embed checksum back into metadata
                   meta.Checksum = checksum;
                   meta.Serialize(Path.Combine(stagingDir, "metadata.xml"));
                   cksumTask.Value = 100;

                   // ── Step 6: Zip everything into .bangka ────────────────────
                   var packTask = ctx.AddTask("[cyan]Creating .bangka archive[/]");
                   Directory.CreateDirectory(opts.OutDir);
                   var outFile = Path.Combine(opts.OutDir, $"{opts.Name}-{opts.Version}.bangka");
                   if (File.Exists(outFile)) File.Delete(outFile);
                   ZipFile.CreateFromDirectory(stagingDir, outFile);
                   packTask.Value = 100;

                   await Task.CompletedTask;
               });

            var finalPath = Path.Combine(opts.OutDir, $"{opts.Name}-{opts.Version}.bangka");
            var size = new FileInfo(finalPath).Length;

            // ── Sign package if requested ─────────────────────────────────────
            string signedNote = "[grey]unsigned[/]";
            if (opts.Sign)
            {
                try
                {
                    PackageSigner.SignPackage(finalPath, opts.SigningKeyPath);
                    var sigPath = finalPath + ".sig";
                    signedNote = $"[green]signed[/] → {Path.GetFileName(sigPath)}";
                    AnsiConsole.MarkupLine($"  [bold green]✓[/] Package signed: {sigPath}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]Signing failed:[/] {Markup.Escape(ex.Message)}");
                    AnsiConsole.MarkupLine("  [grey]Package was built but not signed. Run keygen first.[/]");
                }
            }

            AnsiConsole.WriteLine();
            var panel = new Panel(
                    $"[bold white]{opts.Name}[/] v[white]{opts.Version}[/]\n" +
                    $"[grey]Path:[/]  [white]{Path.GetFullPath(finalPath)}[/]\n" +
                    $"[grey]Size:[/]  [white]{size / 1024.0:F1} KB[/]\n" +
                    $"[grey]CF  :[/]  [white]{(opts.HasCloudflare ? "yes" : "no")}[/]\n" +
                    $"[grey]Sign:[/]  {signedNote}")
                .Header("[bold green] Package Ready [/]")
                .BorderColor(Color.Green);

            AnsiConsole.Write(panel);
            return 0;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
    }

    internal static void RunAsync(ArgInvoke argInvoke)
    {
        throw new NotImplementedException();
    }

    private static Task CopyDirectoryAsync(string src, string dst, ProgressTask task)
    {
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
        task.MaxValue = files.Length;

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            task.Increment(1);
        }

        return Task.CompletedTask;
    }

    static void PrintUsage()
    {
        AnsiConsole.MarkupLine("\n[bold]Usage:[/] aspimport-master build [[options]]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[cyan]Flag[/]")
            .AddColumn("[grey]Required[/]")
            .AddColumn("[grey]Description[/]");

        table.AddRow("--name", "[red]yes[/]", "Application / service name");
        table.AddRow("--version", "[red]yes[/]", "Semantic version (e.g. 1.0.0)");
        table.AddRow("--publish", "[red]yes[/]", "Path to dotnet publish output directory");
        table.AddRow("--port", "no", "ASP.NET port (default: 5000)");
        table.AddRow("--user", "no", "Linux service user (default: www-data)");
        table.AddRow("--env", "no", "ASPNETCORE_ENVIRONMENT (default: Production)");
        table.AddRow("--author", "no", "Package author");
        table.AddRow("--description", "no", "Human-readable description");
        table.AddRow("--out", "no", "Output directory (default: .)");
        table.AddRow("--cf-tunnel-id", "no*", "Cloudflare tunnel UUID");
        table.AddRow("--cf-tunnel-name", "no*", "Cloudflare tunnel name");
        table.AddRow("--cf-hostname", "no*", "Public hostname (e.g. api.example.com)");
        table.AddRow("--dll", "no", "Entry DLL name (e.g. MyApp.Server.dll) — auto-detected if omitted");
        table.AddRow("--env-file", "no", "Path to EnvironmentFile on the remote (e.g. /etc/myapp.env)");
        table.AddRow("--sign", "no", "Sign the package with ~/.aspimport/signing.key");
        table.AddRow("--signing-key", "no", "Path to signing private key (overrides default)");
        table.AddRow("--cf-tunnel-id", "no*", "Cloudflare tunnel UUID");
        table.AddRow("--cf-tunnel-name", "no*", "Cloudflare tunnel name");
        table.AddRow("--cf-hostname", "no*", "Public hostname (e.g. api.example.com)");
        table.AddRow("--cf-credentials", "no*", "Path to tunnel credentials JSON");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]* All --cf-* flags must be provided together or not at all.[/]");
    }
}
