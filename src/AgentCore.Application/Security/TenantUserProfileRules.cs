using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantUserProfileRules
{
    public static void Commit(TenantUserProfile profile, HumanSessionReference human, long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit)
    {
        profile.Freeze();
        if (profile.AccountId != human.AccountId || profile.PrincipalId != human.PrincipalId || expectedSecurityRevision < 1 || expectedProfileRevision < 0
            || profile.Revision != checked(expectedProfileRevision + 1) || audit.AuditId == Guid.Empty || audit.TenantId != profile.TenantId
            || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "profile.update" || audit.Target != human.PrincipalId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != "profile_updated" || audit.At != profile.UpdatedAt || audit.Execution is not null)
            throw new ArgumentException("Invalid owned User Profile commit.");
    }
    public static TenantUserProfile Empty(TenantUser user) => new(user.TenantId, user.PrincipalId, user.AccountId, 0,
        new Dictionary<string, AgentCore.Domain.Conversation.UserProfileValue>(StringComparer.Ordinal), DateTimeOffset.FromUnixTimeMilliseconds(user.CreatedAt.ToUnixTimeMilliseconds()));
}
