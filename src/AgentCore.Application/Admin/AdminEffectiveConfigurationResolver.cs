using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class AdminEffectiveConfigurationResolver
{
    public static AdminEffectiveConfiguration Resolve(
        AgentInstance instance,
        AgentDefinition definition,
        IModelCatalog catalog,
        IToolConfigurationGate configurationGate,
        string definitionSource,
        string definitionStatus) =>
        EffectiveConfigurationComposer.ComposeAdmin(
            instance,
            definition,
            catalog,
            configurationGate,
            definitionSource,
            definitionStatus);
}
