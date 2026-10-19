using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBrowserProfileRules
{
    public static AccessScope Scope(TenantBrowserAgentOwner owner) => AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"));
    public static bool Matches(TenantBrowserAgentOwner owner, TenantAgent agent) => owner.TenantId == agent.TenantId
        && owner.AgentInstanceId == agent.Instance.InstanceId && owner.ExecutorPrincipalId == agent.PrincipalId;
    public static async ValueTask<bool> Allowed(TenantSecurityState state, TenantBrowserAgentOwner owner, Guid actor, IamAuthorizer iam, bool manage, CancellationToken ct)
    {
        foreach (var permission in manage ? new[] { "agents.read", "browser.read", "browser.manage" } : new[] { "agents.read", "browser.read" })
            if (!(await iam.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), Scope(owner), ct).ConfigureAwait(false)).Allowed) return false;
        return true;
    }
    public static void Validate(AgentBrowserProfileReset reset)
    {
        if (reset.OperationId == Guid.Empty || reset.PrincipalId == Guid.Empty || reset.ProviderEpoch == Guid.Empty
            || reset.Witness is not { Length: 64 } || reset.Witness.Any(c => !char.IsAsciiHexDigitLower(c))
            || reset.Status is not ("pending" or "reset" or "unknown") || reset.RequestedAt.Offset != TimeSpan.Zero
            || reset.Status == "pending" && (reset.RecordedAt is not null || reset.ReasonCode != "browser_profile_reset_started")
            || reset.Status == "reset" && reset.ReasonCode != "browser_profile_reset_completed"
            || reset.Status == "unknown" && reset.ReasonCode != "browser_profile_reset_unknown"
            || reset.Status != "pending" && (reset.RecordedAt is null || reset.RecordedAt.Value.Offset != TimeSpan.Zero || reset.RecordedAt < reset.RequestedAt
                || reset.ReasonCode is not ("browser_profile_reset_completed" or "browser_profile_reset_unknown")))
            throw new ArgumentException("Use exact bounded Browser profile reset facts.");
    }
}
