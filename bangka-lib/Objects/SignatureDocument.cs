using System;

namespace bangka_lib.Objects;

public class SignatureDocument
{
    public string Alg { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string SignedAt { get; set; } = string.Empty;
    public string PackageHash { get; set; } = string.Empty;
}
