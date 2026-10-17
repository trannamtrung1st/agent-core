using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Content reads are separate from claim-bound Runtime reads and metadata inventory.
public sealed record TenantConversationPage(TenantSessionRoot Owner, long SessionRevision, ConversationHistoryPage History);
public interface ITenantConversationStore
{
    ValueTask<TenantConversationPage?> ReadTenantConversationAsync(Guid tenant, Guid session, long? after, long? before,
        int limit, CancellationToken ct = default);
}
