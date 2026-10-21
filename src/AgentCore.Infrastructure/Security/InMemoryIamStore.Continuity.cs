using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantContinuityStore
{
    public ValueTask<TenantContinuityObservation> ObserveTenantContinuityAsync(TenantRunReference reference, Guid? generation,
        TenantContinuityArguments arguments, PreparedContinuityRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("continuity_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ValueTask.FromResult(ObserveContinuity(run, arguments, expected, inspectionHuman, ct));
        }
    }
    private TenantContinuityObservation ObserveContinuity(AgentRun run, TenantContinuityArguments arguments, PreparedContinuityRead? expected, Guid? human, CancellationToken ct)
    {
        var args = TenantContinuityToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = MemoryReader(run, ct); var iam = new IamAuthorizer(this);
        if (human is { } actor) ValidateAgentActor(reader.Security, actor);
        if (human is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        var visible = TenantContinuityRules.VisibleSessionIdsAsync(reader, iam, ct, human).AsTask().GetAwaiter().GetResult();
        if (human is null) TenantContinuityRules.Eligible(reader);
        TenantMemoryOperationObservation? memory = null;
        if (human is not null ? expected!.Memory is not null : TenantContinuityRules.IncludeMemoryAsync(reader, args, iam, ct).AsTask().GetAwaiter().GetResult())
            memory = ObserveMemoryOperation(run, TenantMemoryToolContract.Parse(args.Tool, args.CanonicalArguments), expected?.Memory, human, ct);
        TenantExperienceFactObservation? experience = null;
        var settings = Isolation(run.AgentInstanceId);
        if (human is not null ? expected!.Experience is not null : TenantContinuityRules.IncludeExperienceAsync(reader, settings, args, iam, ct).AsTask().GetAwaiter().GetResult())
            experience = ObserveExperienceFacts(run, args, expected?.Experience, human, ct);
        var named = human is not null ? TenantContinuityRules.Sessions(expected!) : null;
        var candidates = args.Kind is not null || human is not null ? [] : sessionBacking.ListTenantSessions(reader.Security.Tenant.TenantId, visible, 0, TenantContinuityRules.CandidateLimit + 2, instanceId: run.AgentInstanceId, recentFirst: true).Items.Where(s => s.Owner.SessionId != run.SessionId).Select(s => s.Owner.SessionId).ToArray();
        var selected = args.Kind is "Memory" or "Experience" ? [] : named is not null ? named.Select(s => s.SessionId).ToArray() : args.Id is { } selectedId ? [selectedId]
            : candidates.Take(TenantContinuityRules.CandidateLimit).ToArray();
        var documents = new List<TenantHistoricalSession>();
        foreach (var id in selected.Where(id => id != run.SessionId && (visible is null || visible.Contains(id))))
        {
            var source = sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, id);
            if (source is null || source.Owner.AgentInstanceId != run.AgentInstanceId || source.Owner.ExecutorPrincipalId != reader.Agent.PrincipalId) continue;
            var witness = named?.Single(s => s.SessionId == id);
            var after = witness?.AfterEntrySequence ?? (args.Kind == "Session" ? args.AfterEntrySequence : Math.Max(0, source.Snapshot.DurableLastEntrySequence - 20));
            var limit = witness?.Limit ?? (args.Kind == "Session" ? args.Limit : 20);
            var rows = sessionBacking.ReadTenantHistoricalWindow(reader.Security.Tenant.TenantId, id, after, source.Snapshot.DurableLastEntrySequence, limit);
            documents.Add(TenantContinuityRules.Project(id, source.Snapshot.Revision, source.Snapshot.DurableLastEntrySequence, source.Snapshot.Title,
                source.Snapshot.Summary, source.Snapshot.UpdatedAt, rows.Take(limit).ToArray(), after, limit, rows.Count > limit));
        }
        if (named is not null && documents.Count != named.Length) throw new AccessDeniedException("continuity_source_unavailable");
        return TenantContinuityRules.Observe(reader, args, documents.AsReadOnly(), memory, expected, human is not null,
            human is null && args.Kind is null && candidates.Length > TenantContinuityRules.CandidateLimit, experience);
    }
    public ValueTask RequireTenantHistoricalSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantHistoricalSource> sources, Guid? generation = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("continuity_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            else if (!run.IsTerminal) throw new AccessDeniedException("continuity_unavailable");
            RequireHistoricalSources(run, sources, ct); return ValueTask.CompletedTask;
        }
    }
    private void RequireHistoricalSources(AgentRun run, IReadOnlyList<TenantHistoricalSource> sources, CancellationToken ct)
    {
        TenantContinuityReadRules.RequireSourcesShape(run, sources); var reader = MemoryReader(run, ct); TenantContinuityRules.Eligible(reader);
        foreach (var source in sources)
        {
            var observed = ObserveContinuity(run, TenantContinuityToolContract.Parse(ToolCatalog.ContinuityGet, System.Text.Json.JsonSerializer.Serialize(new {
                kind = "Session", id = source.SessionId, afterEntrySequence = source.AfterEntrySequence, limit = source.Limit })), null, null, ct);
            TenantContinuityReadRules.RequireSource(source, observed.Sessions.Single());
            if (!TenantContinuityReadRules.OrdinaryAsync(observed, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult()
                && !HistoricalReadConsented(observed, source, ct)) throw new AccessDeniedException("continuity_read_review_required");
        }
    }
}
