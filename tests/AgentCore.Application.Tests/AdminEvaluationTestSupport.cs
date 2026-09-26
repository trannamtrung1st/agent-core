using AgentCore.Application.Admin;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Admin;

namespace AgentCore.Application.Tests;

internal static class AdminEvaluationTestSupport
{
    internal static AgentDefinitionDraftSyntheticEvaluationRunner CreateRunner() =>
        new(
            new SyntheticDefinitionDraftBehaviorEvaluator(
                new DefinitionDraftSyntheticOfflineLanguageModel(),
                TestModelCatalogs.Synthetic(),
                ToolConfigurationGates.AllowAll),
            ToolConfigurationGates.AllowAll);
}
