using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Tests;

internal sealed class UserRetrievalDefinitions(IAgentDefinitionStore inner) : IAgentDefinitionStore
{
    private static AgentDefinition Enable(AgentDefinition d) => d.MemoryPolicy is null ? d
        : d with { MemoryPolicy = d.MemoryPolicy with { UserRetrieval = true } };
    public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => (await inner.ListAsync(ct)).Select(Enable).ToArray();
    public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) =>
        await inner.GetAsync(id, version, ct) is { } d ? Enable(d) : null;
}
