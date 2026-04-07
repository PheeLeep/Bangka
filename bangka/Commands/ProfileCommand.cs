using System;
using ArgSharp.Args;
using bangka.Properties;
using Spectre.Console;

namespace bangka.Commands;

public static class ProfileCommand
{
    public static void Load(ArgInvoke argInvoke)
    {
        ArgInvoke? createInvoke = null;

        createInvoke = argInvoke.AddArgumentAction(["create"],
        () =>
        {
            Environment.Exit(CreateProfile(createInvoke!));
        }, "Create a new profile from build + deploy flags");
        createInvoke.ArgumentZeroAction = ArgSharp.ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        argInvoke.AddArgumentAction(["list"],
        () =>
        {
            ListProfiles();
        }, "List all available profiles");
        Arguments.LoadBuildArgs(createInvoke);
        Arguments.LoadDeployArgs(createInvoke);

        ArgInvoke? showInvoke = null;
        showInvoke = argInvoke.AddArgumentAction(["show"],
        () =>
        {
            Environment.Exit(ShowProfile(showInvoke!));
        }, "Show details of a profile by name");
        showInvoke.AddArgument<string>(["--name"], helpMsg: "Profile name to show", isRequired: true);

        ArgInvoke? updateInvoke = null;
        updateInvoke = argInvoke.AddArgumentAction(["update"],
        () =>
        {
            Environment.Exit(CreateProfile(updateInvoke!, true));
        }, "Update an existing profile (same flags as create, but requires --name)");
        updateInvoke.ArgumentZeroAction = ArgSharp.ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        Arguments.LoadBuildArgs(updateInvoke);
        Arguments.LoadDeployArgs(updateInvoke);
        updateInvoke.AddArgument<string>(["--name"], helpMsg: "Profile name to update (must already exist)", isRequired: true);

        ArgInvoke? deleteInvoke = null;
        deleteInvoke = argInvoke.AddArgumentAction(["delete"],
        () =>
        {
            Environment.Exit(DeleteProfile(deleteInvoke!));
        }, "Delete a profile by name");
        deleteInvoke.AddArgument<string>(["--name"], helpMsg: "Profile name to delete", isRequired: true);
    }

    private static int CreateProfile(ArgInvoke argInvoke, bool isUpdate = false)
    {
        var build = Arguments.BuildArgs.Parse(argInvoke);
        var deploy = Arguments.DeployArgs.Parse(argInvoke);

        string? profileName = null;
        if (argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--profile")) is ArgStore<string> namePf)
            profileName = namePf.Value;

        if (string.IsNullOrWhiteSpace(profileName))
        {
            if (isUpdate)
            {
                AnsiConsole.MarkupLine("[red]Profile name is required for update.[/]");
                return 1;
            }
            // Fall back to --name if provided
            profileName = string.IsNullOrWhiteSpace(build.Name)
                ? AnsiConsole.Ask<string>("[cyan]Profile name:[/]")
                : build.Name;
        }

        var existingPath = DeploymentProfile.ProfilePath(profileName);
        if (File.Exists(existingPath) && !isUpdate)
        {
            AnsiConsole.MarkupLine($"[yellow]Profile '{profileName}' already exists.[/]");
            if (!AnsiConsole.Confirm("Overwrite?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }
        }

        DeploymentProfile profile;

        if (isUpdate)
        {
            profile = DeploymentProfile.Load(profileName);
            // Update fields if they were provided in the arguments
            profile.Name = string.IsNullOrWhiteSpace(build.Name) ? profile.Name : build.Name;
            profile.Version = string.IsNullOrWhiteSpace(build.Version) ? profile.Version : build.Version;
            profile.PublishDir = string.IsNullOrWhiteSpace(build.PublishDir) ? profile.PublishDir : build.PublishDir;
            profile.EntryDll = string.IsNullOrWhiteSpace(build.EntryDll) ? profile.EntryDll : build.EntryDll;
            profile.Port = build.Port != 0 ? build.Port : profile.Port;
            profile.User = string.IsNullOrWhiteSpace(build.User) ? profile.User : build.User;
            profile.Environment = string.IsNullOrWhiteSpace(build.Environment) ? profile.Environment : build.Environment;
            profile.Description = string.IsNullOrWhiteSpace(build.Description) ? profile.Description : build.Description;
            profile.EnvFile = string.IsNullOrWhiteSpace(build.EnvFile) ? profile.EnvFile : build.EnvFile;
            profile.OutDir = string.IsNullOrWhiteSpace(build.OutDir) ? profile.OutDir : build.OutDir;
            profile.Host = string.IsNullOrWhiteSpace(deploy.Host) ? profile.Host : deploy.Host;
            profile.SshUser = string.IsNullOrWhiteSpace(deploy.SshUser) ? profile.SshUser : deploy.SshUser;
            profile.KeyPath = string.IsNullOrWhiteSpace(deploy.KeyPath) ? profile.KeyPath : deploy.KeyPath;
            profile.SshPort = deploy.SshPort != 0 ? deploy.SshPort : profile.SshPort;
            profile.CfTunnelId = string.IsNullOrWhiteSpace(build.CfTunnelId) ? profile.CfTunnelId : build.CfTunnelId;
            profile.CfTunnelName = string.IsNullOrWhiteSpace(build.CfTunnelName) ? profile.CfTunnelName : build.CfTunnelName;
            profile.CfHostname = string.IsNullOrWhiteSpace(build.CfHostname) ? profile.CfHostname : build.CfHostname;
            profile.CfCredentials = string.IsNullOrWhiteSpace(build.CfCredentials) ? profile.CfCredentials : build.CfCredentials;
        }
        else
        {
            profile = new DeploymentProfile
            {
                Name = build.Name,
                Version = build.Version,
                PublishDir = build.PublishDir,
                EntryDll = build.EntryDll ?? string.Empty,
                Port = build.Port,
                User = build.User,
                Environment = build.Environment,
                Author = build.Author,
                Description = build.Description,
                EnvFile = build.EnvFile ?? string.Empty,
                OutDir = build.OutDir,
                Host = deploy.Host,
                SshUser = deploy.SshUser,
                KeyPath = deploy.KeyPath,
                SshPort = deploy.SshPort,
                CfTunnelId = build.CfTunnelId ?? string.Empty,
                CfTunnelName = build.CfTunnelName ?? string.Empty,
                CfHostname = build.CfHostname ?? string.Empty,
                CfCredentials = build.CfCredentials ?? string.Empty,
            };
        }
        // Check if profile already exists


        profile.Save(profileName);

        if(isUpdate)
        {
            AnsiConsole.MarkupLine($"[green]Profile '{profileName}' updated successfully.[/]");
        }
        AnsiConsole.Write(new Panel(
                $"[bold white]{profileName}[/]\n" +
                $"[grey]Path:[/] [white]{DeploymentProfile.ProfilePath(profileName)}[/]")
            .Header("[bold green] Profile Saved [/]")
            .BorderColor(Color.Green));

        return 0;
    }
    private static int ListProfiles()
    {
        var dir = DeploymentProfile.ProfilesDir;
        if (!Directory.Exists(dir) || !Directory.GetFiles(dir, "*.xml").Any())
        {
            AnsiConsole.MarkupLine("[yellow]No profiles found.[/]");
            AnsiConsole.MarkupLine($"[grey]Create one with:[/] bangka profile create --name myapp ...");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[cyan]Profile[/]")
            .AddColumn("[grey]App[/]")
            .AddColumn("[grey]Version[/]")
            .AddColumn("[grey]Host[/]")
            .AddColumn("[grey]Port[/]")
            .AddColumn("[grey]Env File[/]");

        foreach (var file in Directory.GetFiles(dir, "*.xml").OrderBy(f => f))
        {
            try
            {
                var p = DeploymentProfile.Load(file);
                var name = Path.GetFileNameWithoutExtension(file);
                table.AddRow(
                    $"[white]{name}[/]",
                    string.IsNullOrWhiteSpace(p.Name) ? "[grey]—[/]" : p.Name,
                    string.IsNullOrWhiteSpace(p.Version) ? "[grey]—[/]" : p.Version,
                    string.IsNullOrWhiteSpace(p.Host) ? "[grey]—[/]" : p.Host,
                    p.Port.ToString(),
                    string.IsNullOrWhiteSpace(p.EnvFile) ? "[grey]none[/]" : $"[grey]{p.EnvFile}[/]"
                );
            }
            catch
            {
                table.AddRow($"[red]{Path.GetFileNameWithoutExtension(file)}[/]", "[red]parse error[/]", "", "", "", "");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Profiles directory: {dir}[/]");
        return 0;
    }

    private static int ShowProfile(ArgInvoke argInvoke)
    {
        var profileName = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--name")) is ArgStore<string> namePf
            ? namePf.Value
            : throw new ArgumentException("Profile name is required (--name)");
        if (string.IsNullOrWhiteSpace(profileName))
        {
            AnsiConsole.MarkupLine("[red]Profile name is required.[/]");
            return 1;
        }
        try
        {
            var p = DeploymentProfile.Load(profileName);
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn("[grey]Setting[/]")
                .AddColumn("[grey]Value[/]");

            void Row(string k, string v) =>
                table.AddRow(k, string.IsNullOrWhiteSpace(v) ? "[grey]—[/]" : v);

            Row("name", p.Name);
            Row("version", p.Version);
            Row("publishDir", p.PublishDir);
            Row("entryDll", p.EntryDll);
            Row("port", p.Port.ToString());
            Row("user", p.User);
            Row("environment", p.Environment);
            Row("description", p.Description);
            Row("envFile", p.EnvFile);
            Row("outDir", p.OutDir);
            table.AddRow("[grey]─── deploy ───[/]", "");
            Row("host", p.Host);
            Row("sshUser", p.SshUser);
            Row("keyPath", p.KeyPath);
            Row("sshPort", p.SshPort.ToString());
            if (!string.IsNullOrWhiteSpace(p.CfHostname))
            {
                table.AddRow("[grey]─── cloudflare ───[/]", "");
                Row("cfTunnelId", p.CfTunnelId);
                Row("cfTunnelName", p.CfTunnelName);
                Row("cfHostname", p.CfHostname);
                Row("cfCredentials", p.CfCredentials);
            }

            AnsiConsole.Write(table);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
            return 1;
        }
        return 0;
    }

    private static int DeleteProfile(ArgInvoke argInvoke)
    {
        var profileName = argInvoke.GetArgStoreValues().SingleOrDefault(a => a.Parameters.Contains("--name")) is ArgStore<string> namePf
            ? namePf.Value
            : throw new ArgumentException("Profile name is required (--name)");
        if (string.IsNullOrWhiteSpace(profileName))
        {
            AnsiConsole.MarkupLine("[red]Profile name is required.[/]");
            return 1;
        }

        var path = DeploymentProfile.ProfilePath(profileName);
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Profile not found:[/] {profileName}");
            return 1;
        }

        if (!AnsiConsole.Confirm($"[yellow]Delete profile '{profileName}'?[/]", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }
        File.Delete(path);
        AnsiConsole.MarkupLine($"[green]Profile '{profileName}' deleted.[/]");
        return 0;
    }

}
