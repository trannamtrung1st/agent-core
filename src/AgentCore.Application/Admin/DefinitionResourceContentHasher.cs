using System.Security.Cryptography;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

public static class DefinitionResourceContentHasher
{
    public static string ComputeSha256Hex(ReadOnlySpan<byte> content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void EnsureMatches(string expectedSha256, ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
        {
            throw AgentCoreErrors.Validation("contentSha256 must be a lowercase SHA-256 hex digest.");
        }

        var actual = ComputeSha256Hex(content);
        if (!string.Equals(expectedSha256, actual, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("contentSha256 does not match the uploaded bytes.");
        }
    }
}
