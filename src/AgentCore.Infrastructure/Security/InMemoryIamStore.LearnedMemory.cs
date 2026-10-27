using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantLearnedMemoryStore
{
    private readonly Dictionary<(Guid Tenant, Guid Memory), TenantLearnedMemory> learnedMemories = [];
    private readonly Dictionary<(Guid Tenant, string Owner, MemoryItemStatus Status), HashSet<Guid>> learnedMemoryIndex = [];
    private TenantMemoryReader MemoryReader(AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("memory_context_unavailable");
        var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("memory_context_unavailable");
        var root = TenantCoreRoot(run); var user = TenantRunProfileRules.User(run, root, state);
        if (run.Admission.DelegatedTask is null && run.Admission.AgentMessage is null && root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage && (user is null || _accounts.GetValueOrDefault(user.AccountId)?.Status != IdentityStatus.Active))
            throw new AccessDeniedException("memory_context_unavailable");
        var session = sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("memory_context_unavailable");
        var parent = sessionBacking.ReadTenantSession(tenant, root.SessionId) ?? throw new AccessDeniedException("memory_context_unavailable");
        var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("memory_context_unavailable");
        if (run.Admission.Configuration is null) throw new AccessDeniedException("memory_context_unavailable");
        TenantExecutionLineage.RequireRequesterAsync(run, state, new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
        return new(run, root, state, agent, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), session, parent, user);
    }
    private AgentRun RequireMemoryFrame(TenantRuntimeFrame frame)
    {
        var run = sessionBacking.ReadTenantRun(frame.Owner.TenantId ?? Guid.Empty, frame.RunId) ?? throw new AccessDeniedException("memory_context_unavailable");
        var session = sessionBacking.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId) ?? throw new AccessDeniedException("memory_context_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, (clock ?? TimeProvider.System).GetUtcNow()); return run;
    }
    private IReadOnlyList<TenantLearnedMemory> OwnedMemory(TenantMemoryReader reader, IReadOnlyList<MemoryProposal>? proposals = null) => TenantMemoryRules.OwnerKeys(reader)
        .SelectMany(key => new[] { MemoryItemStatus.Active, MemoryItemStatus.Resolved }.SelectMany(status =>
            learnedMemoryIndex.GetValueOrDefault((reader.Security.Tenant.TenantId, key, status)) ?? []))
        .Select(id => learnedMemories[(reader.Security.Tenant.TenantId, id)])
        .Where(i => TenantMemoryRules.Owns(reader, i) && (i.Status == MemoryItemStatus.Active || i.Status == MemoryItemStatus.Resolved
            && proposals?.Any(p => p.Operation == MemoryProposalOperation.Resolve && p.Kind == i.Kind
                && StructuredMemoryItem.SubjectKeyFor(StructuredMemoryItem.CollapseSubject(p.Subject)) == i.SubjectKey) == true))
        .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.MemoryId)
        .GroupBy(i => (i.OwnerKey, i.Kind, i.SubjectKey, i.Status)).Select(g => g.First()).ToArray();
    private bool MemoryAllowed(TenantMemoryReader reader, TenantLearnedMemory? source, bool write, CancellationToken ct) => source is null
        ? TenantMemoryRules.AvailableAsync(reader, write, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult()
        : TenantMemoryRules.SourceAllowedAsync(reader, source, write, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
    public ValueTask<TenantMemoryObservation> ReadTenantRunMemoryAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var reader = MemoryReader(RequireMemoryFrame(frame), ct);
            var readable = reader.Effective.MemoryPolicy?.SessionMemory == true && MemoryAllowed(reader, null, false, ct);
            var items = readable ? TenantMemoryRules.Project(OwnedMemory(reader).Where(i => TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, i.Scope, false)
                && MemoryAllowed(reader, i, false, ct))) : [];
            return ValueTask.FromResult(new TenantMemoryObservation(items, Array.AsReadOnly(items.Select(i => new TenantMemorySource(i.TenantId, i.MemoryId, i.ContentHash)).ToArray()),
                reader.Security.Tenant.Revision, reader.Agent.Instance.Revision, reader.User?.PrincipalId, readable));
        }
    }
    public ValueTask RequireTenantMemoryProjectionAsync(TenantRuntimeFrame frame, TenantMemoryObservation observation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var reader = MemoryReader(RequireMemoryFrame(frame), ct);
            if (observation.SecurityRevision != reader.Security.Tenant.Revision || observation.InstanceRevision != reader.Agent.Instance.Revision
                || observation.UserPrincipalId != reader.User?.PrincipalId || !observation.ReadAllowed || reader.Effective.MemoryPolicy?.SessionMemory != true || !MemoryAllowed(reader, null, false, ct)
                || observation.Items.Count != observation.Sources.Count || observation.Items.Count > MemoryLimits.PromptMaxItems)
                throw new AccessDeniedException("memory_context_changed");
            foreach (var item in observation.Items)
                if (learnedMemories.GetValueOrDefault((item.TenantId, item.MemoryId)) != item || item.Status != MemoryItemStatus.Active
                    || !TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, item.Scope, false)
                    || !observation.Sources.Contains(new(item.TenantId, item.MemoryId, item.ContentHash))) throw new AccessDeniedException("memory_context_changed");
            RequireMemorySources(reader.Run, observation.Sources, ct); return ValueTask.CompletedTask;
        }
    }
    public ValueTask RequireTenantMemorySourcesAsync(TenantRunReference reference, IReadOnlyList<TenantMemorySource> sources, Guid? generation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("memory_context_unavailable");
            if (generation is { } expected && (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != expected
                || run.Claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()) || generation is null && !run.IsTerminal)
                throw new AccessDeniedException("memory_context_unavailable");
            RequireMemorySources(run, sources, ct); return ValueTask.CompletedTask;
        }
    }
    private void RequireMemorySources(AgentRun run, IReadOnlyList<TenantMemorySource> sources, CancellationToken ct)
    {
        if (sources.Count > MemoryLimits.MaxActiveItems || sources.Distinct().Count() != sources.Count) throw new AccessDeniedException("memory_source_unavailable");
        if (sources.Count == 0) return; var reader = MemoryReader(run, ct);
        var ordinaryRead = MemoryAllowed(reader, null, false, ct);
        foreach (var source in sources)
        {
            var item = learnedMemories.GetValueOrDefault((source.TenantId, source.MemoryId));
            if (source.TenantId != run.Owner.TenantId || item is null || item.ContentHash != source.ContentHash
                || !TenantMemoryRules.ScopeEnabled(run.Admission.Configuration!.Definition.MemoryPolicy, item.Scope, false)
                || item.Status == MemoryItemStatus.Deleted && !source.MetadataOnly && !OwnMemoryForget(run, source.MemoryId, source.ContentHash)
                || !(ordinaryRead && TenantMemoryRules.SourceAllowedAsync(reader, item, false, new IamAuthorizer(this), ct, source.MetadataOnly || OwnMemoryForget(run, source.MemoryId, source.ContentHash)).AsTask().GetAwaiter().GetResult()
                    || MemoryReadConsented(reader, item.Reference(), ct))) throw new AccessDeniedException("memory_source_changed");
        }
    }
    public ValueTask<TenantMemoryPlan> PrepareTenantLearningAsync(TenantRuntimeFrame frame, SessionSnapshot proposed, IReadOnlyList<MemoryProposal> proposals,
        IReadOnlyList<Guid> candidateIds, Guid auditId, DateTimeOffset preparedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
            return ValueTask.FromResult(PrepareLearning(frame, proposed, proposals, candidateIds, auditId, preparedAt, ct));
    }
    private TenantMemoryPlan PrepareLearning(TenantRuntimeFrame frame, SessionSnapshot proposed, IReadOnlyList<MemoryProposal> proposals,
        IReadOnlyList<Guid> candidateIds, Guid auditId, DateTimeOffset preparedAt, CancellationToken ct)
    {
        var reader = MemoryReader(RequireMemoryFrame(frame), ct);
        TenantRuntimeRules.ValidateSnapshot(frame, reader.Session.Snapshot, proposed);
        if (preparedAt > (clock ?? TimeProvider.System).GetUtcNow() || candidateIds.Any(id => learnedMemories.ContainsKey((reader.Security.Tenant.TenantId, id)))
            || _audit.Any(a => a.AuditId == auditId)) throw new AccessDeniedException("memory_learning_changed");
        var owned = OwnedMemory(reader, proposals); var writable = owned.Where(i => MemoryAllowed(reader, i, true, ct)).Select(i => i.MemoryId).ToHashSet();
        return TenantMemoryRules.Plan(frame, reader, owned, writable, proposals, candidateIds, auditId, preparedAt, MemoryAllowed(reader, null, true, ct) && !HasOwnMemoryMaintenance(reader.Run), proposed.Entries);
    }
    private Action PrepareLearningCommit(TenantRuntimeFrame frame, SessionSnapshot snapshot, TenantMemoryPlan? plan, CancellationToken ct)
    {
        if (snapshot.RetentionKind == SessionRetentionKind.Temporary) return () => { };
        if (plan is null) return () => { };
        if (plan.Frame != frame) throw new AccessDeniedException("memory_learning_changed");
        var actual = PrepareLearning(frame, snapshot, plan.Proposals, plan.CandidateIds, plan.AuditId, plan.PreparedAt, ct);
        TenantMemoryRules.Match(actual, plan, snapshot);
        if (plan.Changes.Count == 0) return () => { };
        var state = _tenants[frame.Owner.TenantId!.Value]; var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(plan.AuditId, state.Tenant.TenantId, frame.Owner.ExecutorPrincipalId!.Value,
            "memory.learn", frame.RunId.ToString("D"), "allowed", "owned_response_memory_committed", plan.PreparedAt);
        return () => { foreach (var change in plan.Changes)
            {
                SetLearnedMemory(change.Value);
            }
            _tenants[state.Tenant.TenantId] = next; _audit.Add(audit); };
    }
    private void SetLearnedMemory(TenantLearnedMemory value)
    {
        if (learnedMemories.TryGetValue((value.TenantId, value.MemoryId), out var old)) learnedMemoryIndex[(old.TenantId, old.OwnerKey, old.Status)].Remove(old.MemoryId);
        learnedMemories[(value.TenantId, value.MemoryId)] = value;
        var key = (value.TenantId, value.OwnerKey, value.Status);
        if (!learnedMemoryIndex.TryGetValue(key, out var ids)) learnedMemoryIndex[key] = ids = [];
        ids.Add(value.MemoryId);
    }
    private void TombstoneSessionMemory(Guid tenant, Guid session, DateTimeOffset at)
    {
        foreach (var old in learnedMemories.Values.Where(i => i.TenantId == tenant && i.SessionId == session && i.Scope == MemoryScope.Session && i.Status != MemoryItemStatus.Deleted).ToArray())
            SetLearnedMemory(old with { Status = MemoryItemStatus.Deleted, Subject = "", Content = "", SubjectKey = "", Revision = checked(old.Revision + 1), UpdatedAt = at > old.UpdatedAt ? at : old.UpdatedAt });
    }
    private void RequireLearnedMemoryReferences(TenantSecurityState next)
    {
        if (learnedMemories.Values.Any(i => i.TenantId == next.Tenant.TenantId && i.UserPrincipalId is not null
            && !next.Users.Any(u => u.UserId == i.UserId && u.PrincipalId == i.UserPrincipalId && u.AccountId == i.AccountId)))
            throw new ArgumentException("Retain the actual learned-memory User owner; change its status without reparenting it.");
    }
    private void RequireStoredMemorySources(AgentRun run, CancellationToken ct)
    {
        RequireManagementReadSources(run, TenantManagementReadReferences.Read(run.Checkpoint), ct);
        RequireAutomationReadSources(run, TenantAutomationReadReferences.Read(run.Checkpoint), ct);
        RequireExperienceSources(run, TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceReferences.Facts(run.Checkpoint), ct);
        if (TenantHistoricalReferences.Read(run.Checkpoint) is { Count: > 0 } history) RequireHistoricalSources(run, history, ct);
        if (TenantMemoryOperationReferences.Read(run.Checkpoint) is { Count: > 0 } sources) RequireMemorySources(run, sources, ct);
    }
    private void RequireMemoryCheckpoint(AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        RequireManagementReadCheckpoint(run, checkpoint, ct);
        RequireAutomationReadCheckpoint(run, checkpoint, ct);
        RequireExperienceCheckpoint(run, checkpoint, ct);
        var oldHistory = TenantHistoricalReferences.Read(run.Checkpoint); var nextHistory = TenantHistoricalReferences.Read(checkpoint);
        if (oldHistory.Except(nextHistory).Any()) throw new AccessDeniedException("continuity_source_unavailable");
        if (nextHistory.Count > 0) RequireHistoricalSources(run, nextHistory, ct);
        var old = TenantMemoryOperationReferences.Read(run.Checkpoint);
        var proposed = TenantMemoryOperationReferences.Read(checkpoint);
        if (old.Except(proposed).Any()) throw new AccessDeniedException("memory_source_unavailable");
        RequireMemorySources(run, proposed, ct);
        var added = proposed.Except(old).ToArray(); if (added.Length == 0) return;
        var reader = MemoryReader(run, ct);
        if (added.Any(s => !TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy,
            learnedMemories[(s.TenantId, s.MemoryId)].Scope, false))) throw new AccessDeniedException("memory_context_changed");
    }
}
