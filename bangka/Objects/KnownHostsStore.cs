using System;
using System.Text.Json;

namespace bangka.Objects;

public class KnownHostsStore
{
    private readonly string _storePath;
    private Dictionary<string, string> _entries; // "host:port" → fingerprint

    public KnownHostsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bangka");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "known_hosts.json");
        _entries   = Load();
    }

    public bool TryGet(string host, int port, out string? fingerprint)
    {
        return _entries.TryGetValue(Key(host, port), out fingerprint);
    }

    public void Trust(string host, int port, string fingerprint)
    {
        _entries[Key(host, port)] = fingerprint;
        Save();
    }

    public void Remove(string host, int port)
    {
        _entries.Remove(Key(host, port));
        Save();
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>();
        try
        {
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }
}
