using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.DataProtection;

namespace AgentCore.Infrastructure.Credentials;

internal sealed record TenantOAuthProof(string Verifier, string Nonce);
public sealed class TenantOAuthProofProtector(string keyRoot)
{
    private readonly Lazy<IDataProtectionProvider> _provider = new(() =>
    {
        var path = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(keyRoot, "credential-oauth")));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return DataProtectionProvider.Create(path, b => b.SetApplicationName("AgentCore.TenantCredentialOAuth.v1"));
    });
    private IDataProtector For(Guid attempt) => _provider.Value.CreateProtector("AgentCore.OAuth.PrivateProof.v1", attempt.ToString("D"));
    internal string Protect(Guid attempt, TenantOAuthProof proof) => Run(() => For(attempt).Protect(JsonSerializer.Serialize(proof)));
    internal TenantOAuthProof Unprotect(Guid attempt, string payload) => Run(() =>
    {
        var proof = JsonSerializer.Deserialize<TenantOAuthProof>(For(attempt).Unprotect(payload));
        if (proof is null || proof.Verifier is not { Length: 43 } || proof.Nonce is not { Length: 43 }) throw new JsonException();
        return proof;
    });
    private static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        { throw new AgentCoreException("oauth_proof_unavailable", "Authorization proof is unavailable. Connect again.", 409); }
    }
}
