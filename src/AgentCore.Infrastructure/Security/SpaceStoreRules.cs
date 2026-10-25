using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

internal static class SpaceStoreRules
{
    internal static void Page(Guid tenant, int page, int size)
    { if (tenant == Guid.Empty || page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
    internal static void Validate(TenantSecurityState next, long expected, SpaceChange change, AuthorizationAudit audit)
    {
        change.Space.Validate(); var space = change.Space;
        if (space.DeletedAt is not null || space.TenantId != next.Tenant.TenantId || audit.TenantId != space.TenantId || audit.ActorPrincipalId == Guid.Empty
            || next.Tenant.Revision != checked(expected + 1) || change.ExpectedRevision < 0 || space.Revision != change.ExpectedRevision + 1)
            throw new ArgumentException("Invalid Space transaction.");
        if (change.Membership is { } member)
        {
            member.Freeze();
            if (member.TenantId != space.TenantId || member.SpaceId != space.SpaceId || change.ExpectedMembershipRevision is null
                || member.Revision != change.ExpectedMembershipRevision + 1 || change.ExpectedMembershipRevision < 0)
                throw new ArgumentException("Invalid Membership transaction.");
        }
        else if (change.RemoveMembership || change.ExpectedMembershipRevision is not null) throw new ArgumentException("Missing Membership.");
        if (change.Delete && change.Membership is not null) throw new ArgumentException("Delete memberships separately.");
        if (space.Kind == SpaceKind.Private && (change.Delete || change.ExpectedRevision == 0 || change.RemoveMembership
            || change.Membership is { } own && (own.InstanceId != space.OwnerInstanceId || change.ExpectedMembershipRevision is null or 0)))
            throw new AccessDeniedException("private_space_managed");
    }
    internal static bool Matches(Space? current, SpaceChange change) => current is null ? change.ExpectedRevision == 0
        : current.DeletedAt is null && current.Revision == change.ExpectedRevision && current.Kind == change.Space.Kind && current.OwnerInstanceId == change.Space.OwnerInstanceId
            && current.CreatedAt == change.Space.CreatedAt && change.Space.EnvironmentSelectionEpoch ==
                (current.EnvironmentsEnabled && !change.Space.EnvironmentsEnabled ? checked(current.EnvironmentSelectionEpoch + 1) : current.EnvironmentSelectionEpoch);
}
