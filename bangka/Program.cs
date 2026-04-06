using ArgSharp;
using ArgSharp.Args;
using bangka.Commands;

namespace bangka;

public class Program
{
    public static void Main(string[] args)
    {
        ArgSharpClass.Init("bangka",
                            "Bangka Web Deployment",
                            "A web service deployment orchestrator for ASP.NET");
        ArgSharpClass.IgnoreConflictArgument = true;

        ArgInvoke? profileInvoke = null;

        profileInvoke = ArgSharpClass.AddArgumentAction(["profile"],
                                                         null,
                                                         "Manage deployment profiles (create, list, show, delete)");
        profileInvoke.ArgumentZeroAction = ArgSharpClass.ArgZeroAction.TreatAsSuccess;
        ProfileCommand.Load(profileInvoke!);


        if (!ArgSharpClass.Parse(args))
        {
            return;
        }
    }
}