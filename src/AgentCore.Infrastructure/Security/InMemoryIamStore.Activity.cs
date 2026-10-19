using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantRunPage> ListTenantSessionRunsAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var root = sessionBacking.ReadTenantSession(tenant, session) ?? throw new AccessDeniedException("resource_unavailable");
            var rows = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == tenant && r.SessionId == session
                && r.AgentInstanceId == root.Owner.AgentInstanceId && r.Owner.ExecutorPrincipalId == root.Owner.ExecutorPrincipalId
                && (allowed is null || allowed.Contains(r.AgentRunId))).OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.AgentRunId).ToArray();
            return ValueTask.FromResult(new TenantRunPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }
    public ValueTask<CompletionInboxItem?> FindTenantCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(owner);
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (CompletionSource(owner, childRunId) is null || !sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(childRunId, out var row))
                return ValueTask.FromResult<CompletionInboxItem?>(null);
            var item = CompletionInboxMapping.Read(row);
            return ValueTask.FromResult(item.Owner == owner ? item : null);
        }
    }
}
