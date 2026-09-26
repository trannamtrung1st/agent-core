using AgentCore.Application.Admin;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record DefinitionDraftSyntheticResourceSnapshot(
    string LogicalPath,
    AgentDefinitionResourceKind Kind,
    string ContentSha256,
    ReadOnlyMemory<byte> Content);

public sealed record DefinitionDraftSyntheticToolObservation(
    string ToolName,
    ToolPolicyDecision PolicyDecision);

public sealed record DefinitionDraftSyntheticBehaviorResult(
    string RuntimeKind,
    string ModelSelection,
    bool PromptIncludedInRequest,
    IReadOnlyList<DefinitionDraftSyntheticToolObservation> ToolObservations,
    string? AssistantText);

public interface IDefinitionDraftSyntheticBehaviorEvaluator
{
    ValueTask<DefinitionDraftSyntheticBehaviorResult> EvaluateAsync(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resources,
        DefinitionEvaluationScenario scenario,
        CancellationToken cancellationToken = default);
}
