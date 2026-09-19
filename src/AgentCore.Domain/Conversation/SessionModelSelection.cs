namespace AgentCore.Domain.Conversation;

public enum ModelSelectionSource
{
    SystemDefault,
    AgentDefault,
    User,
    Host
}

public sealed record SessionModelSelection(
    string CatalogKey,
    string ProviderAlias,
    string ModelId,
    ModelSelectionSource SelectionSource,
    string? ReasoningEffort);

public sealed record ModelGenerationProvenance(
    string CatalogKey,
    string ProviderAlias,
    string ModelId,
    string? ReasoningEffort);
