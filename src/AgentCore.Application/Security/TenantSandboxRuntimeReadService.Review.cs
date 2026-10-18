using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxPreparedRead(AgentRun Run, TenantSecurityState Security, TenantAgent Agent, TenantSessionRoot Owner,
    PreparedSandboxRead Read, TenantApprovalSnapshot Snapshot, bool ReviewRequired, bool IndependentReview);

public sealed partial class TenantSandboxRuntimeReadService
{
    private sealed record ApprovedRead(TenantApprovalRequest Request, Guid Human);
    public async ValueTask<TenantSandboxPreparedRead> PrepareInspectionAsync(TenantRuntimeFrame frame, SandboxReadKind kind, Guid? executionId, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind) || kind == SandboxReadKind.Process != (executionId is not null) || executionId == Guid.Empty)
            throw new ArgumentException("Use the exact stored inspection intent.");
        var read = await ReadContext(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), TenantSandboxReadOperationRules.Tool(kind), ct).ConfigureAwait(false);
        TenantRuntimeRules.Require(frame, read.Session.Owner, read.Run, (time ?? TimeProvider.System).GetUtcNow());
        var snapshot = await storage.ReadTenantSandboxRuntimeAsync(read.Security.Tenant.TenantId, frame.SessionId, executionId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (executionId is not null && snapshot.Execution is null) throw new AccessDeniedException("resource_unavailable");
        var refs = new List<SandboxReadProfileReference>();
        async ValueTask Add(string id, long revision, bool required)
        {
            if (!(await authorizer.CheckTenantAsync(read.Security, read.Agent.PrincipalId, PermissionCatalog.Get("sandbox.read"),
                TenantSandboxProfileRules.Scope(read.Security.Tenant.TenantId, id), ct).ConfigureAwait(false)).Allowed)
            { if (required) throw new AccessDeniedException("permission_denied"); return; }
            refs.Add(new(id, revision));
        }
        if (kind == SandboxReadKind.Profiles)
        {
            var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(read.Baseline, read.Agent.Instance.SettingsOverrides);
            foreach (var p in (read.Run.Admission.Configuration!.Definition.ExecutionSandboxProfiles ?? []).Take(16))
                if (read.Run.Admission.Configuration.Definition.SandboxPolicy?.AllowedProfileIds.Contains(p.Id, StringComparer.Ordinal) == true
                    && current.SandboxPolicy?.AllowedProfileIds.Contains(p.Id, StringComparer.Ordinal) == true) await Add(p.Id, p.Revision, false).ConfigureAwait(false);
        }
        else
        {
            if (snapshot.Lease is { } lease) await Add(lease.Profile.Id, lease.Profile.Revision, true).ConfigureAwait(false);
            if (snapshot.Execution is { ProfileId: { } id, ProfileRevision: { } revision }) await Add(id, revision, true).ConfigureAwait(false);
            if (snapshot.Lease is { SelectionWitness: not null, TransitionProfileId: { } target, TransitionProfileRevision: { } selectedRevision })
                await Add(target, selectedRevision, false).ConfigureAwait(false);
        }
        refs = refs.Distinct().OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Revision).ToList();
        var value = new PreparedSandboxRead(kind, frame.SessionId, kind == SandboxReadKind.Profiles ? null : snapshot.Execution?.Id,
            snapshot.Lease?.Generation, JsonSerializer.Serialize(refs), TenantSandboxReadOperationRules.TargetHash(kind, read.Session.Owner, snapshot));
        var sources = await ReadSources(read.Security.Tenant.TenantId, value, ct).ConfigureAwait(false);
        TenantSandboxReadOperationRules.Require(value, read.Run, read.Agent, read.Baseline, read.Session, snapshot, sources);
        var review = false;
        foreach (var (permission, scope) in ReadTargets(read.Session.Owner, refs.Select(p => p.Id)))
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(read.Security, read.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, authorizer, ct).ConfigureAwait(false);
        var independent = review && await TenantSandboxReadOperationRules.IndependentAsync(read.Security, read.Session.Owner, value,
            read.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, authorizer, ct).ConfigureAwait(false);
        await FenceModel(frame, read, ct, value).ConfigureAwait(false);
        return new(read.Run, read.Security, read.Agent, read.Session.Owner, value, new(read.Agent.Instance.Persona.Name, SandboxRead: new(TenantSandboxReadOperationRules.Summary(kind),
            JsonSerializer.Serialize(sources.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Revision).Select(p => new SandboxReadProfileLabel(p.Id, p.Revision, p.Name))), kind == SandboxReadKind.Process)), review, independent);
    }
    private async ValueTask<IReadOnlyList<SandboxProfile>> ReadSources(Guid tenant, PreparedSandboxRead value, CancellationToken ct)
    {
        var sources = new List<SandboxProfile>();
        foreach (var reference in TenantApprovalRules.ReadProfiles(value))
            sources.Add(await (profileStore ?? throw new AccessDeniedException("tenant_effect_unavailable")).FindTenantSandboxProfileAsync(tenant, reference.Id, reference.Revision, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable"));
        return sources;
    }
    private async ValueTask RequireBound(ModelRead read, TenantApprovalRequest request, TenantSandboxRuntimeSnapshot snapshot, CancellationToken ct)
    {
        var value = request.Operation.SandboxRead ?? throw new ArgumentException("Exact Sandbox read required.");
        if (read.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || read.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
            throw new SecurityConflictException("operation_witness_changed");
        var sources = await ReadSources(request.TenantId, value, ct).ConfigureAwait(false);
        TenantSandboxReadOperationRules.Require(value, read.Run, read.Agent, read.Baseline, read.Session, snapshot, sources);
        await TenantSandboxReadOperationRules.RequirePermissionsAsync(read.Security, read.Session.Owner, value, read.Agent.PrincipalId, authorizer, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.SandboxRead ?? throw new ArgumentException("Exact Sandbox read required.");
        var read = await ReadContext(new(request.TenantId, run.AgentRunId), TenantSandboxReadOperationRules.Tool(value.Kind), ct).ConfigureAwait(false);
        var snapshot = await storage.ReadTenantSandboxRuntimeAsync(request.TenantId, run.SessionId, value.Kind == SandboxReadKind.Process ? value.ExecutionId : null, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        await RequireBound(read, request, snapshot, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireInspectionReadAsync(TenantApprovalRequest request, TenantSecurityState state, Guid principal, CancellationToken ct = default)
    {
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireRead(state, session.Owner, principal, TenantApprovalRules.ReadProfiles(request.Operation.SandboxRead!).Select(p => p.Id), ct).ConfigureAwait(false);
    }
    public async ValueTask RequireInspectionReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct = default)
    {
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxReadOperationRules.RequireReviewerAsync(state, session.Owner, request.Operation.SandboxRead!, request.OriginalInitiatorPrincipalId, human, authorizer, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> InspectionIndependentAsync(TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct = default)
    {
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return await TenantSandboxReadOperationRules.IndependentAsync(state, session.Owner, request.Operation.SandboxRead!, request.OriginalInitiatorPrincipalId, authorizer, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt, int remainingBytes, CancellationToken ct = default)
    {
        if (operations is null || approvalStore is null || effectStore is null || request.Operation.SandboxRead is not { } value
            || frame.Owner.TenantId != request.TenantId) throw new AccessDeniedException("tenant_effect_unavailable");
        var (run, _) = await operations.ResolveAsync(new(request.TenantId, frame.RunId), ct).ConfigureAwait(false);
        if (await approvalStore.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await effectStore.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false) != receipt
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != TenantSandboxReadOperationRules.Reason
            || receipt.TenantId != request.TenantId || receipt.OperationId != request.Operation.OperationId || receipt.InstanceId != request.Operation.InstanceId
            || receipt.RootActivationId != request.RootActivationId || receipt.RunId != frame.RunId || receipt.SessionId != frame.SessionId || receipt.ExecutorPrincipalId != frame.Owner.ExecutorPrincipalId
            || receipt.ActionHash != request.Operation.ActionHash || receipt.RequestId != request.RequestId
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.Succeeded || run.SideEffect.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D")
            || run.SideEffect.ActionHash != receipt.ActionHash) throw new SecurityConflictException("operation_mismatch");
        var decision = await effectStore.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = run.AgentRunId == request.RunId ? null : await operations.FindRunAsync(new(request.TenantId, request.RunId), ct).ConfigureAwait(false);
        TenantLocalEffectRules.ValidateApproval(request, decision, null, run, new(request.RequestId, request.Operation.OperationId, run.AgentRunId, frame.Generation,
            run.Revision, request.Revision, decision.DecisionId, null, request.Operation.ActionHash, receipt.Outcome, receipt.ReasonCode), source);
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(run, request, requirePending: true);
        return await InspectCore(frame, new("approved-read", TenantSandboxReadOperationRules.Tool(value.Kind), value.Kind == SandboxReadKind.Process
            ? JsonSerializer.Serialize(new { executionId = value.ExecutionId }) : "{}"), remainingBytes, ct, new(request, decision.HumanPrincipalId)).ConfigureAwait(false);
    }
}
