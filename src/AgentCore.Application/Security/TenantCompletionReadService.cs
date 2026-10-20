using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantPreparedCompletionRead(AgentRun Run, TenantSecurityState State, TenantAgent Agent,
    PreparedCompletionRead Read, TenantApprovalSnapshot Snapshot, bool ReviewRequired);

// Existing approval/read-receipt owners supply authority; this service owns no persisted state.
public sealed class TenantCompletionReadService(TenantOperationAuthorizer operations, ITenantRunStore runs,
    ITenantSessionStore sessions, ITenantArtifactStore artifacts, TenantRunConfigurationResolver configurations,
    IIamStore security, IamAuthorizer iam, TimeProvider time, ITenantApprovalStore approvals, ITenantApprovedOperationStore effects)
{
    private sealed record Read(AgentRun Run, TenantSecurityState State, TenantAgent Agent, AgentDefinition Definition,
        BackgroundCompletionCandidate Child, PreparedCompletionRead Witness);
    public static Guid Parse(string arguments)
    {
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("backgroundSessionId", out var id)
            || !id.TryGetGuid(out var session) || session == Guid.Empty) throw new ArgumentException("Provide one exact backgroundSessionId.");
        return session;
    }
    private async ValueTask<Read> RequireAsync(TenantRuntimeFrame frame, Guid childSession, CancellationToken ct, bool live = true)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (parent, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var own = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("session_unavailable");
        if (live) TenantRuntimeRules.Require(frame, own.Owner, parent, time.GetUtcNow());
        if (parent.Owner != frame.Owner || parent.SessionId != frame.SessionId || own.Owner.AgentInstanceId != parent.AgentInstanceId
            || own.Owner.ExecutorPrincipalId != parent.Owner.ExecutorPrincipalId) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        TenantWaitRules.RequireSession(own);
        var current = await configurations.ResolveAsync(tenant, parent.AgentInstanceId, ct).ConfigureAwait(false);
        var child = await TenantWaitRules.ChildAsync(parent, await sessions.FindTenantSessionAsync(tenant, childSession, ct).ConfigureAwait(false),
            (id, token) => runs.FindTenantRunAsync(tenant, id, token), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        var source = child.CurrentTaskRun ?? child.Run;
        if (!source.IsTerminal || source.Result?.OutcomeKind == AgentRunOutcomeKind.Continued) throw new AccessDeniedException("completion_not_ready");
        var files = new List<ArtifactRecord>(); Guid? cursor = null;
        do
        {
            var page = await artifacts.ListTenantAsync(tenant, childSession, null, cursor, 20, source.AgentRunId, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
                if (item.TenantId == tenant && item.SessionId == childSession && item.AgentRunId == source.AgentRunId
                    && (await iam.CheckOwnedTenantAsync(state, parent.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("artifacts.read"), TenantArtifactRules.Scope(tenant, item.ArtifactId), ct).ConfigureAwait(false)).Allowed)
                { files.Add(item); if (files.Count == 3) break; }
            cursor = page.HasMore && files.Count < 3 ? page.NextCursor : null;
        } while (cursor is not null);
        var witness = TenantCompletionReadRules.Witness(parent, child, files);
        TenantCompletionReadRules.Require(witness, parent, current.Agent, current.Configuration.Configuration.Definition, child);
        await TenantCompletionReadRules.PermissionsAsync(state, witness, parent.Owner.ExecutorPrincipalId!.Value, iam, ct).ConfigureAwait(false);
        return new(parent, state, current.Agent, current.Configuration.Configuration.Definition, child, witness);
    }
    private async ValueTask FenceAsync(TenantRuntimeFrame frame, Read before, CancellationToken ct, bool live = true)
    {
        var after = await RequireAsync(frame, before.Witness.ChildSessionId, ct, live).ConfigureAwait(false);
        if (after.Run.Revision != before.Run.Revision || after.State.Tenant.Revision != before.State.Tenant.Revision
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision || !TenantCompletionReadRules.Same(after.Witness, before.Witness)
            || (await security.ReadTenantAsync(after.State.Tenant.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != after.State.Tenant.Revision)
            throw new SecurityConflictException("completion_source_changed");
    }
    public async ValueTask<TenantPreparedCompletionRead> PrepareInspectionAsync(TenantRuntimeFrame frame, string arguments, CancellationToken ct = default)
    {
        var before = await RequireAsync(frame, Parse(arguments), ct).ConfigureAwait(false);
        var review = await TenantCompletionReadRules.NeedsReviewAsync(before.State, before.Witness, before.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, iam, ct).ConfigureAwait(false);
        await FenceAsync(frame, before, ct).ConfigureAwait(false);
        return new(before.Run, before.State, before.Agent, before.Witness, new(before.Agent.Instance.Persona.Name), review);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.CompletionRead ?? throw new AccessDeniedException("operation_mismatch");
        var current = await RequireAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), value.ChildSessionId, ct, false).ConfigureAwait(false);
        if (current.Run.Revision != run.Revision || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || !TenantCompletionReadRules.Same(value, current.Witness))
            throw new SecurityConflictException("completion_source_changed");
    }
    public async ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        var value = request.Operation.CompletionRead!;
        await TenantCompletionReadRules.PermissionsAsync(state, value, human, iam, ct).ConfigureAwait(false);
        var parent = await runs.FindTenantRunAsync(request.TenantId, request.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var child = await TenantWaitRules.ChildAsync(parent, await sessions.FindTenantSessionAsync(request.TenantId, value.ChildSessionId, ct).ConfigureAwait(false),
            (id, token) => runs.FindTenantRunAsync(request.TenantId, id, token), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var files = new List<ArtifactRecord>();
        foreach (var expected in TenantCompletionReadRules.Artifacts(value))
        {
            var item = await artifacts.FindTenantAsync(request.TenantId, value.ChildSessionId, expected.ArtifactId, ct).ConfigureAwait(false);
            if (item is null || item.AgentRunId != value.SourceRunId || TenantArtifactVerificationRules.Witness(item) != expected)
                throw new AccessDeniedException("approval_unavailable");
            files.Add(item);
        }
        if (!TenantCompletionReadRules.Same(value, TenantCompletionReadRules.Witness(parent, child, files))) throw new AccessDeniedException("approval_unavailable");
    }
    public ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct) =>
        TenantCompletionReadRules.ReviewerAsync(state, request.Operation.CompletionRead!, request.OriginalInitiatorPrincipalId, human, iam, ct);
    public ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct) =>
        TenantCompletionReadRules.IndependentAsync(state, request.Operation.CompletionRead!, request.OriginalInitiatorPrincipalId, iam, ct);

    public static async ValueTask RequireApprovedAsync(AgentRun parent, TenantSecurityState state, PreparedCompletionRead current,
        TenantApprovalRequest request, TenantOperationReceipt receipt, TenantApprovalDecision decision, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Operation.CompletionRead is not { } value || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
            || request.TenantId != parent.Owner.TenantId || request.SessionId != parent.SessionId || request.ExecutorPrincipalId != parent.Owner.ExecutorPrincipalId
            || request.Operation.InstanceId != parent.AgentInstanceId || request.RootActivationId != parent.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != parent.Admission.ExecutionContext.OriginalInitiatorPrincipalId || !TenantCompletionReadRules.Same(value, current)
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantCompletionReadRules.Reason || receipt.RequestId != request.RequestId
            || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash || receipt.TenantId != request.TenantId
            || receipt.InstanceId != parent.AgentInstanceId || receipt.SessionId != parent.SessionId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.RootActivationId != request.RootActivationId || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId
            || decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash)
            throw new AccessDeniedException("completion_source_changed");
        await TenantCompletionReadRules.PermissionsAsync(state, current, parent.Owner.ExecutorPrincipalId!.Value, iam, ct).ConfigureAwait(false);
        await TenantCompletionReadRules.ReviewerAsync(state, current, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt,
        int remainingBytes, CancellationToken ct = default)
    {
        var before = await RequireAsync(frame, request.Operation.CompletionRead!.ChildSessionId, ct).ConfigureAwait(false);
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effects.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt
            || await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) is not null)
            throw new AccessDeniedException("completion_source_changed");
        await RequireApprovedAsync(before.Run, before.State, before.Witness, request, receipt, decision, iam, ct).ConfigureAwait(false);
        if (receipt.RunId != frame.RunId || before.Run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded
            || before.Run.SideEffect.ToolCallId != "operation:" + receipt.OperationId.ToString("D") || before.Run.SideEffect.ActionHash != receipt.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(before.Run, request, requirePending: true);
        await FenceAsync(frame, before, ct).ConfigureAwait(false);
        var json = System.Text.Json.Nodes.JsonNode.Parse(before.Witness.ObservationJson)!;
        json["readRequestId"] = request.RequestId.ToString("D");
        return System.Text.Encoding.UTF8.GetByteCount(json.ToJsonString()) <= remainingBytes ? json.ToJsonString() : throw new AccessDeniedException("result_budget_exhausted");
    }
    public async ValueTask<PreparedCompletionRead> RequireStoredSourceAsync(AgentRun parent, Guid session, Guid source, Guid requestId, CancellationToken ct)
    {
        if (!TenantCompletionReadReferences.Contains(parent, session, source, requestId)) throw new AccessDeniedException("completion_source_changed");
        var tenant = parent.Owner.TenantId!.Value;
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var receipt = await effects.FindOperationReceiptAsync(tenant, request.Operation.OperationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_source_changed");
        var decision = await effects.FindApprovalDecisionAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (request.Operation.CompletionRead?.SourceRunId != source || request.Operation.CompletionRead.ChildSessionId != session
            || await approvals.FindOperationGrantAsync(tenant, requestId, ct).ConfigureAwait(false) is not null) throw new AccessDeniedException("completion_source_changed");
        var before = await RequireAsync(new(parent.Owner, parent.SessionId, parent.AgentRunId, parent.Claim?.Generation ?? Guid.Empty), session, ct, false).ConfigureAwait(false);
        if (before.Run.Revision != parent.Revision || before.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || before.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new SecurityConflictException("completion_source_changed");
        await RequireApprovedAsync(parent, before.State, before.Witness, request, receipt, decision, iam, ct).ConfigureAwait(false);
        await FenceAsync(new(parent.Owner, parent.SessionId, parent.AgentRunId, parent.Claim?.Generation ?? Guid.Empty), before, ct, false).ConfigureAwait(false);
        return before.Witness;
    }

}
