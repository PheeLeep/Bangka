using System;
using System.Security.Cryptography;
using System.Text.Json;
using bangka_lib;
using bangka_lib.Objects;

namespace bangkactl.Objects;

public static class TrustStore
{
    public static readonly string TrustDir    = "/etc/bangka";
    public static readonly string PubKeyPath  = "/etc/bangka/trusted.pub";
    public static readonly string PrivKeyPath = "/etc/bangka/trusted.key";
    public static readonly string FingerprintPath = "/etc/bangka/trusted.fingerprint";

    // ── State ─────────────────────────────────────────────────────────────────

    public static bool IsInitialized => File.Exists(PubKeyPath);

    public static string? GetPublicKeyPem()
    {
        if (!File.Exists(PubKeyPath)) return null;
        return File.ReadAllText(PubKeyPath).Trim();
    }

    public static string? GetFingerprint()
    {
        if (!File.Exists(FingerprintPath)) return null;
        return File.ReadAllText(FingerprintPath).Trim();
    }

    public static string? GetPrivateKeyPem()
    {
        if (!File.Exists(PrivKeyPath)) return null;
        return File.ReadAllText(PrivKeyPath).Trim();
    }

    // ── Key generation ────────────────────────────────────────────────────────

    public static (string PublicKeyPem, string Fingerprint) GenerateKeys(bool force = false)
    {
        if (IsInitialized && !force)
            throw new InvalidOperationException(
                $"Trust key already exists at {PubKeyPath}. Use 'trust rotate' to replace it.");

        Directory.CreateDirectory(TrustDir);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Write private key (root-only)
        var privPem = ecdsa.ExportECPrivateKeyPem();
        File.WriteAllText(PrivKeyPath, privPem);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PrivKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Write public key (readable)
        var pubPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(PubKeyPath, pubPem);

        // Write fingerprint for easy identification
        var fingerprint = ComputeFingerprint(ecdsa);
        File.WriteAllText(FingerprintPath, fingerprint);

        return (pubPem, fingerprint);
    }

    // ── Verification ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the package signature against the local trusted public key.
    /// Returns (ok, message).
    /// Only called when IsInitialized is true.
    /// </summary>
    public static (bool Ok, string Message) VerifySignature(
        string packagePath, string sigJson)
    {
        var pubPem = GetPublicKeyPem();
        if (pubPem == null)
            return (false, "No trusted key installed on this server.");

        SignatureDocument sigDoc;
        try
        {
            sigDoc = JsonSerializer.Deserialize<SignatureDocument>(sigJson)
                     ?? throw new Exception("Empty signature document.");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot parse signature: {ex.Message}");
        }

        // Verify package hash
        var actualHash = ChecksumHelper.ComputeSha512(packagePath);
        if (!string.Equals(actualHash, sigDoc.PackageHash, StringComparison.OrdinalIgnoreCase))
            return (false, "Package hash does not match signature — package may have been tampered with.");

        // Verify ECDsa signature
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(PemToBytes(pubPem, "PUBLIC KEY"), out _);

            var packageBytes = File.ReadAllBytes(packagePath);
            var sigBytes     = Convert.FromBase64String(sigDoc.Signature);
            bool valid = ecdsa.VerifyData(packageBytes, sigBytes, HashAlgorithmName.SHA512);

            return valid
                ? (true,  $"Signature valid (key: {sigDoc.KeyId}, signed: {sigDoc.SignedAt})")
                : (false, $"Signature invalid — key mismatch or tampered package.");
        }
        catch (Exception ex)
        {
            return (false, $"Verification error: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeFingerprint(ECDsa ecdsa)
    {
        var pubBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var hash     = SHA256.HashData(pubBytes);
        // Format as SHA256:xx:xx:xx... (OpenSSH style)
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return "SHA256:" + string.Join(":", Enumerable.Range(0, hex.Length / 2)
            .Select(i => hex.Substring(i * 2, 2)));
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

