using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class DefinitionDraftResourceValidation
{
    internal static IEnumerable<DefinitionValidationFinding> CollectFindings(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<AgentDefinitionDraftResource> draftResources)
    {
        var environment = candidate.Environment;
        if (environment is null)
        {
            yield break;
        }

        var resourcesByPath = draftResources
            .GroupBy(item => DefinitionPublicationResourceReader.NormalizeLogicalPath(item.LogicalPath), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        for (var index = 0; index < environment.KnowledgeList.Count; index++)
        {
            var source = environment.KnowledgeList[index];
            var expectedPath = DefinitionPublicationResourceReader.NormalizeLogicalPath($"knowledge/{source.Identity}");
            if (!resourcesByPath.TryGetValue(expectedPath, out var resource)
                || resource.Kind != AgentDefinitionResourceKind.Knowledge)
            {
                yield return new DefinitionValidationFinding(
                    $"environment.knowledgeSources[{index}].identity",
                    "missing_knowledge_resource",
                    $"Draft is missing a Knowledge resource at '{expectedPath}'.",
                    DefinitionValidationSeverity.Blocking);
                continue;
            }

            if (!DefinitionResourcePolicies.IsTextualKnowledgeMediaType(resource.MediaType))
            {
                yield return new DefinitionValidationFinding(
                    $"environment.knowledgeSources[{index}].identity",
                    "non_textual_knowledge_resource",
                    $"Knowledge resource at '{expectedPath}' must use text/plain, text/markdown, or application/json.",
                    DefinitionValidationSeverity.Blocking);
            }
        }

        var templateId = environment.WorkspacePolicy.TemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            yield break;
        }

        var templatePrefix = DefinitionPublicationResourceReader.NormalizeLogicalPath(templateId) + "/";
        if (!draftResources.Any(item =>
                item.Kind == AgentDefinitionResourceKind.Template
                && DefinitionPublicationResourceReader.NormalizeLogicalPath(item.LogicalPath)
                    .StartsWith(templatePrefix, StringComparison.Ordinal)))
        {
            yield return new DefinitionValidationFinding(
                "environment.workspace.templateId",
                "missing_workspace_template",
                $"Draft is missing Template resources under '{templateId}/'.",
                DefinitionValidationSeverity.Blocking);
        }
    }
}
