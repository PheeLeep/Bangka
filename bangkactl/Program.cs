using ArgSharp;
using bangkactl.Commands;
namespace bangkactl;

public class Program
{
    public static void Main(string[] args)
    {
        ArgSharpClass.Init("bangkactl",
                           "Bangka Control Panel",
                           "A control panel for programs uploaded by Bangka, trust management, and snapshot.");

    
        TrustCommand.Load(ArgSharpClass.AddArgumentAction(["trust"], null, "Manage trust signing enforcement.")); 
        DeployUserCommand.Load(ArgSharpClass.AddArgumentAction(["deploy-user"], 
                                                                null, 
                                                                "Create and manage the bangka-deploy SSH user",
                                                                epilog: "These commands require root. Run as root or with sudo.\n\nThe generated private key is printed once and never stored on this server."));   
        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }
}