using System;
using System.Security.Cryptography;

namespace bangka_lib;

public static class ChecksumHelper
{
    public static string ComputeSha512(string filePath)
    {
        using var sha = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Verify(string filePath, string expectedChecksum)
    {
        var actual = ComputeSha512(filePath);
        return string.Equals(actual, expectedChecksum.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
