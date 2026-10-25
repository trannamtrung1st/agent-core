using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareAccountActionAsync(TenantRunReference reference, TenantAccountActionIntent intent,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        var permission = intent.ToolName switch { ToolCatalog.EmailCreateDraft => "email.draft", ToolCatalog.EmailSend => "email.send",
            ToolCatalog.DriveDownload => "workspaces.write", ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch => "drive.delete", _ => "drive.write" };
        try
        {
            var (run, _) = await operations.ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Revision != expectedRunRevision || run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
            var prepared = await (accountActions ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(
                new(run.Owner, run.SessionId, run.AgentRunId, generation), intent, ct).ConfigureAwait(false);
            var source = prepared.Source;
            var operation = TenantApprovalRules.PrepareAccountAction(ids.NewId(), source.Agent.Instance.InstanceId, prepared.Action,
                source.Agent.Instance.Revision, source.Agent.Instance.PersonaRevision);
            operation = TenantApprovalRules.ForRun(operation, source.Run);
            var explicitReview = intent.ToolName is ToolCatalog.EmailSend or ToolCatalog.DriveRename or ToolCatalog.DriveMove or ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch;
            // An Allowed primary write policy cannot bypass independently protected
            // account use or source reads. One exact compound consent binds them all.
            foreach (var (key, scope) in TenantAccountActionRules.Targets(reference.TenantId, source.Run.AgentInstanceId, prepared.Action, TenantWorkspaceRules.Context(source.Run)).Distinct())
                if ((key != operation.Permission || scope != TenantApprovalRules.Scope(reference.TenantId, operation))
                    && await TenantSandboxReadOperationRules.NeedsReviewAsync(source.State, source.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                        key, scope, authorizer, ct).ConfigureAwait(false)) explicitReview = true;
            return await PersistAsync(reference, source.Run, source.State, source.Agent, operation, prepared.Snapshot, explicitReview, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, permission, e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
