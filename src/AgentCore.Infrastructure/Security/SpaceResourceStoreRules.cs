using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

internal static class SpaceResourceStoreRules
{
    internal static void Validate(TenantSecurityState next, long expectedSecurity, long expectedSpace, SpaceFileResource value, long expectedResource, AuthorizationAudit audit)
    {
        value.Validate();
        if (expectedSecurity < 1 || expectedSpace < 1 || expectedResource < 0 || value.Revision != checked(expectedResource + 1)
            || next.Tenant.Revision != checked(expectedSecurity + 1) || value.TenantId != next.Tenant.TenantId
            || audit.TenantId != value.TenantId || audit.ActorPrincipalId == Guid.Empty || audit.At != value.UpdatedAt
            || expectedResource == 0 && value.DeletedAt is not null) throw new ArgumentException("Invalid Space resource transaction.");
    }
    internal static bool Matches(SpaceFileResource? current, SpaceFileResource value, long expected) => current is null
        ? expected == 0 && value.DeletedAt is null
        : current.DeletedAt is null && current.Revision == expected && current.CreatedAt == value.CreatedAt && value.UpdatedAt >= current.UpdatedAt;
    internal static void Limits(IReadOnlyList<SpaceFileResource> current, SpaceFileResource value)
    {
        var next = current.Where(r => r.ResourceId != value.ResourceId && r.DeletedAt is null).Concat(value.DeletedAt is null ? [value] : []).ToArray();
        if (next.Length > AgentCore.Domain.Definitions.AgentResourceLimits.MaxItemsPerDraft
            || next.Sum(r => r.ByteLength) > AgentCore.Domain.Definitions.AgentResourceLimits.MaxAggregateBytes)
            throw new SecurityConflictException("space_resource_limit");
        if (next.Select(r => r.LogicalPath).Distinct(StringComparer.Ordinal).Count() != next.Length)
            throw new SecurityConflictException("space_resource_path_exists");
    }
}
