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

        var trust = ArgSharpClass.AddArgumentAction(["trust"], null, "Manage trust signing enforcement.");
    
        TrustCommand.Load(trust);    
        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }
}