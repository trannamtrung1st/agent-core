using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers;

public sealed class ConfigurationModelCatalog : IModelCatalog
{
    public ConfigurationModelCatalog(string defaultKey, IReadOnlyList<ModelDescriptor> models)
    {
        if (models.Count == 0)
        {
            throw new InvalidOperationException("The model catalog is empty.");
        }

        Models = models;
        var resolved = string.IsNullOrWhiteSpace(defaultKey) ? models[0].OfferingId : defaultKey.Trim();
        Default = models.FirstOrDefault(model => string.Equals(model.OfferingId, resolved, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Model catalog default '{resolved}' was not found.");
        DefaultOfferingId = Default.OfferingId;
    }

    public string DefaultOfferingId { get; }

    public IReadOnlyList<ModelDescriptor> Models { get; }

    public ModelDescriptor Default { get; }

    public ModelDescriptor? Get(string key) =>
        Models.FirstOrDefault(model => string.Equals(model.OfferingId, key, StringComparison.Ordinal));
}
