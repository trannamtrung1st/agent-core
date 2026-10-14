using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Admin;
using AgentCore.Infrastructure.Admin;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemorySandboxStore(InMemoryAdminEventStore? events = null) : ISandboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, long Revision), SandboxProfile> _profiles = [];
    private readonly Dictionary<Guid, SandboxLease> _leases = [];
    private readonly Dictionary<Guid, SandboxExecution> _executions = [];
    public ValueTask<IReadOnlyList<SandboxProfile>> ListProfilesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<SandboxProfile>>(_profiles.Values.GroupBy(p => p.Id).Select(g => g.MaxBy(p => p.Revision)!).OrderBy(p => p.Name).ToArray()); }
    public ValueTask<SandboxProfile?> GetProfileAsync(string id, long? revision = null, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_profiles.Values.Where(p => p.Id == id && (revision is null || p.Revision == revision)).MaxBy(p => p.Revision)); }
    public ValueTask SaveProfileAsync(SandboxProfile profile, long expectedRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        ct.ThrowIfCancellationRequested(); profile.Validate(); lock (_gate)
        {
            if ((_profiles.Values.Where(p => p.Id == profile.Id).MaxBy(p => p.Revision)?.Revision ?? 0) != expectedRevision || profile.Revision != expectedRevision + 1)
                throw AgentCoreErrors.Conflict("Sandbox profile changed. Reload before saving.");
            if (history is not null) (events ?? throw new InvalidOperationException("Sandbox audit store is required.")).AppendAsync(history,ct).GetAwaiter().GetResult();
            _profiles.Add((profile.Id, profile.Revision), profile with { });
        } return ValueTask.CompletedTask;
    }
    public ValueTask<SandboxLease?> GetLeaseAsync(Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_leases.GetValueOrDefault(sessionId)); }
    public ValueTask<IReadOnlyList<SandboxLease>> ListLeasesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<SandboxLease>>(_leases.Values.Where(l => l.State != SandboxLeaseState.Terminated).ToArray()); }
    public ValueTask SaveLeaseAsync(SandboxLease lease, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var old = _leases.GetValueOrDefault(lease.SessionId);
            if ((old?.Revision ?? 0) != expectedRevision || lease.Revision != expectedRevision + 1 || old is not null && old.AgentInstanceId != lease.AgentInstanceId)
                throw AgentCoreErrors.Conflict("Sandbox generation changed.");
            _leases[lease.SessionId] = lease;
        } return ValueTask.CompletedTask;
    }
    public ValueTask<SandboxExecution?> GetExecutionAsync(Guid sessionId, Guid executionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_executions.TryGetValue(executionId, out var e) && e.SessionId == sessionId ? e : null); }
    public ValueTask<SandboxExecution?> GetOperationAsync(Guid sessionId, Guid agentRunId, string toolCallId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_executions.Values.SingleOrDefault(e => e.SessionId == sessionId && e.AgentRunId == agentRunId && e.ToolCallId == toolCallId)); }
    public ValueTask SaveExecutionAsync(SandboxExecution execution, bool create, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var old = _executions.GetValueOrDefault(execution.Id);
            if (create ? old is not null || _executions.Values.Any(e => e.SessionId == execution.SessionId && e.AgentRunId == execution.AgentRunId && e.ToolCallId == execution.ToolCallId)
                : old is null || old.SessionId != execution.SessionId || old.Generation != execution.Generation || old.ActionHash != execution.ActionHash)
                throw AgentCoreErrors.Conflict("Sandbox operation receipt changed.");
            _executions[execution.Id] = execution;
        } return ValueTask.CompletedTask;
    }
}
