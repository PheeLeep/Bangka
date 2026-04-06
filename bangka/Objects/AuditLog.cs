using System;
using System.Text.Json;

namespace bangka.Objects;

public static class AuditLog
{
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bangka", "deploy.log");

    public static void Record(AuditRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var line = JsonSerializer.Serialize(record);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Audit log failure must never crash the deploy
        }
    }
}

public class AuditRecord
{
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public string Action { get; set; } = string.Empty;  // "deploy", "rollback"
    public string PackageName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string SshUser { get; set; } = string.Empty;
    public string KeyPath { get; set; } = string.Empty;
    public bool Signed { get; set; } = false;
    public string Result { get; set; } = string.Empty;  // "success", "failed", "cancelled"
    public string? Note { get; set; }
}
