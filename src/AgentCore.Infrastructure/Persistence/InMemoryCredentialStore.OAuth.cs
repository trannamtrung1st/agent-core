using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryCredentialStore
{
    internal bool ApplyTenantOAuthCheck(Guid tenant, Guid id, long revision, long secretRevision,
        AgentCore.Application.Ports.CredentialOAuthCheck check, DateTimeOffset at)
    {
        lock (instances.CredentialGate)
        {
            if (!OwnsTenantCredential(tenant, id)) return false;
            var current = _credentials[id];
            if (current.Revision != revision || current.SecretRevision != secretRevision) return false;
            _credentials[id] = TenantCredentialOAuthRules.Checked(current, check, at); return true;
        }
    }
    internal bool SaveTenantOAuthAuthorization(Guid tenant, Credential value, long expectedRevision, long expectedSecretRevision)
    {
        lock (instances.CredentialGate)
        {
            if (!OwnsTenantCredential(tenant, value.CredentialId)) return false;
            var current = _credentials[value.CredentialId];
            if (current.Revision != expectedRevision || current.SecretRevision != expectedSecretRevision) return false;
            value = TenantCredentialRules.Freeze(value);
            if (!TenantCredentialOAuthRules.IsOAuth(current) || value.Revision != checked(current.Revision + 1)
                || value.SecretRevision != checked(current.SecretRevision + 1) || TenantCredentialOAuthRules.Epoch(value) != checked(TenantCredentialOAuthRules.Epoch(current) + 1)
                || !TenantCredentialOAuthRules.SameNonAuthorization(current, value)) throw new ArgumentException("Invalid OAuth transition.");
            if (_credentials.Values.Any(c => c.CredentialId != value.CredentialId && OwnsTenantCredential(tenant, c.CredentialId)
                && (value.EmailAccount?.GoogleSubject is { } email && c.EmailAccount?.GoogleSubject == email
                    || value.DriveAccount?.GoogleSubject is { } drive && c.DriveAccount?.GoogleSubject == drive)))
                throw new SecurityConflictException("oauth_identity_already_connected");
            _credentials[value.CredentialId] = value; return true;
        }
    }
}
