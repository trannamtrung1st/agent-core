using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Security;

public static class TenantCredentialOAuthRules
{
    public static bool IsOAuth(Credential value) => value.Purpose == CredentialPurpose.GoogleDriveAccount
        || value.Purpose == CredentialPurpose.EmailAccount && value.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2;
    public static long Epoch(Credential value) => value.EmailAccount?.AuthEpoch ?? value.DriveAccount?.AuthEpoch ?? throw new ArgumentException("OAuth profile is required.");
    public static IReadOnlyList<string>? Scopes(CredentialPurpose purpose, IReadOnlyList<string>? scopes)
    {
        if (scopes is null) return null;
        var allowed = new[] { "openid", "email", "https://www.googleapis.com/auth/userinfo.email" }
            .Concat(purpose == CredentialPurpose.EmailAccount ? new[] { EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope } : new[] { DriveAccountProfile.Scope }).ToHashSet(StringComparer.Ordinal);
        if (scopes.Count > 8 || scopes.Any(s => s is null || !allowed.Contains(s))) throw new ArgumentException("Unexpected OAuth scope.");
        return Array.AsReadOnly(scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    public static Credential AdvanceStatusEpoch(Credential value, CredentialStatus status)
    {
        if (!IsOAuth(value) || status == value.Status) return value with { Status = status };
        return value with { Status = status, EmailAccount = value.EmailAccount is { } email ? email with { AuthEpoch = checked(email.AuthEpoch + 1) } : null,
            DriveAccount = value.DriveAccount is { } drive ? drive with { AuthEpoch = checked(drive.AuthEpoch + 1) } : null };
    }
    public static Credential Connect(Credential current, CredentialOAuthGrant grant, DateTimeOffset at)
    {
        if (!IsOAuth(current) || current.Status != CredentialStatus.Active || string.IsNullOrWhiteSpace(grant.Subject) || grant.Subject.Length > 255
            || grant.Subject.Any(char.IsControl) || string.IsNullOrEmpty(grant.EmailAddress) || string.IsNullOrEmpty(grant.ProtectedPayload) || grant.Scopes is null)
            throw new ArgumentException("An active OAuth credential and protected grant are required.");
        var subject = current.EmailAccount?.GoogleSubject ?? current.DriveAccount?.GoogleSubject;
        var address = current.EmailAccount?.EmailAddress ?? current.DriveAccount!.EmailAddress;
        if (subject is not null && subject != grant.Subject || subject is null && address.Length > 0 && !string.Equals(address, grant.EmailAddress, StringComparison.OrdinalIgnoreCase))
            throw new AccessDeniedException("oauth_identity_mismatch");
        var scopes = Scopes(current.Purpose, grant.Scopes)!;
        if (current.Purpose == CredentialPurpose.EmailAccount ? !scopes.Contains(EmailAccountProfile.ReadScope) || !scopes.Contains(EmailAccountProfile.SendScope) : !scopes.Contains(DriveAccountProfile.Scope))
            throw new AccessDeniedException("oauth_scope_missing");
        var epoch = checked(Epoch(current) + 1);
        return TenantCredentialRules.Freeze(current with { ProtectedPayload = grant.ProtectedPayload, SecretRevision = checked(current.SecretRevision + 1),
            Revision = checked(current.Revision + 1), UpdatedAtUtc = at,
            EmailAccount = current.EmailAccount is { } email ? email with { EmailAddress = grant.EmailAddress, GoogleSubject = grant.Subject, GrantedScopes = scopes,
                ConnectionStatus = EmailConnectionStatus.Connected, AuthEpoch = epoch, ConnectedAtUtc = at, Synthetic = grant.Synthetic } : null,
            DriveAccount = current.DriveAccount is { } drive ? drive with { EmailAddress = grant.EmailAddress, GoogleSubject = grant.Subject, GrantedScopes = scopes,
                ConnectionStatus = DriveConnectionStatus.Connected, AuthEpoch = epoch, ConnectedAtUtc = at, ReconnectRequiredAtUtc = null, Synthetic = grant.Synthetic } : null });
    }
    public static Credential Disconnect(Credential current, DateTimeOffset at)
    {
        if (!IsOAuth(current)) throw new ArgumentException("OAuth credential is required.");
        return TenantCredentialRules.Freeze(current with { ProtectedPayload = "", SecretRevision = checked(current.SecretRevision + 1), Revision = checked(current.Revision + 1), UpdatedAtUtc = at,
            EmailAccount = current.EmailAccount is { } email ? email with { ConnectionStatus = EmailConnectionStatus.Disconnected, AuthEpoch = checked(email.AuthEpoch + 1) } : null,
            DriveAccount = current.DriveAccount is { } drive ? drive with { ConnectionStatus = DriveConnectionStatus.Disconnected, AuthEpoch = checked(drive.AuthEpoch + 1), ReconnectRequiredAtUtc = null } : null });
    }
    public static CredentialOAuthAttempt Attempt(CredentialOAuthAttempt value)
    {
        if (new[] { value.AttemptId, value.TenantId, value.CredentialId, value.AccountId, value.AuthenticationSessionId, value.PrincipalId }.Any(id => id == Guid.Empty)
            || value.Purpose is not (CredentialPurpose.EmailAccount or CredentialPurpose.GoogleDriveAccount) || value.CredentialRevision < 1 || value.AuthEpoch < 0
            || !IsDigest(value.StateHash) || !IsDigest(value.CorrelationHash) || value.ProtectedProof is not { Length: > 0 and <= 65536 }
            || value.Provider is null || value.Provider.ClientId is not { Length: > 0 and <= 2048 } || value.Provider.ClientId.Any(char.IsControl)
            || value.Provider.RedirectUri is not { Length: > 0 and <= 2048 }
            || !Uri.TryCreate(value.Provider.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "https" && !(redirect.Scheme == "http" && redirect.IsLoopback)
            || redirect.UserInfo.Length > 0 || redirect.Fragment.Length > 0 || redirect.Query.Length > 0 || !value.Provider.Configured
            || value.Revision != 1 || value.Outcome != CredentialOAuthOutcome.Pending || value.ClaimedAt is not null || value.CompletedAt is not null
            || value.ExpiresAt <= value.CreatedAt || value.ExpiresAt > value.CreatedAt.AddMinutes(5)) throw new ArgumentException("Invalid OAuth attempt.");
        return value;
    }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool DigestMatches(string a, string b) => IsDigest(a) && IsDigest(b)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    public static bool SameNonAuthorization(Credential a, Credential b) => JsonSerializer.Serialize(a with { EmailAccount = b.EmailAccount, DriveAccount = b.DriveAccount,
        ProtectedPayload = b.ProtectedPayload, SecretRevision = b.SecretRevision, Revision = b.Revision, UpdatedAtUtc = b.UpdatedAtUtc }) == JsonSerializer.Serialize(b);
}
