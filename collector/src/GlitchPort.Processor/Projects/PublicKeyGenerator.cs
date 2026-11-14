using System.Security.Cryptography;

namespace GlitchPort.Processor.Projects;

/// <summary>
/// Generates the random public key that identifies a project in a DSN.
/// </summary>
/// <remarks>
/// A key is 16 random bytes rendered as 32 lowercase hex characters, so it can
/// be embedded verbatim in the user-info part of a DSN.
/// </remarks>
public sealed class PublicKeyGenerator
{
    /// <summary>Number of random bytes behind a public key.</summary>
    public const int RandomByteCount = 16;

    /// <summary>Number of characters of a generated public key.</summary>
    public const int PublicKeyLength = RandomByteCount * 2;

    /// <summary>Creates a new random public key.</summary>
    public string Generate()
    {
        Span<byte> bytes = stackalloc byte[RandomByteCount];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToHexStringLower(bytes);
    }
}
