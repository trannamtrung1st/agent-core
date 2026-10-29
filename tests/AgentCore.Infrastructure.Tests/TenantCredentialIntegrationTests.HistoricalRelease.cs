using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    internal sealed partial class Fixture
    {
        // Historical-pin fixtures represent creation before the current product cutover.
        // Current-release creation still uses Agents and enforces the public template rule.
        public TenantAgentService AgentsAtRelease(int version) => new(Iam, (ITenantAgentStore)Iam,
            new HistoricalRelease(BuiltIns, version), Authorizer, Ids, Clock, ownedDefinitions: (ITenantDefinitionStore)Iam);
        internal sealed class HistoricalRelease(IBuiltInAgentDefinitionStore source, int version) : IBuiltInAgentDefinitionStore
        {
            public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) =>
                (await source.ListAsync(ct)).Where(d => d.Id != ManagedGeneralAssistant.DefinitionId || d.Version <= version).ToArray();
            public ValueTask<AgentDefinition?> GetAsync(string id, int? exact = null, CancellationToken ct = default) =>
                source.GetAsync(id, exact ?? (id == ManagedGeneralAssistant.DefinitionId ? version : null), ct);
        }
    }
}
