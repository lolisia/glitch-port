using System.Security.Cryptography;
using System.Text;

namespace GlitchPort.Processor.Security;

/// <summary>
/// Compares shared secrets and bearer tokens without leaking their length or
/// contents through timing.
/// </summary>
internal static class SecretComparer
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Compares two secrets by hashing both values first, so the comparison
    /// itself always runs in constant time and is independent of the input
    /// lengths.
    /// </summary>
    public static bool FixedTimeEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        Span<byte> providedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(provided), providedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);

        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }

    /// <summary>
    /// Validates an <c>Authorization</c> header of the form
    /// <c>Bearer &lt;token&gt;</c> against <paramref name="expectedToken"/>.
    /// </summary>
    public static bool MatchesBearerToken(string? authorizationHeader, string? expectedToken)
    {
        if (string.IsNullOrEmpty(authorizationHeader) || string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return FixedTimeEquals(authorizationHeader[BearerPrefix.Length..].Trim(), expectedToken);
    }
}
