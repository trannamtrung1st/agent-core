using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth;
using AgentCore.Application.Sessions;
namespace AgentCore.Infrastructure.Credentials;

internal sealed record GoogleOAuthIdentity(string Subject, string Email);
internal static class GoogleOAuthProof
{
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Random() => Encode(RandomNumberGenerator.GetBytes(32));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Challenge(string verifier) => Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static async Task<GoogleOAuthIdentity> VerifyIdentity(string token, string clientId, string nonce, CancellationToken ct)
    {
        var identity = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] }).WaitAsync(ct);
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/'); part = part.PadRight((part.Length + 3) / 4 * 4, '=');
        using var body = JsonDocument.Parse(Convert.FromBase64String(part));
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Subject) || !body.RootElement.TryGetProperty("nonce", out var n) || n.GetString() != nonce)
            throw AgentCoreErrors.Validation("Google identity validation failed. Connect again.");
        return new(identity.Subject, identity.Email);
    }
}
