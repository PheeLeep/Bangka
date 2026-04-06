using System;
using System.Security.Cryptography;
using System.Text.Json;
using bangka_lib;
using bangka_lib.Objects;

namespace bangka.Objects;

public static class PackageSigner
{
    private static readonly string KeyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bangka");

    public static string PrivateKeyPath => Path.Combine(KeyDir, "signing.key");
    public static string PublicKeyPath  => Path.Combine(KeyDir, "signing.pub");

    // ── Key generation ────────────────────────────────────────────────────────

    public static void GenerateKeys(bool force = false)
    {
        Directory.CreateDirectory(KeyDir);

        if (File.Exists(PrivateKeyPath) && !force)
            throw new InvalidOperationException(
                $"Signing key already exists at {PrivateKeyPath}. Use --force to overwrite.");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Write private key
        var privPem = ecdsa.ExportECPrivateKeyPem();
        File.WriteAllText(PrivateKeyPath, privPem);
        // Restrict permissions on Linux
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PrivateKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Write public key
        var pubPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(PublicKeyPath, pubPem);
    }

    // ── Sign ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs the .aspkg file and writes a detached .aspkg.sig next to it.
    /// The sig file is JSON: { "alg": "ES256", "keyId": "...", "signature": "base64" }
    /// </summary>
    public static void SignPackage(string packagePath, string? privateKeyPath = null)
    {
        privateKeyPath ??= PrivateKeyPath;

        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException(
                $"Signing key not found at {privateKeyPath}. Run: bangka keygen");

        var keyPem  = File.ReadAllText(privateKeyPath);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(PemToBytes(keyPem, "EC PRIVATE KEY"), out _);

        var packageBytes = File.ReadAllBytes(packagePath);
        var signature    = ecdsa.SignData(packageBytes, HashAlgorithmName.SHA512);

        // Derive a short key ID from the public key for identification
        var keyId = ComputeKeyId(ecdsa);

        var sigDoc = new SignatureDocument
        {
            Alg       = "ES256-SHA512",
            KeyId     = keyId,
            Signature = Convert.ToBase64String(signature),
            SignedAt  = DateTime.UtcNow.ToString("O"),
            PackageHash = ChecksumHelper.ComputeSha512(packagePath)
        };

        var sigPath = packagePath + ".sig";
        File.WriteAllText(sigPath, JsonSerializer.Serialize(sigDoc, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    // ── Verify ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the .aspkg.sig against the package.
    /// Returns (success, message).
    /// </summary>
    public static (bool Ok, string Message) VerifyPackage(
        string packagePath, string? publicKeyPath = null)
    {
        publicKeyPath ??= PublicKeyPath;

        if (!File.Exists(publicKeyPath))
            return (false,
                $"Public key not found at {publicKeyPath}. Run: bangka keygen");

        var sigPath = packagePath + ".sig";
        if (!File.Exists(sigPath))
            return (false,
                $"No signature file found at {sigPath}. Package was not signed or .sig was not transferred.");

        SignatureDocument sigDoc;
        try
        {
            sigDoc = JsonSerializer.Deserialize<SignatureDocument>(
                File.ReadAllText(sigPath))
                ?? throw new Exception("Empty signature file.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not parse signature file: {ex.Message}");
        }

        // Verify package hash first (quick integrity check)
        var actualHash = ChecksumHelper.ComputeSha512(packagePath);
        if (!string.Equals(actualHash, sigDoc.PackageHash, StringComparison.OrdinalIgnoreCase))
            return (false,
                "Package hash does not match signature file — package may have been tampered with.");

        // Verify ECDsa signature
        var keyPem = File.ReadAllText(publicKeyPath);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(PemToBytes(keyPem, "PUBLIC KEY"), out _);

        var packageBytes = File.ReadAllBytes(packagePath);
        var sigBytes     = Convert.FromBase64String(sigDoc.Signature);

        bool valid = ecdsa.VerifyData(packageBytes, sigBytes, HashAlgorithmName.SHA512);

        return valid
            ? (true,  $"Signature valid. Signed at {sigDoc.SignedAt} by key {sigDoc.KeyId}")
            : (false, $"Signature INVALID — package may have been forged or tampered with.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeKeyId(ECDsa ecdsa)
    {
        var pubBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var hash     = SHA256.HashData(pubBytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static byte[] PemToBytes(string pem, string label)
    {
        var header = $"-----BEGIN {label}-----";
        var footer = $"-----END {label}-----";
        var start  = pem.IndexOf(header, StringComparison.Ordinal) + header.Length;
        var end    = pem.IndexOf(footer, StringComparison.Ordinal);
        var base64 = pem[start..end].Replace("\n", "").Replace("\r", "").Trim();
        return Convert.FromBase64String(base64);
    }
}
