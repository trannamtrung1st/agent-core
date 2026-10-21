using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantMemoryOperationStore
{
    public ValueTask<TenantMemoryOperationObservation> ObserveTenantMemoryOperationAsync(TenantRunReference reference, Guid? generation,
        TenantMemoryArguments arguments, PreparedMemoryOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("memory_context_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ValueTask.FromResult(ObserveMemoryOperation(run, arguments, expected, inspectionHuman, ct));
        }
    }
    private TenantMemoryOperationObservation ObserveMemoryOperation(AgentRun run, TenantMemoryArguments arguments, PreparedMemoryOperation? expected, Guid? inspectionHuman, CancellationToken ct)
    {
        var args = TenantMemoryToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = MemoryReader(run, ct);
        if (inspectionHuman is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        if (inspectionHuman is { } human) ValidateAgentActor(reader.Security, human);
        var readable = TenantMemoryOperationRules.ReadableAgentsAsync(reader, new IamAuthorizer(this), inspectionHuman, ct).AsTask().GetAwaiter().GetResult();
        var ids = TenantMemoryOperationRules.Keys(reader, inspectionHuman is not null).SelectMany(key => Enum.GetValues<MemoryItemStatus>()
            .SelectMany(status => learnedMemoryIndex.GetValueOrDefault((reader.Security.Tenant.TenantId, key, status)) ?? []));
        var selected = inspectionHuman is not null ? TenantMemoryOperationRules.Sources(expected!).Select(s => s.MemoryId).ToHashSet()
            : args.SourceIds is not null ? args.SourceIds.ToHashSet() : args.MemoryId is { } id ? new HashSet<Guid> { id } : null;
        var rows = ids.Select(id => learnedMemories[(reader.Security.Tenant.TenantId, id)])
            .Where(i => readable.Contains(i.AgentInstanceId) && (selected is null ? i.Status == MemoryItemStatus.Active : selected.Contains(i.MemoryId))).ToArray();
        if (args.Tool == ToolCatalog.MemoryConsolidate && rows.Length > 0 && inspectionHuman is null)
        {
            var resultId = TenantMemoryOperationRules.ResultId(reader, args, rows[0]);
            var result = learnedMemories.GetValueOrDefault((reader.Security.Tenant.TenantId, resultId));
            if (result is not null && readable.Contains(result.AgentInstanceId) && !rows.Any(i => i.MemoryId == resultId)) rows = [.. rows, result];
        }
        var observed = TenantMemoryOperationRules.Observe(reader, args, rows, expected, inspectionHuman is not null);
        TenantMemoryOperationRules.RequireRawAsync(reader, observed.Items, new IamAuthorizer(this), ct, inspectionHuman).AsTask().GetAwaiter().GetResult();
        return observed;
    }
}

public sealed partial class InMemoryIamStore
{
    private TenantMemoryOperationObservation RequireMemoryOperation(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.MemoryOperation ?? throw new SecurityConflictException("operation_mismatch");
        var result = ObserveMemoryOperation(run, TenantMemoryToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct);
        TenantMemoryOperationReview.ExecutorAsync(request, result, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return result;
    }
    private bool OwnMemoryForget(AgentRun run, Guid memory, string hash) => _operationReceipts.Values.Any(r => r.TenantId == run.Owner.TenantId
        && r.SessionId == run.SessionId && r.RootActivationId == run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is { } requestId
        && _approvals.GetValueOrDefault(requestId)?.Operation.MemoryOperation is { ToolName: ToolCatalog.MemoryForget } forgotten
        && TenantMemoryOperationRules.Sources(forgotten).Any(s => s.MemoryId == memory && s.ContentHash == hash));
    private bool HasOwnMemoryMaintenance(AgentRun run) => _operationReceipts.Values.Any(r => r.TenantId == run.Owner.TenantId && r.SessionId == run.SessionId && r.RootActivationId == run.Admission.Activation.RootActivationId
        && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is { } id && _approvals.GetValueOrDefault(id)?.Operation.MemoryOperation is { } memory && !TenantMemoryToolContract.ReadOnly(memory.ToolName));
    private IReadOnlyList<TenantLearnedMemory> MemoryEffectValues(TenantMemoryOperationObservation observed, DateTimeOffset at)
    {
        var values = TenantMemoryOperationMutation.Values(observed, at);
        if (observed.Value.ResultMemoryId is { } result)
        {
            var draft = values.Last();
            if (learnedMemories.ContainsKey((draft.TenantId, result)) || learnedMemoryIndex.GetValueOrDefault((draft.TenantId, draft.OwnerKey, MemoryItemStatus.Active))?.Any(id =>
                !observed.Items.Any(i => i.MemoryId == id) && learnedMemories[(draft.TenantId, id)] is { } row && row.Kind == draft.Kind && row.SubjectKey == draft.SubjectKey) == true)
                throw new SecurityConflictException("memory_subject_conflict");
        }
        return values;
    }
}

public sealed partial class InMemoryIamStore
{
    private bool MemoryReadConsented(TenantMemoryReader reader, AgentCore.Domain.Memory.TenantMemoryReference item, CancellationToken ct)
    {
        foreach (var receipt in _operationReceipts.Values.Where(r => r.TenantId == reader.Run.Owner.TenantId && r.SessionId == reader.Run.SessionId
            && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is not null))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            var value = request?.Operation.MemoryOperation ?? request?.Operation.ContinuityRead?.Memory;
            if (value is null || !TenantMemoryToolContract.ReadOnly(value.ToolName)
                || !TenantMemoryOperationRules.Sources(value).Any(s => s.MemoryId == item.MemoryId && s.ContentHash == item.ContentHash)
                || request!.Operation.ContinuityRead is { } continuity && !ContinuitySourceOwnersAvailable(reader, continuity)
                || _approvalDecisions.GetValueOrDefault(request!.RequestId) is not { Status: TenantApprovalStatus.Approved } decision) continue;
            var references = TenantMemoryOperationRules.Sources(value).Select(s => learnedMemories.GetValueOrDefault((request.TenantId, s.MemoryId))?.Reference()).ToArray();
            if (references.Any(s => s is null)) continue;
            try { ValidateAgentActor(reader.Security, decision.HumanPrincipalId); TenantMemoryReadConsent.RequireAsync(request, reader, references.Select(s => s!).ToArray(), decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true; }
            catch (AccessDeniedException) { }
        }
        return false;
    }
}

public sealed partial class InMemoryIamStore
{
    private bool MemoryApprovalVisible(TenantApprovalRequest request, TenantMemoryApprovalVisibility? visibility)
    {
        if (visibility is null || request.Operation.MemoryOperation is not { } memory || memory.UserPrincipalId is { } owner && owner != visibility.HumanPrincipalId) return false;
        bool Agent(Guid id) => visibility.Agents is null || visibility.Agents.Contains(id);
        bool Session(Guid id) => visibility.Sessions is null || visibility.Sessions.Contains(id);
        return Agent(request.Operation.InstanceId) && Session(memory.SessionId) && Session(memory.RootSessionId)
            && TenantMemoryOperationRules.Sources(memory).All(s => learnedMemories.GetValueOrDefault((request.TenantId, s.MemoryId)) is { } item
                && Agent(item.AgentInstanceId) && (item.Scope != MemoryScope.Session || Session(item.SessionId)));
    }
}
