using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    internal sealed partial class Fixture
    {
        internal void AddBuiltInFixture(AgentDefinition definition) => _builtIns = new ExplicitDefinition(BuiltIns, definition);
        private sealed class ExplicitDefinition(IBuiltInAgentDefinitionStore source, AgentDefinition definition) : IBuiltInAgentDefinitionStore
        {
            public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) =>
                [.. await source.ListAsync(ct), definition];
            public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) =>
                id == definition.Id ? ValueTask.FromResult<AgentDefinition?>(version is null || version == definition.Version ? definition : null) : source.GetAsync(id, version, ct);
        }
    }
}
