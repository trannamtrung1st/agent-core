using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private Action? PrepareConfigurationContinuation(AgentRun source, SessionSnapshot snapshot, TenantConfigurationContinuationAdmission? admission, CancellationToken ct)
    {
        if (admission is null) return null;
        var state = _tenants[source.Owner.TenantId!.Value];
        if (state.Tenant.Revision != admission.ExpectedSecurityRevision) return null;
        RequireSpaceExecutionSource(admission.Run, state, new IamAuthorizer(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
        var root = TenantCoreRoot(source);
        var current = FindTenantAgentAsync(state.Tenant.TenantId, source.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        TenantConfigurationContinuationRules.RequireAdmission(source, root, snapshot, sessionBacking.AgentRuns.Runs.Values.Where(r => r.SessionId == source.SessionId),
            admission, current, state.Tenant.Revision, id => _approvals.GetValueOrDefault(id), id => _operationReceipts.GetValueOrDefault(id),
            sessionBacking.ReadTenantConversation(state.Tenant.TenantId, source.SessionId, null, null, 100)?.History.Items ?? []);
        var next = admission.Run; var all = sessionBacking.AgentRuns;
        if (all.Runs.ContainsKey(next.AgentRunId) || all.Activations.ContainsKey(next.ActivationId) || all.ByAdmission.ContainsKey((next.SessionId, next.Admission.Activation.DedupeKey)))
            throw new SecurityConflictException("identity_conflict");
        var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, next);
        return () => {
            all.Runs.Add(next.AgentRunId, next); all.Activations.Add(next.ActivationId, activation);
            all.ByAdmission.Add((next.SessionId, next.Admission.Activation.DedupeKey), next.AgentRunId);
            var currentState = _tenants[state.Tenant.TenantId];
            _tenants[state.Tenant.TenantId] = currentState with { Tenant = currentState.Tenant with { Revision = checked(currentState.Tenant.Revision + 1) } };
            _audit.Add(new(next.ActivationId, state.Tenant.TenantId, current.PrincipalId, "agent-runs.configuration.continue", next.AgentRunId.ToString("D"),
                "allowed", "verified_configuration_continuation", next.CreatedAtUtc));
        };
    }
}
