using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemorySandboxStore
{
    private InMemoryMemoryStore? tenantSessions;
    private InMemoryAgentInstanceStore? tenantInstances;
    internal InMemorySandboxStore BindTenantAuthority(InMemoryMemoryStore sessions, InMemoryAgentInstanceStore instances)
    {
        lock (_gate)
        {
            if (tenantSessions is not null && !ReferenceEquals(tenantSessions, sessions) || tenantInstances is not null && !ReferenceEquals(tenantInstances, instances))
                throw new InvalidOperationException("Sandbox runtime must share actual tenant ownership backing.");
            tenantSessions = sessions; tenantInstances = instances; return this;
        }
    }
    private bool OwnedSession(Guid session) => tenantSessions?.RetainedTenantSessionOwnerSnapshot(session) is not null;
    private bool OwnedInstance(Guid instance) => tenantInstances?.IsTenantOwned(instance) == true;
    internal IReadOnlyList<SandboxLease> TenantRecoveryLeases(string? afterSession, int count) => _leases.Values
        .Where(l => l.TenantId is not null && (afterSession is null || string.CompareOrdinal(l.SessionId.ToString("D"), afterSession) > 0))
        .OrderBy(l => l.SessionId.ToString("D"), StringComparer.Ordinal).Take(count).Select(FreezeLease).ToArray();
    internal SandboxLease? TenantLease(Guid tenant, Guid session) => _leases.GetValueOrDefault(session) is { } lease && lease.TenantId == tenant ? FreezeLease(lease) : null;
    internal SandboxExecution? TenantExecution(Guid tenant, Guid session, Guid execution) => _executions.GetValueOrDefault(execution) is { } value
        && value.TenantId == tenant && value.SessionId == session ? value : null;
    internal SandboxExecution? TenantOperation(Guid tenant, Guid session, Guid run, string call) => _executions.Values.SingleOrDefault(e => e.TenantId == tenant && e.SessionId == session && e.AgentRunId == run && e.ToolCallId == call);
    private static SandboxLease FreezeLease(SandboxLease lease) => lease with { BaselineProcesses = lease.BaselineProcesses?.ToArray() };
    internal void SaveTenantLease(SandboxLease lease) => _leases[lease.SessionId] = FreezeLease(lease);
    internal void SaveTenantExecution(SandboxExecution execution) => _executions[execution.Id] = execution;
    internal SandboxExecution? ApprovalOperation(Guid operation) => _executions.Values.SingleOrDefault(e => e.OperationId == operation);
    internal bool HasLease(Guid session) => _leases.ContainsKey(session);
    internal bool HasExecution(Guid execution) => _executions.ContainsKey(execution);
    internal bool HasOperation(Guid session, Guid run, string call) => _executions.Values.Any(e => e.SessionId == session && e.AgentRunId == run && e.ToolCallId == call);
}
