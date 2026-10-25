using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using System.Text.Json;

namespace AgentCore.Application.Security;

public static class TenantCredentialRules
{
    public static AccessScope Scope(Guid tenant, Guid id) => AccessScope.ForResource(tenant, "credential", id.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty
            && scope.ResourceType == "credential" && Guid.TryParseExact(scope.ResourceId, "D", out id)
            && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }

    public static Credential Freeze(Credential value)
    {
        if (value.CredentialId == Guid.Empty || !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Status)
            || !Enum.IsDefined(value.Purpose) || value.ProtectionVersion != 1 || value.SecretRevision < 0
            || value.ProtectedPayload is null || value.ProtectedPayload.Length > 262144 || value.AllowedOrigins is null)
            throw new ArgumentException("Invalid credential value.");
        var email = value.EmailAccount?.Validate(); var drive = value.DriveAccount?.Validate();
        var registry = value.ContainerRegistry?.Validate();
        if (email?.AuthMethod == EmailAuthMethod.Password && (email.ConnectionStatus != EmailConnectionStatus.NotConnected
            || email.AuthEpoch != 0 || email.ConnectedAtUtc is not null || email.Synthetic))
            throw new ArgumentException("Custom email authorization state is server-owned.");
        if ((value.Purpose == CredentialPurpose.EmailAccount) != (email is not null)
            || (value.Purpose == CredentialPurpose.GoogleDriveAccount) != (drive is not null)
            || (value.Purpose == CredentialPurpose.ContainerRegistry) != (registry is not null)
            || email is not null && value.Kind != (email.AuthMethod == EmailAuthMethod.Password ? CredentialKind.Password : CredentialKind.Token)
            || drive is not null && value.Kind != CredentialKind.Token
            || value.Purpose == CredentialPurpose.ModelInference && value.Kind != CredentialKind.ApiKey
            || registry is not null && value.Kind is not (CredentialKind.Password or CredentialKind.Token)
            || value.Purpose != CredentialPurpose.General && value.AllowedOrigins.Count != 0)
            throw new ArgumentException("Invalid credential purpose/profile.");
        if (email?.AuthMethod == EmailAuthMethod.GoogleOAuth2 || drive is not null)
        {
            var scopes = TenantCredentialOAuthRules.Scopes(value.Purpose, email?.GrantedScopes ?? drive?.GrantedScopes);
            if (email is not null) email = email with { GrantedScopes = scopes };
            if (drive is not null) drive = drive with { GrantedScopes = scopes };
            var connected = email?.ConnectionStatus == EmailConnectionStatus.Connected || drive?.ConnectionStatus == DriveConnectionStatus.Connected;
            if (connected != (value.ProtectedPayload.Length > 0) || connected && value.SecretRevision == 0
                || (email?.GoogleSubject ?? drive?.GoogleSubject) is { } subject && (subject.Length is < 1 or > 255 || subject.Any(char.IsControl)))
                throw new ArgumentException("Invalid protected OAuth authorization state.");
        }
        else if (string.IsNullOrEmpty(value.ProtectedPayload)) throw new ArgumentException("Protected credential value is required.");
        return value with { DisplayName = CredentialRules.DisplayName(value.DisplayName), Metadata = CredentialRules.Metadata(value.Metadata),
            AllowedOrigins = CredentialRules.Origins(value.AllowedOrigins), EmailAccount = email, DriveAccount = drive, ContainerRegistry = registry };
    }

    public static void ValidateChange(TenantCredentialRoot owner, Credential value, Credential? current, long expectedRevision, long expectedSecretRevision)
    {
        if (owner.TenantId == Guid.Empty || owner.CredentialId != value.CredentialId || owner.DeletedAt is not null
            || owner.CreatedAt != value.CreatedAtUtc || value.Revision != checked(expectedRevision + 1)
            || expectedRevision < 0 || expectedSecretRevision < 0) throw new ArgumentException("Invalid credential transaction.");
        if (current is null)
        {
            if (expectedRevision != 0 || expectedSecretRevision != 0 || value.SecretRevision != 0 || value.Status != CredentialStatus.Active)
                throw new ArgumentException("Invalid initial credential.");
            RequireInitialOAuth(value);
            return;
        }
        if (current.Kind != value.Kind || current.Purpose != value.Purpose || current.CreatedAtUtc != value.CreatedAtUtc
            || current.ProtectionVersion != value.ProtectionVersion || current.EmailAccount?.AuthMethod != value.EmailAccount?.AuthMethod
            || current.EmailAccount?.Preset != value.EmailAccount?.Preset)
            throw new ArgumentException("Credential identity, purpose and authentication method are immutable.");
        if (TenantCredentialOAuthRules.IsOAuth(current))
        {
            var expected = TenantCredentialOAuthRules.AdvanceStatusEpoch(current, value.Status);
            if (value.ProtectedPayload != current.ProtectedPayload || value.SecretRevision != current.SecretRevision
                || JsonSerializer.Serialize(value.EmailAccount) != JsonSerializer.Serialize(expected.EmailAccount)
                || JsonSerializer.Serialize(value.DriveAccount) != JsonSerializer.Serialize(expected.DriveAccount))
                throw new ArgumentException("OAuth authorization is changed only through its correlated flow.");
        }
        var changed = current.ProtectedPayload != value.ProtectedPayload;
        if (value.SecretRevision != checked(current.SecretRevision + (changed ? 1 : 0))) throw new ArgumentException("Invalid secret generation.");
    }
    public static void RequireInitialOAuth(Credential value)
    {
        if (!TenantCredentialOAuthRules.IsOAuth(value)) return;
        var email = value.EmailAccount; var drive = value.DriveAccount;
        if (value.ProtectedPayload != "" || value.SecretRevision != 0 || TenantCredentialOAuthRules.Epoch(value) != 0
            || email is not null && (email.ConnectionStatus != EmailConnectionStatus.NotConnected || email.GoogleSubject is not null || email.GrantedScopes is not null || email.ConnectedAtUtc is not null || email.Synthetic)
            || drive is not null && (drive.ConnectionStatus != DriveConnectionStatus.NotConnected || drive.GoogleSubject is not null || drive.GrantedScopes is not null || drive.ConnectedAtUtc is not null || drive.ReconnectRequiredAtUtc is not null || drive.Synthetic))
            throw new ArgumentException("Use correlated OAuth authorization to connect this credential.");
    }
}
