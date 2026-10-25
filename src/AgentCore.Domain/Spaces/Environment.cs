using System.Text;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Spaces;

/// <summary>A Space-owned profile of typed variables. It carries no execution policy or grants.</summary>
public sealed record Environment(Guid TenantId, Guid SpaceId, Guid EnvironmentId, string Name, string Description,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null)
{
    public string NormalizedName => Name.Trim().ToUpperInvariant();
    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || EnvironmentId == Guid.Empty || Revision < 1 || UpdatedAt < CreatedAt)
            throw new ArgumentException("Invalid Environment ownership.");
        SpaceText.Require(Name, 120, required: true); SpaceText.Require(Description, 2048);
        if (Name != Name.Trim() || Name.Any(char.IsControl)) throw new ArgumentException("Invalid Environment name.");
    }
}

public enum EnvironmentVariableKind { Plain, SecretReference, CredentialReference }
public sealed record EnvironmentVariable(Guid TenantId, Guid SpaceId, Guid EnvironmentId, string Name, EnvironmentVariableKind Kind,
    string Description, [property: JsonIgnore] string? PlainValue, Guid? SecretId, Guid? CredentialId,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static string NormalizeName(string name)
    {
        name = name?.Trim().ToUpperInvariant() ?? "";
        if (name.Length is < 1 or > 64 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Use 1–64 letters, digits or underscores, starting with a letter or underscore.");
        return name;
    }
    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || EnvironmentId == Guid.Empty || !Enum.IsDefined(Kind)
            || Revision < 1 || UpdatedAt < CreatedAt || NormalizeName(Name) != Name || SecretId == Guid.Empty || CredentialId == Guid.Empty)
            throw new ArgumentException("Invalid variable ownership or name.");
        SpaceText.Require(Description, 1024);
        if (Kind switch {
            EnvironmentVariableKind.Plain => PlainValue is null || SecretId is not null || CredentialId is not null || Encoding.UTF8.GetByteCount(PlainValue) > 4096 || PlainValue.Contains('\0'),
            EnvironmentVariableKind.SecretReference => PlainValue is not null || SecretId is null || CredentialId is not null,
            EnvironmentVariableKind.CredentialReference => PlainValue is not null || SecretId is not null || CredentialId is null,
            _ => true }) throw new ArgumentException("A variable requires exactly one value matching its kind.");
    }
}
