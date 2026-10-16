using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Security;

public static class TenantCredentialOAuthValidation
{
    // The authorization port may replace the grant, never the Credential's ordinary configuration.
    public static void RequireConnection(Credential current, Credential next, bool synthetic)
    {
        var subject = next.EmailAccount?.GoogleSubject ?? next.DriveAccount?.GoogleSubject;
        var address = next.EmailAccount?.EmailAddress ?? next.DriveAccount?.EmailAddress;
        var scopes = next.EmailAccount?.GrantedScopes ?? next.DriveAccount?.GrantedScopes;
        var actualSynthetic = next.EmailAccount?.Synthetic ?? next.DriveAccount?.Synthetic;
        if (subject is null || address is null || scopes is null || actualSynthetic != synthetic)
            throw new ArgumentException("Invalid verified OAuth grant.");
        var expected = TenantCredentialOAuthRules.Connect(current, new CredentialOAuthGrant(subject, address, scopes, next.ProtectedPayload, synthetic), next.UpdatedAtUtc);
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(next))
            throw new ArgumentException("Invalid OAuth connection transition.");
    }
}
