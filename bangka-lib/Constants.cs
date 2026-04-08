using System;

namespace bangka_lib;

public sealed class Constants
{
    public const string BangkaDeployUserName = "bangka-deploy";

    public static string SSHStorage { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
}
