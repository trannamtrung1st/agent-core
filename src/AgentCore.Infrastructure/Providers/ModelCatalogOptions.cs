using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers;

public sealed class ModelCatalogOptions
{
    public string? DefaultKey { get; set; }

    public List<ModelCatalogEntryOptions> Models { get; set; } = [];
}

public sealed class ModelCatalogEntryOptions
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProviderAlias { get; set; } = "primary-llm";
    public string ModelId { get; set; } = "";
    public bool Tools { get; set; }
    public bool Vision { get; set; }
    public bool StructuredOutput { get; set; }
    public bool Reasoning { get; set; }
    public List<string> SupportedReasoningEfforts { get; set; } = [];
    public string? DefaultReasoningEffort { get; set; }
    public string? ContextCategory { get; set; }
    public string? CostCategory { get; set; }
}

public sealed class ConfigurationModelCatalog : IModelCatalog
{
    public ConfigurationModelCatalog(string defaultKey, IReadOnlyList<ModelDescriptor> models)
    {
        if (models.Count == 0)
        {
            throw new InvalidOperationException("The model catalog is empty.");
        }

        Models = models;
        var resolved = string.IsNullOrWhiteSpace(defaultKey) ? models[0].Key : defaultKey.Trim();
        Default = models.FirstOrDefault(model => string.Equals(model.Key, resolved, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Model catalog default '{resolved}' was not found.");
        DefaultKey = Default.Key;
    }

    public string DefaultKey { get; }

    public IReadOnlyList<ModelDescriptor> Models { get; }

    public ModelDescriptor Default { get; }

    public ModelDescriptor? Get(string key) =>
        Models.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.Ordinal));
}
