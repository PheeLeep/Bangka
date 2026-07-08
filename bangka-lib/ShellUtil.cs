using System;
using System.Text.RegularExpressions;

namespace bangka_lib;

/// <summary>
/// Helpers for building shell commands safely. Every value that is interpolated
/// into a /bin/bash -c "..." string or an SSH command must go through Quote to
/// prevent command injection and word-splitting.
/// </summary>
public static class ShellUtil
{
    /// <summary>
    /// Wraps <paramref name="value"/> in POSIX single quotes so the shell treats
    /// it as a single literal argument. Embedded single quotes are escaped using
    /// the standard '\'' idiom. A null/empty value becomes an empty quoted string.
    /// </summary>
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "''";
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    // systemd unit names: letters, digits, and a small set of separators.
    // Must start with an alphanumeric so we never accept a leading '-' (which
    // would be parsed as a flag) or a path separator.
    private static readonly Regex ServiceNameRegex =
        new("^[A-Za-z0-9][A-Za-z0-9_.@-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true if <paramref name="name"/> is a safe systemd service name.
    /// </summary>
    public static bool IsValidServiceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ServiceNameRegex.IsMatch(name);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if the service name is unsafe.
    /// </summary>
    public static void ValidateServiceName(string? name)
    {
        if (!IsValidServiceName(name))
            throw new ArgumentException(
                $"Invalid service name '{name}'. Allowed: letters, digits, and _ . @ - (must start with a letter or digit).");
    }
}
