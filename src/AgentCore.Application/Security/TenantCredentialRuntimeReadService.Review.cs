using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantCredentialPreparedRead(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    PreparedCredentialRead Read, TenantApprovalSnapshot Snapshot, bool ReviewRequired);

public sealed partial class TenantCredentialRuntimeReadService
{
    public async ValueTask<TenantCredentialPreparedRead> PrepareInspectionAsync(TenantRuntimeFrame frame, string arguments, CancellationToken ct = default)
    {
        var canonical = TenantApprovalRules.CredentialReadArguments(arguments);
        using var doc = JsonDocument.Parse(canonical); var input = CredentialDiscovery.Parse(doc.RootElement);
        var before = await RequireAsync(frame, ct, approved: true).ConfigureAwait(false);
        var references = new List<CredentialReadReference>();
        foreach (var bound in before.Bindings.OrderBy(b => b.Binding.Reference, StringComparer.Ordinal))
        {
            if (!Eligible(before, bound) || input.Cursor is not null && string.CompareOrdinal(bound.Binding.Reference, input.Cursor) <= 0) continue;
            if (!(await authorizer.CheckTenantAsync(before.State, before.Agent.PrincipalId, PermissionCatalog.Get("credentials.read"),
                TenantCredentialRules.Scope(before.State.Tenant.TenantId, bound.Credential.Value.CredentialId), ct).ConfigureAwait(false)).Allowed) continue;
            references.Add(TenantCredentialReadOperationRules.Reference(bound)); if (references.Count > input.Limit) break;
        }
        var value = new PreparedCredentialRead(frame.SessionId, references.FirstOrDefault()?.CredentialId, canonical, JsonSerializer.Serialize(references));
        var review = false;
        foreach (var (permission, scope) in TenantCredentialReadOperationRules.Targets(before.State.Tenant.TenantId, value))
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(before.State, before.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                permission, scope, authorizer, ct).ConfigureAwait(false);
        var after = await RequireAsync(frame, ct, approved: true).ConfigureAwait(false);
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision || after.Session.Snapshot.Revision != before.Session.Snapshot.Revision)
            throw new SecurityConflictException("credential_source_changed");
        await RequireBoundAsync(after, value, ct).ConfigureAwait(false); await FenceAsync(frame, after, ct).ConfigureAwait(false);
        return new(before.Run, before.State, before.Agent, value, new(before.Agent.Instance.Persona.Name, CredentialRead: new(references.Count)), review);
    }
    private async ValueTask RequireBoundAsync(Read current, PreparedCredentialRead value, CancellationToken ct)
    {
        foreach (var reference in TenantApprovalRules.CredentialReadReferences(value))
        {
            var bound = current.Bindings.SingleOrDefault(b => b.Binding.BindingId == reference.BindingId);
            if (bound is null || TenantCredentialReadOperationRules.Reference(bound) != reference || !Eligible(current, bound))
                throw new SecurityConflictException("credential_source_changed");
        }
        await TenantCredentialReadOperationRules.PermissionsAsync(current.State, value, current.Agent.PrincipalId, authorizer, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var current = await RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), ct, approved: true, live: false).ConfigureAwait(false);
        if (current.Run.Revision != run.Revision || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new SecurityConflictException("operation_witness_changed");
        await RequireBoundAsync(current, request.Operation.CredentialRead!, ct).ConfigureAwait(false);
    }
    public ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantCredentialReadOperationRules.PermissionsAsync(state, request.Operation.CredentialRead!, human, authorizer, ct);
    public ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantCredentialReadOperationRules.ReviewerAsync(state, request.Operation.CredentialRead!, request.OriginalInitiatorPrincipalId, human, authorizer, ct);
    public ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct) =>
        TenantCredentialReadOperationRules.IndependentAsync(state, request.Operation.CredentialRead!, request.OriginalInitiatorPrincipalId, authorizer, ct);

    private async ValueTask RequireApprovedDataAsync(Read current, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct)
    {
        if (approvals is null || effects is null || request.Operation.CredentialRead is not { } value || request.Kind != ApprovalKind.Review
            || request.Status != TenantApprovalStatus.Approved || request.SessionId != current.Run.SessionId || request.TenantId != current.Run.Owner.TenantId
            || request.ExecutorPrincipalId != current.Run.Owner.ExecutorPrincipalId || request.Operation.InstanceId != current.Run.AgentInstanceId
            || request.RootActivationId != current.Run.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != current.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantCredentialReadOperationRules.Reason
            || receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.ActionHash != request.Operation.ActionHash || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != request.SessionId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId
            || await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effects.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt
            || await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) is not null)
            throw new AccessDeniedException("credential_source_changed");
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("credential_source_changed");
        await RequireBoundAsync(current, value, ct).ConfigureAwait(false);
        await RequireInspectionReviewerAsync(request, current.State, decision.HumanPrincipalId, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt,
        int remainingBytes, CancellationToken ct = default)
    {
        var before = await RequireAsync(frame, ct, approved: true).ConfigureAwait(false);
        await RequireApprovedDataAsync(before, request, receipt, ct).ConfigureAwait(false);
        await RequireInspectionAsync(request, before.Run, ct).ConfigureAwait(false);
        if (receipt.RunId != frame.RunId || before.Run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded
            || before.Run.SideEffect.ToolCallId != "operation:" + receipt.OperationId.ToString("D") || before.Run.SideEffect.ActionHash != receipt.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(before.Run, request, requirePending: true);
        var after = await RequireAsync(frame, ct, approved: true).ConfigureAwait(false);
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision
            || after.Session.Snapshot.Revision != before.Session.Snapshot.Revision) throw new SecurityConflictException("credential_source_changed");
        await RequireApprovedDataAsync(after, request, receipt, ct).ConfigureAwait(false); await FenceAsync(frame, after, ct).ConfigureAwait(false);
        var selected = TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead!).Select(r =>
            after.Bindings.Single(b => b.Binding.BindingId == r.BindingId)).ToArray();
        using var doc = JsonDocument.Parse(request.Operation.CredentialRead!.ArgumentsJson);
        return Serialize(selected, CredentialDiscovery.Parse(doc.RootElement), remainingBytes);
    }
    private async ValueTask RequireStoredApprovedAsync(AgentRun run, IReadOnlyList<ModelMessage> messages, CancellationToken ct)
    {
        Read? current = null;
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("credentialMetadata", out var metadata) || metadata.ValueKind == JsonValueKind.Null) continue;
            if (approvals is null || effects is null || !root.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var requestId)
                || !root.TryGetProperty("operationId", out var op) || !op.TryGetGuid(out var operationId)
                || !metadata.TryGetProperty("tenantCredentialRead", out var marker) || marker.ValueKind != JsonValueKind.True)
                throw new AccessDeniedException("credential_source_changed");
            var request = await approvals.FindApprovalAsync(run.Owner.TenantId!.Value, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.FindOperationReceiptAsync(request.TenantId, operationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_source_changed");
            current ??= await RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), ct, approved: true).ConfigureAwait(false);
            await RequireApprovedDataAsync(current, request, receipt, ct).ConfigureAwait(false);
            var witnesses = metadata.GetProperty("credentialReads").Deserialize<Witness[]>() ?? throw new AccessDeniedException("credential_source_changed");
            if (witnesses.Length > 100) throw new AccessDeniedException("credential_source_changed");
            var saved = TenantApprovalRules.CredentialReadReferences(request.Operation.CredentialRead!);
            foreach (var witness in witnesses)
                if (!saved.Any(r => r.BindingId == witness.BindingId && r.CredentialId == witness.CredentialId
                    && r.BindingRevision == witness.BindingRevision && r.CredentialRevision == witness.CredentialRevision))
                    throw new AccessDeniedException("credential_source_changed");
        }
        if (current is not null) await FenceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), current, ct).ConfigureAwait(false);
    }
}
