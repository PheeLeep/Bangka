using System;

namespace bangka_lib;

public sealed class Constants
{
    public const string BangkaDeployUserName = "bangka-deploy";

    /// <summary>
    /// Timestamp format used to name rollback snapshot directories.
    /// Chosen so lexicographic ordering == chronological ordering
    /// (do NOT sort snapshots by mtime — cp -a preserves source mtimes).
    /// </summary>
    public const string SnapshotStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Default number of rollback snapshots retained per service.</summary>
    public const int DefaultMaxSnapshots = 5;

    public static string SSHStorage { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
}
