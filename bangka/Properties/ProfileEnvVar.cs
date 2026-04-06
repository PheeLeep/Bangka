using System;
using System.Xml.Serialization;

namespace bangka.Properties;


/// <summary>
/// A single environment variable entry stored inside a profile.
/// </summary>
public class ProfileEnvVar
{
    [XmlAttribute("key")]
    public string Key { get; set; } = string.Empty;

    [XmlAttribute("value")]
    public string Value { get; set; } = string.Empty;
}