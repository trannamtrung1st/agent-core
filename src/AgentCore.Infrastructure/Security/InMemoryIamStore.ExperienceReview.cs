using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantExperienceReviewStore
{
    private TenantExperienceReviewSource ExperienceReviewSource(Guid tenant, Guid instance, HumanSessionReference human,
        ExperienceSourceKind kind, Guid id, CancellationToken ct)
    {
        RequireProfileHuman(tenant, human); var state = _tenants[tenant];
        var agent = FindTenantAgentAsync(tenant, instance, ct).Result ?? throw new AccessDeniedException("experience_source_unavailable");
        var run = kind == ExperienceSourceKind.AgentRun ? sessionBacking.ReadRetainedTenantOperationRun(tenant, id) : null;
        var session = sessionBacking.ReadTenantSession(tenant, run?.SessionId ?? id);
        if (session is null || session.Owner.AgentInstanceId != instance || session.Owner.ExecutorPrincipalId != agent.PrincipalId
            || kind == ExperienceSourceKind.AgentRun && (run is null || run.AgentInstanceId != instance || run.Owner.ExecutorPrincipalId != agent.PrincipalId))
            throw new AccessDeniedException("experience_source_unavailable");
        TenantExperienceReviewRules.RequireAsync(state, agent, human, Isolation(instance), kind, id, session.Owner.SessionId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
        (TenantExperienceSourceWitness Witness, string EvidenceJson, string Title) projected;
        if (kind == ExperienceSourceKind.Session)
        {
            var rows = sessionBacking.ReadTenantHistoricalWindow(tenant, id, Math.Max(0, session.Snapshot.DurableLastEntrySequence - 100), session.Snapshot.DurableLastEntrySequence, 101);
            projected = TenantExperienceSourceProjection.SessionEvidence(session.Snapshot, rows, AgentCore.Application.Experience.ExperienceService.StableSessionCheckpoint(rows));
        }
        else
        {
            RequireTenantOutputSources(run!, ct);
            var privateSource = HasPrivateExperienceEvidence(run!);
            if (privateSource && TenantRunProfileRules.User(run!, TenantCoreRoot(run!), state)?.PrincipalId != human.PrincipalId)
                throw new AccessDeniedException("experience_source_unavailable");
            projected = TenantExperienceSourceProjection.RunEvidence(run!, privateSource ? human.PrincipalId : null);
        }
        return new(tenant, instance, agent.PrincipalId, human.PrincipalId, projected.Witness, projected.Title, state.Tenant.Revision, agent.Instance.Revision);
    }
    public ValueTask<TenantExperienceReviewSource> ObserveOwnTenantExperienceReviewAsync(Guid tenant, Guid agent, HumanSessionReference human,
        ExperienceSourceKind kind, Guid source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (isolationBacking.IsolationGate)
            return ValueTask.FromResult(ExperienceReviewSource(tenant, agent, human, kind, source, ct));
    }
    public ValueTask<TenantExperienceReviewReceipt?> TryAdmitOwnTenantExperienceReviewAsync(TenantExperienceReviewAdmission admission,
        HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var selected = admission.Source; var current = ExperienceReviewSource(selected.TenantId, selected.AgentId, human, selected.Witness.Kind, selected.Witness.SourceId, ct);
            if (current != selected) return ValueTask.FromResult<TenantExperienceReviewReceipt?>(null);
            var state = _tenants[selected.TenantId]; var agent = FindTenantAgentAsync(selected.TenantId, selected.AgentId, ct).Result!;
            var root = TenantExperienceReviewRules.Shape(admission, agent, human, audit); var run = admission.Run;
            if (sessionBacking.ReadRetainedTenantOperationRun(selected.TenantId, run.AgentRunId) is { } previous)
            {
                if (previous.Owner != run.Owner || previous.Admission.Activation.DedupeKey != TenantExperienceReviewRules.Key(selected)
                    || previous.Admission.TaskIntent?.Text != TenantExperienceReviewRules.Objective(selected)) throw new SecurityConflictException("identity_conflict");
                return ValueTask.FromResult<TenantExperienceReviewReceipt?>(new(false, previous.SessionId, previous.AgentRunId, state.Tenant.Revision));
            }
            if (_audit.Any(i => i.AuditId == audit.AuditId)) throw new SecurityConflictException("identity_conflict");
            ct.ThrowIfCancellationRequested(); StageTenantBackgroundGraph(root, admission.Session, run);
            _tenants[selected.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; _audit.Add(audit);
            return ValueTask.FromResult<TenantExperienceReviewReceipt?>(new(true, run.SessionId, run.AgentRunId, checked(state.Tenant.Revision + 1)));
        }
    }
}
