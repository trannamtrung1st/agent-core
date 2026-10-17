using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Security;

public static class TenantSandboxProfileRules
{
    public static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "^p-[a-f0-9]{32}\\z");
    public static AccessScope Scope(Guid tenant, string id) => AccessScope.ForResource(tenant, "sandbox-profile", id);
    public static bool TryId(AccessScope scope, out string id)
    {
        id = scope.ResourceId ?? "";
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty
            && scope.ResourceType == "sandbox-profile" && ValidId(id);
    }
    public static void Validate(TenantSandboxProfileRoot root, SandboxProfile value, long expected)
    {
        value.Validate();
        if (root.TenantId == Guid.Empty || !ValidId(root.ProfileId) || value.TenantId != root.TenantId || value.Id != root.ProfileId
            || expected < 0 || value.Revision != checked(expected + 1) || value.LastVerifiedAt is not null || value.VerificationReason is not null
            || value.InstanceInitializationScript is not null || value.InstanceRevision is not null || value.AdmittedForExecution is not null
            || (value.RegistryCredentialId is null ? value.RegistryCredentialRevision is not null || value.RegistrySecretRevision is not null
                : value.RegistryCredentialRevision is not > 0 || value.RegistrySecretRevision is not >= 0))
            throw new ArgumentException("Invalid tenant Sandbox profile revision or ownership.");
    }
    public static string SearchText(SandboxProfile profile) => profile.Name.ToUpperInvariant() + "\n" + profile.Description.ToUpperInvariant();
    public static string SearchTerm(string search) => search.ToUpperInvariant();
    public static void Query(Guid tenant, int page, int size, string search)
    {
        if (tenant == Guid.Empty || page is < 0 or > 100_000 || size is < 1 or > 100 || search is null || search.Length > 128 || search.Any(char.IsControl))
            throw new ArgumentException("Use a bounded Sandbox profile page and search.");
    }
    public static bool DisablingUnchangedSource(SandboxProfile? old, SandboxProfile value) => !value.Enabled && old is not null
        && old.ImageReference == value.ImageReference && old.RegistryCredentialId == value.RegistryCredentialId
        && old.RegistryCredentialRevision == value.RegistryCredentialRevision && old.RegistrySecretRevision == value.RegistrySecretRevision;
    public static void RequireCredential(SandboxProfile value, Credential? credential, DateTimeOffset now)
    {
        if (credential is not { Purpose: CredentialPurpose.ContainerRegistry, Status: CredentialStatus.Active, ContainerRegistry: { } scope }
            || credential.CredentialId != value.RegistryCredentialId || credential.Revision != value.RegistryCredentialRevision
            || credential.SecretRevision != value.RegistrySecretRevision || scope.ExpiresAtUtc <= now || !scope.Allows(OciImageReference.Parse(value.ImageReference)))
            throw new SecurityConflictException("resource_changed");
    }
}
