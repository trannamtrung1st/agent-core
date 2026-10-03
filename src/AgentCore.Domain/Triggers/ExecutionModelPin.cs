using AgentCore.Domain.Work;

namespace AgentCore.Domain.Triggers;

public enum ExecutionModelSource
{
    TriggerOverride = 0,
    UnattendedDefault = 1,
    ConversationDefault = 2
}

public sealed class ExecutionModelPin
{
    public ExecutionModelPin(
        string catalogKey,
        string providerAlias,
        string modelId,
        string? reasoningEffort,
        ExecutionModelSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentException("Execution model source is not valid.", nameof(source));
        }

        CatalogKey = WorkText.RequireToken(catalogKey, WorkLimits.MaxModelFieldCharacters, "Model catalog key");
        ProviderAlias = WorkText.RequireToken(providerAlias, WorkLimits.MaxModelFieldCharacters, "Model provider");
        ModelId = WorkText.RequireToken(modelId, WorkLimits.MaxModelFieldCharacters, "Model");
        ReasoningEffort = reasoningEffort is null
            ? null
            : WorkText.RequireToken(reasoningEffort, WorkLimits.MaxReasoningEffortCharacters, "Reasoning effort");
        Source = source;
    }

    public string CatalogKey { get; }

    public string ProviderAlias { get; }

    public string ModelId { get; }

    public string? ReasoningEffort { get; }

    public ExecutionModelSource Source { get; }

    public static string ToWire(ExecutionModelSource source) =>
        source switch
        {
            ExecutionModelSource.TriggerOverride => "triggerOverride",
            ExecutionModelSource.UnattendedDefault => "unattendedDefault",
            ExecutionModelSource.ConversationDefault => "conversationDefault",
            _ => "conversationDefault"
        };
}
