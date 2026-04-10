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
        return RunAsync(invoke).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(ArgInvoke invoke)
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
                AnsiConsole.MarkupLine($"Profile loaded: [bold]{opts.Profile}[/]");
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
                AnsiConsole.MarkupLine($"[red]•[/] {e}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Building package[/] [white]{opts.Name}[/] v[white]{opts.Version}[/]\n");

        // ── Resolve entry DLL before entering Progress (Prompt can't run inside Progress) ──
        string entryDll;
        if (!string.IsNullOrWhiteSpace(opts.EntryDll))
        {
            var explicitPath = Path.Combine(opts.PublishDir, opts.EntryDll);
            if (!File.Exists(explicitPath))
            {
                AnsiConsole.MarkupLine($"[red]--dll '{opts.EntryDll}' not found in publish directory: {opts.PublishDir}[/]");
                return 1;
            }
            entryDll = opts.EntryDll;
            AnsiConsole.MarkupLine($"Entry DLL (explicit): [bold]{entryDll}[/]");
        }
        else
        {
            var runtimeConfigs = Directory.GetFiles(opts.PublishDir, "*.runtimeconfig.json",
                SearchOption.TopDirectoryOnly);
            if (runtimeConfigs.Length == 1)
            {
                entryDll = Path.GetFileName(runtimeConfigs[0]).Replace(".runtimeconfig.json", ".dll");
                AnsiConsole.MarkupLine($"Entry DLL (auto-detected): [bold]{entryDll}[/]");
            }
            else if (runtimeConfigs.Length > 1)
            {
                var nameMatch = runtimeConfigs.FirstOrDefault(r =>
                    Path.GetFileName(r).StartsWith(opts.Name, StringComparison.OrdinalIgnoreCase));
                if (nameMatch != null)
                {
                    entryDll = Path.GetFileName(nameMatch).Replace(".runtimeconfig.json", ".dll");
                    AnsiConsole.MarkupLine($"Entry DLL (matched by name): [bold]{entryDll}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Multiple entry points found. Pick the correct DLL:[/]");
                    var choices = runtimeConfigs
                        .Select(r => Path.GetFileName(r).Replace(".runtimeconfig.json", ".dll"))
                        .ToList();
                    entryDll = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[cyan]Which is the entry DLL?[/]")
                            .AddChoices(choices));
                    AnsiConsole.MarkupLine($"Entry DLL (selected): [bold]{entryDll}[/]");
                }
            }
            else
            {
                entryDll = $"{opts.Name}.dll";
                AnsiConsole.MarkupLine($"[yellow]No .runtimeconfig.json found — defaulting to {entryDll}[/]");
            }
        }

        var stagingDir = Path.Combine(Path.GetTempPath(), $"bangka-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            await AnsiConsole.Progress()
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
                   copyTask.StopTask();

                   // ── Step 2: Write metadata.xml ───────────────────────────────────────
                   var metaTask = ctx.AddTask("[cyan]Writing metadata.xml[/]");
                   var installPath = $"/home/{opts.User}/bangkasvcs/{opts.Name}";

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
                           AnsiConsole.MarkupLine($"Embedded {requiredEnvKeys.Count} env key(s) from local env file.[/]");
                       }
                       else
                       {
                           AnsiConsole.MarkupLine($"[yellow]No local env file found at '{localCopy}' — env key verification will be skipped at deploy time.[/]");
                           AnsiConsole.MarkupLine($"[yellow]If this is your intention. Make sure that the specific env file exists in the server during deployment.[/]");
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
                       RequiredEnvKeys = requiredEnvKeys,
                       DataPath = opts.DataPath ?? string.Empty
                   };
                   meta.Serialize(Path.Combine(stagingDir, "metadata.xml"));
                   metaTask.Value = 100;
                   metaTask.StopTask();

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
                   svcTask.StopTask();

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
                       cfTask.StopTask();
                   }

                   // ── Step 5: Compute checksum of data dir ──────────────────
                   var cksumTask = ctx.AddTask("[cyan]Computing SHA-512 checksum[/]");
                   var dataZipTemp = Path.Combine(Path.GetTempPath(), $"data-{Guid.NewGuid():N}.zip");
                   ZipFile.CreateFromDirectory(dataDir, dataZipTemp);
                   var checksum = await ChecksumHelper.ComputeSha512Async(dataZipTemp);
                   File.Delete(dataZipTemp);

                   // Embed checksum back into metadata
                   meta.Checksum = checksum;
                   meta.Serialize(Path.Combine(stagingDir, "metadata.xml"));
                   cksumTask.Value = 100;
                   cksumTask.StopTask();

                   // ── Step 6: Zip everything into .bangka ────────────────────
                   var packTask = ctx.AddTask("[cyan]Creating .bangka archive[/]");
                   Directory.CreateDirectory(opts.OutDir);
                   var outFile = Path.Combine(opts.OutDir, $"{opts.Name}-{opts.Version}.bangka");
                   if (File.Exists(outFile)) File.Delete(outFile);
                   ZipFile.CreateFromDirectory(stagingDir, outFile);
                   packTask.Value = 100;
                   packTask.StopTask();
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
                    AnsiConsole.MarkupLine($"[bold green]✓[/] Package signed: {sigPath}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Signing failed:[/] {Markup.Escape(ex.Message)}");
                    AnsiConsole.MarkupLine("  [grey]Package was built but not signed. Run keygen first.[/]");
                }
            }

            AnsiConsole.WriteLine();
            var dataPathNote = string.IsNullOrWhiteSpace(opts.DataPath)
                ? "[grey]none[/]"
                : $"[white]{opts.DataPath}[/]";
            var panel = new Panel(
                    $"[bold white]{opts.Name}[/] v[white]{opts.Version}[/]\n" +
                    $"[grey]Path:[/]  [white]{Path.GetFullPath(finalPath)}[/]\n" +
                    $"[grey]Size:[/]  [white]{size / 1024.0:F1} KB[/]\n" +
                    $"[grey]CF  :[/]  [white]{(opts.HasCloudflare ? "yes" : "no")}[/]\n" +
                    $"[grey]Data:[/]  {dataPathNote}\n" +
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

    private static async Task CopyDirectoryAsync(string src, string dst, ProgressTask task)
    {
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
        task.MaxValue = files.Length;

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);
            await using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var dstStream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await srcStream.CopyToAsync(dstStream);
            task.Increment(1);
        }
    }
}
