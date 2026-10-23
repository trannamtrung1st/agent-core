using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantExperienceStore
{
    private readonly Dictionary<(Guid Tenant, Guid Experience), TenantExperience> tenantExperiences = [];
    private readonly Dictionary<(Guid Tenant, Guid Agent, string Owner), HashSet<Guid>> tenantExperienceIndex = [];
    private TenantExperienceSourceObservation ObserveExperienceSource(AgentRun run, ExperienceSourceKind kind, Guid id,
        TenantExperienceSourceWitness? expected, Guid? human, CancellationToken ct)
    {
        var reader = MemoryReader(run, ct); var settings = Isolation(run.AgentInstanceId); var iam = new IamAuthorizer(this);
        if (human is null) TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        else ValidateAgentActor(reader.Security, human.Value);
        if (TenantExperienceReviewRules.Selected(reader.Root) is { } selected)
        {
            if (kind != selected.Kind || id != selected.SourceId || expected is not null && expected != selected) throw new AccessDeniedException("experience_source_changed");
            expected = selected;
        }
        TenantExperienceSourceObservation result;
        if (kind == ExperienceSourceKind.Session)
        {
            var source = sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, id);
            if (source is null || source.Owner.AgentInstanceId != run.AgentInstanceId || source.Owner.ExecutorPrincipalId != reader.Agent.PrincipalId || id == run.SessionId)
                throw new AccessDeniedException("experience_source_unavailable");
            var raw = new TenantExperienceSourceWitness(kind, id, id, source.Snapshot.Revision, Math.Max(1, source.Snapshot.DurableLastEntrySequence), 0, 20, null, null, new string('a', 64));
            TenantExperienceRules.RawAsync(reader, raw, false, iam, ct, human).AsTask().GetAwaiter().GetResult();
            var rows = sessionBacking.ReadTenantHistoricalWindow(reader.Security.Tenant.TenantId, id, Math.Max(0, source.Snapshot.DurableLastEntrySequence - 100), source.Snapshot.DurableLastEntrySequence, 101);
            var stable = AgentCore.Application.Security.TenantExperienceSourceProjection.StableSessionCheckpoint(rows);
            result = TenantExperienceSourceProjection.Session(reader, settings, source.Snapshot, rows, stable, human is null || TenantExperienceReviewRules.Selected(reader.Root) is not null ? expected : null);
        }
        else if (kind == ExperienceSourceKind.AgentRun)
        {
            var source = sessionBacking.ReadRetainedTenantOperationRun(reader.Security.Tenant.TenantId, id);
            if (source is null || source.AgentInstanceId != run.AgentInstanceId || source.Owner.ExecutorPrincipalId != reader.Agent.PrincipalId || !TenantExperienceRules.Substantive(source) || sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, source.SessionId) is null)
                throw new AccessDeniedException("experience_source_unavailable");
            var raw = new TenantExperienceSourceWitness(kind, id, source.SessionId, 0, source.Revision, 0, 0, id, source.Revision, new string('a', 64));
            TenantExperienceRules.RawAsync(reader, raw, false, iam, ct, human).AsTask().GetAwaiter().GetResult();
            RequireTenantOutputSources(source, ct);
            var root = TenantCoreRoot(source); var privateSource = HasPrivateExperienceEvidence(source);
            var privateUser = privateSource ? TenantRunProfileRules.User(source, root, reader.Security)?.PrincipalId : null;
            if (privateSource && (privateUser is null || privateUser != reader.User?.PrincipalId))
                throw new AccessDeniedException("experience_source_unavailable");
            if (human is { } viewer && privateSource && reader.User?.PrincipalId != viewer) throw new AccessDeniedException("approval_unavailable");
            result = TenantExperienceSourceProjection.Run(reader, settings, source, privateUser, human is null || TenantExperienceReviewRules.Selected(reader.Root) is not null ? expected : null);
        }
        else throw new AccessDeniedException("experience_source_unavailable");
        return result;
    }
    public ValueTask<TenantExperienceSourceObservation> ObserveTenantExperienceSourceAsync(TenantRunReference reference, Guid? generation,
        ExperienceSourceKind kind, Guid id, TenantExperienceSourceWitness? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("experience_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ValueTask.FromResult(ObserveExperienceSource(run, kind, id, expected, inspectionHuman, ct));
        }
    }
    private TenantExperienceOperationObservation ObserveExperienceOperation(AgentRun run, TenantExperienceArguments arguments, PreparedExperienceOperation? expected, Guid? human, CancellationToken ct)
    {
        var args = TenantExperienceToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = MemoryReader(run, ct); var settings = Isolation(run.AgentInstanceId);
        if (human is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        TenantExperienceSourceObservation? source = null; TenantExperience[] items = [];
        if (args.SourceKind is { } kind)
        {
            var witness = expected?.Source ?? (args.Tool == TenantExperienceToolContract.Record
                ? TenantExperienceReferences.RecordedSource(reader, kind, args.SourceId!.Value, args.ThroughCursor!.Value) : null);
            if (args.Tool == TenantExperienceToolContract.Record && human is null && witness != TenantExperienceReferences.RecordedSource(reader, kind, args.SourceId!.Value, args.ThroughCursor!.Value))
                throw new AccessDeniedException("experience_source_inspection_required");
            source = ObserveExperienceSource(run, kind, args.SourceId!.Value, witness, human, ct);
            if (args.Tool == TenantExperienceToolContract.Record && human is null) RequireExperienceSources(run, [new(reader.Security.Tenant.TenantId, source.Witness)], [], ct);
        }
        else if (args.SourceIds is { } ids)
        {
            items = ids.Select(id => tenantExperiences.GetValueOrDefault((reader.Security.Tenant.TenantId, id))).Where(i => i is not null).Select(i => TenantExperienceRules.Copy(i!)).ToArray();
            if (items.Length != ids.Count || items.Any(i => !TenantExperienceRules.Owns(reader, i.Reference()))) throw new AccessDeniedException("experience_sources_unavailable");
        }
        var existing = args.ReadOnly ? null : tenantExperiences.GetValueOrDefault((reader.Security.Tenant.TenantId, TenantExperienceRules.ResultId(reader, args)));
        var result = TenantExperienceOperationRules.Observe(reader, settings, args, source, items, existing, expected, human is not null);
        TenantExperienceRules.RawAsync(reader, source?.Witness, args.Tool == TenantExperienceToolContract.Consolidate, new IamAuthorizer(this), ct, human,
            privateValue: !args.ReadOnly).AsTask().GetAwaiter().GetResult(); return result;
    }
    public ValueTask<TenantExperienceOperationObservation> ObserveTenantExperienceOperationAsync(TenantRunReference reference, Guid? generation,
        TenantExperienceArguments arguments, PreparedExperienceOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("experience_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ValueTask.FromResult(ObserveExperienceOperation(run, arguments, expected, inspectionHuman, ct));
        }
    }
    private IEnumerable<TenantExperience> OwnedExperience(TenantMemoryReader reader) => new[] { "instance", TenantExperienceRules.OwnerKey(reader) }.Distinct()
        .SelectMany(key => tenantExperienceIndex.GetValueOrDefault((reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, key)) ?? [])
        .Select(id => tenantExperiences[(reader.Security.Tenant.TenantId, id)]).Where(i => TenantExperienceRules.Owns(reader, i.Reference())).Select(TenantExperienceRules.Copy);
    public ValueTask<TenantExperienceObservation> ReadTenantRunExperienceAsync(TenantRuntimeFrame frame, string? query = null, Guid? experienceId = null, int limit = 5, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var reader = MemoryReader(RequireMemoryFrame(frame), ct); var settings = Isolation(reader.Run.AgentInstanceId); var iam = new IamAuthorizer(this);
            var allowed = false;
            try { TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source); TenantExperienceRules.RawAsync(reader, null, true, iam, ct).AsTask().GetAwaiter().GetResult();
                allowed = TenantExperienceRules.OrdinaryAsync(reader, null, true, false, iam, ct).AsTask().GetAwaiter().GetResult(); }
            catch (AccessDeniedException) { }
            var items = allowed ? OwnedExperience(reader).Where(i => i.Visibility == ExperienceVisibility.Eligible && i.Content is not null && (experienceId is null || i.ExperienceId == experienceId))
                .OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.ExperienceId).Take(101)
                .Select(i => (Item: i, Score: AgentCore.Application.Continuity.ContinuityRelevance.Score(TenantExperienceRules.SearchText(i), query)))
                .Where(i => string.IsNullOrWhiteSpace(query) || i.Score > 0).OrderByDescending(i => i.Score).ThenByDescending(i => i.Item.UpdatedAt).ThenBy(i => i.Item.ExperienceId)
                .Take(Math.Clamp(limit, 1, 11)).Select(i => i.Item).ToArray() : [];
            return ValueTask.FromResult(new TenantExperienceObservation(reader, settings, items, reader.Security.Tenant.Revision, allowed));
        }
    }
    private void SaveExperience(TenantExperience value)
    {
        tenantExperiences[(value.TenantId, value.ExperienceId)] = value with { Content = value.Content is null ? null : TenantExperienceRules.Freeze(value.Content) };
        var key = (value.TenantId, value.AgentInstanceId, value.OwnerKey);
        if (!tenantExperienceIndex.TryGetValue(key, out var ids)) tenantExperienceIndex[key] = ids = [];
        ids.Add(value.ExperienceId);
    }
    private bool HasPrivateExperienceEvidence(AgentRun run)
    {
        var facts = TenantExperienceReferences.Facts(run.Checkpoint);
        var rows = facts.Select(i => tenantExperiences.GetValueOrDefault((i.TenantId, i.ExperienceId)) ?? throw new AccessDeniedException("experience_source_changed")).ToArray();
        return TenantExperienceRules.HasPrivateEvidence(run, rows.Any(i => i.UserPrincipalId is not null));
    }
}
