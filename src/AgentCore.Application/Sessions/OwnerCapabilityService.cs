using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Sessions;

public sealed class OwnerCapabilityService(IOwnerCapabilityStore store, TimeProvider time) : IOwnerCapabilityService
{
    public async ValueTask<IssuedOwnerCapability> IssueAsync(CancellationToken cancellationToken = default)
    {
        var token = CreateToken();
        var now = time.GetUtcNow();
        await store.SaveHashAsync(Hash(token), now, cancellationToken).ConfigureAwait(false);
        return new IssuedOwnerCapability(token, now);
    }

    public async ValueTask<bool> ValidateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return await store.ContainsHashAsync(Hash(token), cancellationToken).ConfigureAwait(false);
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
