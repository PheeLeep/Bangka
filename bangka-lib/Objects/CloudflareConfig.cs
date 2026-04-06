using System;
using System.Xml.Serialization;

namespace bangka_lib.Objects;


[XmlRoot("cloudflared")]
public class CloudflaredConfig
{
    [XmlElement("tunnelName")]
    public string TunnelName { get; set; } = string.Empty;

    [XmlElement("tunnelId")]
    public string TunnelId { get; set; } = string.Empty;

    [XmlElement("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [XmlElement("serviceUrl")]
    public string ServiceUrl { get; set; } = string.Empty;

    [XmlElement("credentialsFile")]
    public string CredentialsFile { get; set; } = string.Empty;

    public static CloudflaredConfig Deserialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(CloudflaredConfig));
        using var reader = new StreamReader(xmlPath);
        return (CloudflaredConfig)serializer.Deserialize(reader)!;
    }

    public void Serialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(CloudflaredConfig));
        using var writer = new StreamWriter(xmlPath);
        serializer.Serialize(writer, this);
    }

    /// <summary>
    /// Generates a standalone cloudflared config yaml (used when no existing config is present).
    /// </summary>
    public string ToConfigYaml()
    {
        return $"""
tunnel: {TunnelId}
credentials-file: {CredentialsFile}

ingress:
  - hostname: {Hostname}
    service: {ServiceUrl}
  - service: http_status:404
""";
    }

    /// <summary>
    /// Returns just the ingress rule block for this service — used when
    /// appending to an existing cloudflared config that already has a tunnel header.
    /// </summary>
    public string ToIngressRule()
    {
        return $"  - hostname: {Hostname}\n    service: {ServiceUrl}";
    }
}
