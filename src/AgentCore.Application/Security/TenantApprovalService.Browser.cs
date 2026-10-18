using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareBrowserAsync(TenantRunReference reference, string tool, string argumentsJson,
        long expectedRunRevision, Guid generation, CancellationToken ct = default, bool registerCold = false)
    {
        var permission = "browser.use";
        try
        {
            var (run, state) = await operations.ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                throw new AccessDeniedException("tenant_runtime_unavailable");
            var intent = TenantApprovalRules.BrowserIntent(run.SessionId, tool, argumentsJson);
            permission = intent.Command is AgentCore.Application.Ports.BrowserFillCredential ? "credentials.use" : intent.ReadOnly ? "browser.read" : "browser.use";
            var prepared = await (browserTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(run, tool, argumentsJson, ct, registerCold).ConfigureAwait(false);
            var value = prepared.Command;
            var operation = TenantApprovalRules.PrepareBrowser(ids.NewId(), run.AgentInstanceId, value, prepared.Agent.Instance.Revision, prepared.Agent.Instance.PersonaRevision);
            var review = await TenantBrowserOperationRules.NeedsReviewAsync(state, run.AgentInstanceId, value,
                run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, authorizer, ct).ConfigureAwait(false);
            var kind = value.Credential is { } credential && !(await authorizer.CheckTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value,
                PermissionCatalog.Get("credentials.use"), TenantCredentialRules.Scope(reference.TenantId, credential.CredentialId), ct).ConfigureAwait(false)).Allowed
                ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review;
            if (review)
            {
                var qualified = false;
                foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
                {
                    try { await TenantBrowserOperationRules.RequireReviewerAsync(state, run.AgentInstanceId, value, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                        human.PrincipalId, authorizer, ct, kind).ConfigureAwait(false); qualified = true; break; }
                    catch (AccessDeniedException) { }
                }
                if (!qualified) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            }
            return await PersistAsync(reference, run, state, prepared.Agent, operation, TenantBrowserOperationRules.Snapshot(prepared.Agent, value), review,
                expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, permission, e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
}
