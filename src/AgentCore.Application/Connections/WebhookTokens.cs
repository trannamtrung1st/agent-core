using System.Security.Cryptography;
using System.Text;
using AgentCore.Domain.Connections;

namespace AgentCore.Application.Connections;

public sealed record WebhookCredential(Guid WebhookKey, string Token, WebhookCredentialStatus Status);

public static class WebhookTokens
{
    private static readonly byte[] DummyHash = new byte[32];

    public static string Create() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    public static bool Matches(string? storedHex, string presentedToken)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken ?? ""));
        var expected = Decode(storedHex) ?? DummyHash;
        var equal = CryptographicOperations.FixedTimeEquals(presented, expected);
        return equal && storedHex is not null && Decode(storedHex) is not null;
    }

    private static byte[]? Decode(string? storedHex)
    {
        if (storedHex is null || storedHex.Length != 64)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(storedHex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        var text = Convert.ToBase64String(bytes);
        return text.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
