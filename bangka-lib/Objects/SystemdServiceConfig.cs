using System;
using System.Xml.Serialization;

namespace bangka_lib.Objects;

[XmlRoot("systemdservice")]
public class SystemdServiceConfig
{
    [XmlElement("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [XmlElement("description")]
    public string Description { get; set; } = string.Empty;

    [XmlElement("user")]
    public string User { get; set; } = "www-data";

    [XmlElement("workingDirectory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [XmlElement("execStart")]
    public string ExecStart { get; set; } = string.Empty;

    [XmlElement("restartPolicy")]
    public string RestartPolicy { get; set; } = "on-failure";

    [XmlElement("restartSec")]
    public int RestartSec { get; set; } = 5;

    [XmlElement("environment")]
    public string Environment { get; set; } = "Production";

    [XmlElement("aspNetPort")]
    public int AspNetPort { get; set; } = 5000;

    [XmlElement("environmentFile")]
    public string EnvironmentFile { get; set; } = string.Empty;

    public static SystemdServiceConfig Deserialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(SystemdServiceConfig));
        using var reader = new StreamReader(xmlPath);
        return (SystemdServiceConfig)serializer.Deserialize(reader)!;
    }

    public void Serialize(string xmlPath)
    {
        var serializer = new XmlSerializer(typeof(SystemdServiceConfig));
        using var writer = new StreamWriter(xmlPath);
        serializer.Serialize(writer, this);
    }

    public string ToUnitFileContent()
    {
        return $"""
[Unit]
Description={Description}
After=network.target

[Service]
Type=simple
User={User}
WorkingDirectory={WorkingDirectory}
ExecStart={ExecStart}
Restart={RestartPolicy}
RestartSec={RestartSec}
Environment=ASPNETCORE_ENVIRONMENT={Environment}
Environment=ASPNETCORE_URLS=http://0.0.0.0:{AspNetPort}
{(string.IsNullOrWhiteSpace(EnvironmentFile) ? "" : $"EnvironmentFile={EnvironmentFile}\n")}SyslogIdentifier={ServiceName}
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
""";
    }
}
