using AgentCore.Domain.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareWorkspaceReadAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            if (TenantAttachmentVirtualReadContract.IsPath(TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson).Path))
            {
                var (sourceRun, _) = await operations.ResolveForDecisionAsync(reference, "attachments.read", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
                if (sourceRun.Status != AgentRunStatus.Running || sourceRun.Claim?.Generation != generation || sourceRun.Revision != expectedRunRevision || sourceRun.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                    throw new AccessDeniedException("tenant_runtime_unavailable");
                var arguments = await (attachmentReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).VirtualArgumentsAsync(new(sourceRun.Owner, sourceRun.SessionId, sourceRun.AgentRunId, generation), call, ct).ConfigureAwait(false);
                return await PrepareAttachmentReadAsync(reference, arguments, expectedRunRevision, generation, ct).ConfigureAwait(false);
            }
            var permission = call.Name == AgentCore.Application.Tools.ToolCatalog.KnowledgeRetrieve || AgentCore.Application.Tools.TenantWorkspaceReadContract.IsResourcePath(AgentCore.Application.Tools.TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson).Path) ? "resources.read" : "workspaces.read";
            var (run, _) = await operations.ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                throw new AccessDeniedException("tenant_runtime_unavailable");
            var prepared = await (workspaceReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareInspectionAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), call, ct).ConfigureAwait(false);
            if (prepared.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var op = TenantApprovalRules.PrepareWorkspaceRead(ids.NewId(), run.AgentInstanceId, prepared.Read, prepared.Agent.Instance.Revision, prepared.Agent.Instance.PersonaRevision);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.ExecutionContext!.RootActivationId, prepared.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(prepared.State, run, prior, ct).ConfigureAwait(false);
            if (!prepared.ReviewRequired) return await PreparationOutcome(prepared.State, run, op, OperationDecisionKind.Allowed, "allowed", null, ct).ConfigureAwait(false);
            var qualified = false;
            foreach (var human in prepared.State.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantWorkspaceReadOperationRules.ReviewerAsync(prepared.State, prepared.Agent.Instance.InstanceId, prepared.Read, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(prepared.State, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, prepared.State, prepared.Agent, op, prepared.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            var path = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson).Path;
            await operations.RecordFailureAsync(reference, TenantAttachmentVirtualReadContract.IsPath(path) ? "attachments.read" : call.Name == ToolCatalog.KnowledgeRetrieve || TenantWorkspaceReadContract.IsResourcePath(path) ? "resources.read" : "workspaces.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
