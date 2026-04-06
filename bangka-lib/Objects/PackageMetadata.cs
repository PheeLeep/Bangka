using System;
using System.Xml.Serialization;

namespace bangka_lib.Objects;

[XmlRoot("metadata")]
public class PackageMetadata
{
    [XmlElement("name")]
    public string Name { get; set; } = string.Empty;

    [XmlElement("version")]
    public string Version { get; set; } = string.Empty;

    [XmlElement("description")]
    public string Description { get; set; } = string.Empty;

    [XmlElement("author")]
    public string Author { get; set; } = string.Empty;

    [XmlElement("aspPort")]
    public int AspPort { get; set; } = 5000;

    [XmlElement("aspEnvironment")]
    public string AspEnvironment { get; set; } = "Production";

    [XmlElement("dotnetRuntime")]
    public string DotnetRuntime { get; set; } = "net8.0";

    [XmlElement("checksum")]
    public string Checksum { get; set; } = string.Empty;

    [XmlElement("builtAt")]
    public string BuiltAt { get; set; } = string.Empty;

    [XmlElement("hasCloudflare")]
    public bool HasCloudflare { get; set; } = false;

    [XmlElement("entryDll")]
    public string EntryDll { get; set; } = string.Empty;

    /// <summary>
    /// Keys present in the env file at build time (not values — just key names).
    /// Used at deploy time to verify the remote env file has all required keys.
    /// </summary>
    [XmlArray("requiredEnvKeys")]
    [XmlArrayItem("key")]
    public List<string> RequiredEnvKeys { get; set; } = new();

    public static PackageMetadata Deserialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(PackageMetadata));
        using var reader = new StreamReader(xmlPath);
        return (PackageMetadata)serializer.Deserialize(reader)!;
    }

    public void Serialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(PackageMetadata));
        using var writer = new StreamWriter(xmlPath);
        serializer.Serialize(writer, this);
    }
}
