using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantArtifactPreparedVerification(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    PreparedArtifactVerification Verification, TenantApprovalSnapshot Snapshot, bool ReviewRequired);

public sealed partial class TenantArtifactRuntimeReadService
{
    public async ValueTask<TenantArtifactPreparedVerification> PrepareInspectionAsync(TenantRuntimeFrame frame, Guid artifact, CancellationToken ct = default)
    {
        var before = await RequireAsync(frame, artifact, ct, approved: true).ConfigureAwait(false);
        var value = Witness(before.Artifact); var review = false;
        foreach (var (permission, scope) in Targets(before.State.Tenant.TenantId, value))
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(before.State, before.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false);
        await FenceAsync(frame, before, ct, approved: true).ConfigureAwait(false);
        return new(before.Run, before.State, before.Agent, value, new(before.Agent.Instance.Persona.Name), review);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.ArtifactVerification ?? throw new AccessDeniedException("operation_mismatch");
        var read = await RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), value.ArtifactId, ct, approved: true, live: false).ConfigureAwait(false);
        if (read.Run.Revision != run.Revision || read.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || read.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || Witness(read.Artifact) != value)
            throw new SecurityConflictException("artifact_source_changed");
    }
    public async ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        var value = request.Operation.ArtifactVerification!;
        await PermissionsAsync(state, value, human, ct).ConfigureAwait(false);
        var actual = await artifacts.FindTenantAsync(request.TenantId, request.SessionId, value.ArtifactId, ct).ConfigureAwait(false);
        if (actual is null || actual.TenantId != request.TenantId || Witness(actual) != value) throw new AccessDeniedException("approval_unavailable");
    }
    public ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantArtifactVerificationRules.ReviewerAsync(state, request.Operation.ArtifactVerification!, request.OriginalInitiatorPrincipalId, human, iam, ct);
    public ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct) =>
        TenantArtifactVerificationRules.IndependentAsync(state, request.Operation.ArtifactVerification!, request.OriginalInitiatorPrincipalId, iam, ct);

    private async ValueTask RequireApprovedDataAsync(Read current, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct)
    {
        if (effects is null || request.Operation.ArtifactVerification is not { } value || request.Kind != ApprovalKind.Review
            || request.Status != TenantApprovalStatus.Approved || request.SessionId != current.Run.SessionId || request.TenantId != current.Run.Owner.TenantId
            || request.ExecutorPrincipalId != current.Run.Owner.ExecutorPrincipalId || request.Operation.InstanceId != current.Run.AgentInstanceId
            || request.RootActivationId != current.Run.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != current.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantArtifactVerificationRules.Reason
            || receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.ActionHash != request.Operation.ActionHash || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != request.SessionId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId || Witness(current.Artifact) != value
            || await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effects.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt
            || await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) is not null)
            throw new AccessDeniedException("artifact_source_changed");
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("artifact_source_changed");
        await RequireInspectionReviewerAsync(request, current.State, decision.HumanPrincipalId, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int remainingBytes, CancellationToken ct = default)
    {
        var before = await RequireAsync(frame, request.Operation.ArtifactVerification!.ArtifactId, ct, approved: true).ConfigureAwait(false);
        await RequireApprovedDataAsync(before, request, receipt, ct).ConfigureAwait(false);
        await RequireInspectionAsync(request, before.Run, ct).ConfigureAwait(false);
        if (receipt.RunId != frame.RunId || before.Run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded
            || before.Run.SideEffect.ToolCallId != "operation:" + receipt.OperationId.ToString("D") || before.Run.SideEffect.ActionHash != receipt.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(before.Run, request, requirePending: true);
        await FenceAsync(frame, before, ct, approved: true).ConfigureAwait(false);
        return Serialize(before.Artifact, remainingBytes);
    }
    private async ValueTask RequireStoredApprovedAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct)
    {
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty);
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("artifactMetadata", out var metadata) || metadata.ValueKind == JsonValueKind.Null) continue;
            if (effects is null || !root.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var requestId)
                || !root.TryGetProperty("operationId", out var op) || !op.TryGetGuid(out var operationId)
                || !metadata.TryGetProperty("tenantArtifactRead", out var marker) || marker.ValueKind != JsonValueKind.True)
                throw new AccessDeniedException("artifact_source_changed");
            var request = await approvals.FindApprovalAsync(run.Owner.TenantId!.Value, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.FindOperationReceiptAsync(request.TenantId, operationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("artifact_source_changed");
            var witness = metadata.GetProperty("artifactRead").Deserialize<PreparedArtifactVerification>() ?? throw new AccessDeniedException("artifact_source_changed");
            if (request.Operation.ArtifactVerification != witness) throw new AccessDeniedException("artifact_source_changed");
            var current = await RequireAsync(frame, witness.ArtifactId, ct, approved: true).ConfigureAwait(false);
            await RequireApprovedDataAsync(current, request, receipt, ct).ConfigureAwait(false);
            await FenceAsync(frame, current, ct, approved: true).ConfigureAwait(false);
        }
    }
}
