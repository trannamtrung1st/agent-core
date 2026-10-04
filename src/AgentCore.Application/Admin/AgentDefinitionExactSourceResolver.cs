using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record AgentDefinitionExactSource(AgentDefinition Definition, DefinitionDraftSourceKind Kind);

/// <summary>Shared exact-version precedence for runtime resolution and Admin/harness forks.</summary>
public static class AgentDefinitionExactSourceResolver
{
    public static async ValueTask<AgentDefinitionExactSource?> ResolveAsync(
        IBuiltInAgentDefinitionStore builtIns, IAgentDefinitionAdminStore admin,
        string definitionId, int version, CancellationToken ct = default)
    {
        var builtIn = await builtIns.GetAsync(definitionId, version, ct).ConfigureAwait(false);
        if (builtIn is not null)
            return new(builtIn, DefinitionDraftSourceKind.ForkBuiltIn);

        var durable = await admin.GetPublicationAsync(definitionId, version, ct).ConfigureAwait(false);
        return durable is null ? null : new(durable.Payload, DefinitionDraftSourceKind.ForkDurable);
    }
}
