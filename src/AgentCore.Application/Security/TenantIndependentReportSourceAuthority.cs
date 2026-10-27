using System.Security.Cryptography;
using System.Text;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Exact read targets of a retained independent Automation/Event/Manual result.
public static class TenantIndependentReportSourceAuthority
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedReportSourceRead read) =>
        new[] { ("sessions.read", TenantSessionRules.Scope(tenant, read.ChildSessionId)),
            ("agent-runs.read", TenantRunRules.Scope(tenant, read.InitialRunId)), ("agent-runs.read", TenantRunRules.Scope(tenant, read.SourceRunId)) }.Distinct();
    public static async ValueTask PermissionsAsync(TenantSecurityState state, PreparedReportSourceRead read, Guid principal, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (!(await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
    }
    public static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, PreparedReportSourceRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, PreparedReportSourceRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, PreparedReportSourceRead read, Guid? requester, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, read, human, iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
        {
            if (key != "agent-runs.read" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
}
