using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Current presentation facts only. No role/permission snapshot is persisted or accepted as effect authority.
public interface ITenantRunContext
{
    ValueTask<TenantRequesterContext> CurrentRequesterAsync(AgentRun run, CancellationToken ct = default);
}
