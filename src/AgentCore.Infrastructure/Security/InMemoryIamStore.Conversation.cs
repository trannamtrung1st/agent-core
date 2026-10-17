using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantConversationStore
{
    public ValueTask<TenantConversationPage?> ReadTenantConversationAsync(Guid tenant, Guid session, long? after, long? before,
        int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        TenantConversationRules.RequirePaging(after, before, limit);
        lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantConversation(tenant, session, after, before, limit));
    }
}
