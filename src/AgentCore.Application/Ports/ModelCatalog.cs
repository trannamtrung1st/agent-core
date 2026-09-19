using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public enum ModelPurpose
{
    Conversation,
    Initiative,
    CompletionEvaluation
}

public sealed record ModelDescriptor(
    string Key,
    string DisplayName,
    string ProviderAlias,
    string ModelId,
    bool Tools,
    bool Vision,
    bool StructuredOutput,
    bool Reasoning,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? ContextCategory = null,
    string? CostCategory = null);

public interface IModelCatalog
{
    string DefaultKey { get; }

    IReadOnlyList<ModelDescriptor> Models { get; }

    ModelDescriptor Default { get; }

    ModelDescriptor? Get(string key);
}

public interface ILanguageModelResolver
{
    ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose);
}

public sealed class StaticLanguageModelResolver(ILanguageModel model) : ILanguageModelResolver
{
    public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose)
    {
        _ = selection;
        _ = purpose;
        return model;
    }
}
