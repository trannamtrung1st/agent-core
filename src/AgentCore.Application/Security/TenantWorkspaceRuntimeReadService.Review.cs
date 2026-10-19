using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantWorkspacePreparedRead(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    PreparedWorkspaceRead Read, TenantApprovalSnapshot Snapshot, bool ReviewRequired);

public sealed partial class TenantWorkspaceRuntimeReadService
{
    private static PreparedWorkspaceRead Witness(Guid session, ModelToolCall call, string hash, string? resourceKey = null, long? resourceRevision = null)
    {
        var args = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson);
        return new(session, args.Path.StartsWith("/working", StringComparison.Ordinal), call.Name, TenantWorkspaceReadContract.Canonical(call.Name, args), hash, resourceKey, resourceRevision);
    }
    private static ModelToolCall Call(PreparedWorkspaceRead value) => new("workspace-inspection", value.ToolName, value.ArgumentsJson);
    public async ValueTask<TenantWorkspacePreparedRead> PrepareInspectionAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct = default)
    {
        var observed = await ObserveExact(frame, call, ct).ConfigureAwait(false); var before = observed.Before;
        var value = Witness(frame.SessionId, call, observed.Hash, observed.Before.Resource?.Resource.Key, observed.Before.Resource?.ResourceRevision); var review = false;
        foreach (var (key, scope) in TenantWorkspaceReadOperationRules.Targets(before.Workspace.TenantId, before.Agent.Instance.InstanceId, value))
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(before.State, before.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false);
        await Fence(frame, call.Name, TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson), before, ct, approved: true).ConfigureAwait(false);
        return new(before.Run, before.State, before.Agent, value, new(before.Agent.Instance.Persona.Name), review);
    }
    private async ValueTask<(Read Before, string Output, string Hash)> ObserveExact(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct, bool live = true, int budget = ToolLimits.MaxOutputBytes)
    {
        try
        {
            var observed = await InspectCore(frame, call, budget, ct, approved: true, live: live).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(observed.Output);
            if (!doc.RootElement.TryGetProperty("tenantWorkspaceRead", out var marker) || marker.ValueKind != JsonValueKind.True)
                throw new AccessDeniedException("workspace_read_unavailable");
            return observed;
        }
        catch (Exception e) when (e is Sessions.AgentCoreException or IOException or UnauthorizedAccessException) { throw new AccessDeniedException("workspace_source_changed"); }
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.WorkspaceRead ?? throw new AccessDeniedException("operation_mismatch");
        var observed = await ObserveExact(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), Call(value), ct, live: false).ConfigureAwait(false);
        if (observed.Before.Run.Revision != run.Revision || observed.Before.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || observed.Before.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || Witness(run.SessionId, Call(value), observed.Hash, observed.Before.Resource?.Resource.Key, observed.Before.Resource?.ResourceRevision) != value)
            throw new SecurityConflictException("workspace_source_changed");
    }
    public async ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        await TenantWorkspaceReadOperationRules.PermissionsAsync(state, request.Operation.InstanceId, request.Operation.WorkspaceRead!, human, iam, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (session.Owner.AgentInstanceId != request.Operation.InstanceId || session.Owner.ExecutorPrincipalId != request.ExecutorPrincipalId) throw new AccessDeniedException("approval_unavailable");
    }
    public ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantWorkspaceReadOperationRules.ReviewerAsync(state, request.Operation.InstanceId, request.Operation.WorkspaceRead!, request.OriginalInitiatorPrincipalId, human, iam, ct);
    public ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct) =>
        TenantWorkspaceReadOperationRules.IndependentAsync(state, request.Operation.InstanceId, request.Operation.WorkspaceRead!, request.OriginalInitiatorPrincipalId, iam, ct);
    private async ValueTask RequireApprovedData(Read current, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct)
    {
        if (request.Operation.WorkspaceRead is null || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
            || request.SessionId != current.Run.SessionId || request.TenantId != current.Run.Owner.TenantId || request.ExecutorPrincipalId != current.Run.Owner.ExecutorPrincipalId
            || request.Operation.InstanceId != current.Run.AgentInstanceId || request.RootActivationId != current.Run.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != current.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantWorkspaceReadOperationRules.Reason
            || receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.ActionHash != request.Operation.ActionHash || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != request.SessionId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId
            || await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effects.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt
            || await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) is not null) throw new AccessDeniedException("workspace_source_changed");
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash) throw new AccessDeniedException("workspace_source_changed");
        await RequireInspectionReviewerAsync(request, current.State, decision.HumanPrincipalId, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int remainingBytes, CancellationToken ct = default)
    {
        var value = request.Operation.WorkspaceRead ?? throw new AccessDeniedException("operation_mismatch");
        var observed = await ObserveExact(frame, Call(value), ct, budget: remainingBytes).ConfigureAwait(false);
        await RequireApprovedData(observed.Before, request, receipt, ct).ConfigureAwait(false);
        if (Witness(frame.SessionId, Call(value), observed.Hash, observed.Before.Resource?.Resource.Key, observed.Before.Resource?.ResourceRevision) != value || observed.Before.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || observed.Before.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new SecurityConflictException("workspace_source_changed");
        if (receipt.RunId != frame.RunId || observed.Before.Run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded
            || observed.Before.Run.SideEffect.ToolCallId != "operation:" + receipt.OperationId.ToString("D") || observed.Before.Run.SideEffect.ActionHash != receipt.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(observed.Before.Run, request, requirePending: true);
        await Fence(frame, value.ToolName, TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson), observed.Before, ct, approved: true).ConfigureAwait(false);
        return observed.Output;
    }
    private async ValueTask RequireStoredApprovedAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct)
    {
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty);
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("workspaceObservation", out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (!root.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var requestId) || !root.TryGetProperty("operationId", out var op) || !op.TryGetGuid(out var operationId)
                || !data.TryGetProperty("tenantWorkspaceRead", out var marker) || marker.ValueKind != JsonValueKind.True) throw new AccessDeniedException("workspace_source_changed");
            var request = await approvals.FindApprovalAsync(run.Owner.TenantId!.Value, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.FindOperationReceiptAsync(request.TenantId, operationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("workspace_source_changed");
            var value = request.Operation.WorkspaceRead ?? throw new AccessDeniedException("operation_mismatch");
            var witness = data.GetProperty("workspaceRead");
            if (witness.GetProperty("tool").GetString() != value.ToolName || witness.GetProperty("arguments").Deserialize<TenantWorkspaceReadArguments>() != TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson))
                throw new AccessDeniedException("workspace_source_changed");
            var observed = await ObserveExact(frame, Call(value), ct).ConfigureAwait(false);
            if (Witness(run.SessionId, Call(value), observed.Hash, observed.Before.Resource?.Resource.Key, observed.Before.Resource?.ResourceRevision) != value) throw new AccessDeniedException("workspace_source_changed");
            await RequireApprovedData(observed.Before, request, receipt, ct).ConfigureAwait(false);
            await Fence(frame, value.ToolName, TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson), observed.Before, ct, approved: true).ConfigureAwait(false);
        }
    }
}
