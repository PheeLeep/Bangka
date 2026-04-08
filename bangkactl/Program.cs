using ArgSharp;
using ArgSharp.Args;
using bangka_lib;
using bangkactl.Commands;
namespace bangkactl;

public class Program
{
    public static void Main(string[] args)
    {
        ArgSharpClass.Init("bangkactl",
                           "Bangka Control Panel",
                           "A control panel for programs uploaded by Bangka, trust management, and snapshot.");

        ArgInvoke? logInvoke = null;
        ArgInvoke? statusInvoke = null;
        ArgInvoke? rollbackInvoke = null;
        ArgInvoke? uninstallInvoke = null;

        logInvoke = ArgSharpClass.AddArgumentAction(["logs"], () => { LogsCommand.Run(logInvoke!); }, "Tail the systemd journal for a service");
        statusInvoke = ArgSharpClass.AddArgumentAction(["status"], () => { Environment.Exit(StatusCommand.Run(statusInvoke!)); }, "Show health status of a deployed service");
        rollbackInvoke = ArgSharpClass.AddArgumentAction(["rollback"], () => { Environment.Exit(RollbackCommand.Run(rollbackInvoke!)); }, "Manually trigger rollback to a previous snapshot");

        StatusCommand.Load(statusInvoke);
        LogsCommand.Load(logInvoke);
        RollbackCommand.Load(rollbackInvoke);
        TrustCommand.Load(ArgSharpClass.AddArgumentAction(["trust"], null, "Manage trust signing enforcement."));
        ArgSharpClass.AddArgumentAction(["list"], () => { Environment.Exit(ListCommand.Load()); }, "List all services managed by Bangka").ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;

        uninstallInvoke = ArgSharpClass.AddArgumentAction(["uninstall"], () => { Environment.Exit(UninstallCommand.Run(uninstallInvoke!)); }, "Uninstall a deployed service");
        UninstallCommand.Load(uninstallInvoke!);

        DeployUserCommand.Load(ArgSharpClass.AddArgumentAction(["deploy-user"],
                                                                null,
                                                                $"Create and manage the {Constants.BangkaDeployUserName} user",
                                                                epilog: "These commands require root. Run as root or with sudo.\n\nThe generated private key is printed once and never stored on this server."));
        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }
}