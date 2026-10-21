using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Current presentation facts only. No role/permission snapshot is persisted or accepted as effect authority.
public interface ITenantRunContext
{
    // Projection may refresh an owned Profile witness before the ordinary source/effect fence.
    ValueTask<AgentCore.Domain.Definitions.AgentDefinition?> CurrentProjectionDefinitionAsync(AgentRun run, CancellationToken ct = default);
    ValueTask<AgentRunContinuitySettings?> CurrentContinuitySettingsAsync(AgentRun run, CancellationToken ct = default) => ValueTask.FromResult<AgentRunContinuitySettings?>(null);
    ValueTask<TenantRequesterContext> CurrentRequesterAsync(AgentRun run, CancellationToken ct = default);
}
