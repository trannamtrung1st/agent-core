using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<AgentRunAdmissionResult> AdmitTenantImmediateAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot,
        AgentRun child, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(frame.Owner);
        if (auditId == Guid.Empty) throw new ArgumentException("An audit identity is required.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireRuntimeOwner(frame.Owner); var tenant = frame.Owner.TenantId!.Value; var state = _tenants[tenant];
            var parent = sessionBacking.ReadTenantRun(tenant, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var session = sessionBacking.ReadTenantSession(tenant, frame.SessionId) ?? throw new AccessDeniedException("session_unavailable");
            var agent = FindTenantAgentAsync(tenant, frame.Owner.AgentInstanceId, ct).Result!;
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var root = TenantBackgroundAdmissionRules.Shape(frame, session, parent, snapshot, child, agent, now);
            RequireSpaceExecutionSource(child, state, new IamAuthorizer(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
                ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
                : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).AsTask().GetAwaiter().GetResult();
            if (baseline is null) throw new AccessDeniedException("definition_unavailable");
            var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
            TenantBackgroundAdmissionRules.AuthorizeAsync(state, parent, current, new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
            var key = AgentRunStoreMapping.BackgroundSourceKey(snapshot, child)!;
            var all = sessionBacking.AgentRuns;
            if (all.ByBackgroundSource.TryGetValue((child.Owner, key), out var previous))
            {
                var saved = all.Runs[previous]; var savedSession = sessionBacking.ReadTenantSession(tenant, saved.SessionId);
                if (savedSession is null || !TenantBackgroundAdmissionRules.Same(saved, savedSession.Snapshot, child, snapshot))
                    throw new SecurityConflictException("background_id_conflict");
                return ValueTask.FromResult(new AgentRunAdmissionResult(false, saved));
            }
            if (_audit.Any(a => a.AuditId == auditId)) throw new SecurityConflictException("identity_conflict");
            var audit = new AuthorizationAudit(auditId, tenant, frame.Owner.ExecutorPrincipalId!.Value,
                "agent-runs.background.admit", child.AgentRunId.ToString("D"), "allowed", "owned_parent_child_admitted", now);
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            ct.ThrowIfCancellationRequested();
            StageTenantBackgroundGraph(root, snapshot, child);
            _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult(new AgentRunAdmissionResult(true, child));
        }
    }
}
