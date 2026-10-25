using System.Text.Json.Serialization;
using AgentCore.Domain.Credentials;

namespace AgentCore.Domain.Spaces;

public enum SecretStatus { Active, Disabled, Deleted }
public sealed record Secret(Guid TenantId, Guid SecretId, string Name, string Description, SecretStatus Status,
    [property: JsonIgnore] string ProtectedPayload, int ProtectionVersion, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || SecretId == Guid.Empty || !Enum.IsDefined(Status) || ProtectionVersion < 1 || Revision < 1 || UpdatedAt < CreatedAt)
            throw new ArgumentException("Invalid Secret ownership.");
        if (CredentialRules.DisplayName(Name) != Name) throw new ArgumentException("Invalid Secret name.");
        SpaceText.Require(Description, 2048);
        if (Status == SecretStatus.Deleted ? ProtectedPayload != "" : string.IsNullOrWhiteSpace(ProtectedPayload) || ProtectedPayload.Length > 131072) throw new ArgumentException("An encrypted Secret payload is required.");
    }
}
