using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed record TenantHumanExperienceOwner(Guid TenantId, Guid AgentId, Guid ExecutorPrincipalId, string AgentName, TenantUser User, bool CanManage, long SecurityRevision)
{
    public string[] Keys => ["instance", "user:" + User.PrincipalId.ToString("D")];
}
public static class TenantExperienceManagementRules
{
    public static void Validate(TenantExperienceQuery query)
    {
        if (query.Page is < 1 or > 100000 || query.PageSize is < 1 or > 100 || query.ExperienceId == Guid.Empty)
            throw AgentCoreErrors.Validation("Use valid Experience pagination and an exact optional observation.");
    }
    public static async ValueTask<TenantHumanExperienceOwner> OwnerAsync(TenantSecurityState state, TenantAgent agent, HumanSessionReference human, IamAuthorizer iam, CancellationToken ct)
    {
        var user = state.Users.SingleOrDefault(i => i.PrincipalId == human.PrincipalId && i.AccountId == human.AccountId && i.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("initiator_unavailable");
        if (agent.TenantId != state.Tenant.TenantId) throw new AccessDeniedException("experience_unavailable");
        var scope = AccessScope.ForResource(state.Tenant.TenantId, "agent", agent.Instance.InstanceId.ToString("D"));
        foreach (var permission in new[] { "agents.read", "memory.read" })
            if (!(await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("experience_unavailable");
        var manage = (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("memory.manage"), scope, ct).ConfigureAwait(false)).Allowed;
        return new(state.Tenant.TenantId, agent.Instance.InstanceId, agent.PrincipalId, agent.Instance.Persona.Name, user, manage, state.Tenant.Revision);
    }
    public static bool Owns(TenantHumanExperienceOwner owner, TenantExperience item) => item.TenantId == owner.TenantId && item.AgentInstanceId == owner.AgentId && item.ExecutorPrincipalId == owner.ExecutorPrincipalId
        && (item.UserPrincipalId is null && item.AccountId is null && item.UserId is null || item.UserPrincipalId == owner.User.PrincipalId && item.AccountId == owner.User.AccountId && item.UserId == owner.User.UserId);
    public static void Validate(TenantExperienceChange change, Guid tenant, Guid agent, HumanSessionReference human, AuthorizationAudit audit)
    {
        if (change.ExpectedSecurityRevision < 1 || change.ExperienceId == Guid.Empty || !Enum.IsDefined(change.Visibility) || change.Visibility == ExperienceVisibility.Superseded
            || change.ExperienceId is null && (change.Visibility != ExperienceVisibility.Deleted || change.ExpectedExperienceRevision is not null || change.ExpectedRetainedCount is null or < 1)
            || change.ExperienceId is not null && (change.ExpectedExperienceRevision is null or < 1 || change.ExpectedRetainedCount is not null)
            || audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.AuditId == Guid.Empty || audit.At.Offset != TimeSpan.Zero
            || audit.Action != "experience.manage" || audit.Target != agent.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "human_experience_changed")
            throw AgentCoreErrors.Validation("Supply exact Experience visibility or reset witnesses.");
    }
    public static TenantExperience Change(TenantExperience item, ExperienceVisibility visibility, DateTimeOffset at)
    {
        if (item.Visibility == ExperienceVisibility.Deleted || item.Visibility == ExperienceVisibility.Superseded && visibility != ExperienceVisibility.Deleted)
            throw new AccessDeniedException("experience_terminal");
        return item with { Content = visibility == ExperienceVisibility.Deleted ? null : TenantExperienceRules.Freeze(item.Content!), Visibility = visibility,
            Revision = checked(item.Revision + 1), UpdatedAt = at > item.UpdatedAt ? at : item.UpdatedAt };
    }
}
