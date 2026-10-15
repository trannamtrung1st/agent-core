using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Providers;

public sealed class LanguageModelResolver : ILanguageModelResolver
{
    private readonly IServiceProvider _services;
    private readonly string _profile;
    private readonly LanguageModelProviderOptions _primary;
    private readonly IModelCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, ILanguageModel> _clients = new(StringComparer.Ordinal);

    public LanguageModelResolver(
        IServiceProvider services,
        string profile,
        LanguageModelProviderOptions primary,
        IModelCatalog catalog)
    {
        _services = services;
        _profile = profile;
        _primary = Clone(primary);
        _catalog = catalog;
    }

    public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose)
    {
        _ = purpose;
        var providerAlias = selection.ProviderAlias;
        var modelId = selection.ModelId;
        var descriptor = _catalog.Get(selection.CatalogKey);
        if (descriptor?.DeveloperRoleSupport != true || !Enum.IsDefined(descriptor.Transport))
            throw new InvalidOperationException("Selected model/transport does not support the required separate system and developer roles.");
        if (!string.Equals(descriptor.ProviderAlias, providerAlias, StringComparison.Ordinal)
            || !string.Equals(descriptor.ModelId, modelId, StringComparison.Ordinal))
            throw new InvalidOperationException("Pinned model route no longer matches the catalog's declared hierarchy contract. Restore that route or select a supported model for a new Run.");
        var route = $"{selection.CatalogKey}\u001f{providerAlias}\u001f{modelId}";
        lock (_gate)
        {
            if (_clients.TryGetValue(route, out var cached))
            {
                return cached;
            }

            var created = new SemanticResponseLanguageModel(
                BindCatalogCapabilities(Create(modelId, descriptor), descriptor),
                preferResponseFunction: descriptor?.PreferResponseFunction == true);
            _clients[route] = created;
            return created;
        }
    }

    private ILanguageModel Create(string modelId, ModelDescriptor? descriptor)
    {
        var options = Clone(_primary);
        options.DefaultModel = modelId;
        if (descriptor is not null && string.Equals(descriptor.ModelId, modelId, StringComparison.Ordinal))
        {
            options.Transport = descriptor.Transport;
            options.Tools = descriptor.Tools;
            options.Vision = descriptor.Vision;
            options.StructuredOutput = descriptor.StructuredOutput;
            options.DeveloperRoleSupport = descriptor.DeveloperRoleSupport;
        }

        options.ReasoningEffort = null;
        if (IsScripted(options))
        {
            var registered = _services.GetService<ILanguageModel>();
            if (registered is not null)
            {
                return registered;
            }
        }

        return LanguageModelFactory.Create(_services, _profile, options);
    }

    private static ILanguageModel BindCatalogCapabilities(ILanguageModel model, ModelDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return model;
        }

        var overlay = model.Capabilities with
        {
            Tools = descriptor.Tools,
            Vision = descriptor.Vision,
            StructuredOutput = descriptor.StructuredOutput
        };
        return overlay.Equals(model.Capabilities)
            ? model
            : new CatalogBoundLanguageModel(model, overlay);
    }

    private bool IsScripted(LanguageModelProviderOptions options) =>
        string.Equals(_profile, "Synthetic", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Adapter, "Scripted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Adapter, "Synthetic", StringComparison.OrdinalIgnoreCase);

    private static LanguageModelProviderOptions Clone(LanguageModelProviderOptions source) =>
        new()
        {
            Adapter = source.Adapter,
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey,
            DefaultModel = source.DefaultModel,
            Transport = source.Transport,
            ReasoningEffort = source.ReasoningEffort,
            ReasoningObjectWire = source.ReasoningObjectWire,
            ExcludeVisibleReasoning = source.ExcludeVisibleReasoning,
            MapSeparateReasoningDeltas = source.MapSeparateReasoningDeltas,
            Vision = source.Vision,
            Tools = source.Tools,
            StructuredOutput = source.StructuredOutput,
            DeveloperRoleSupport = source.DeveloperRoleSupport,
            LogProviderErrorMessages = source.LogProviderErrorMessages,
            AdditionalHeaders = new Dictionary<string, string>(source.AdditionalHeaders, StringComparer.Ordinal),
            Timeouts = new ProviderTimeoutOptions
            {
                SetupSeconds = source.Timeouts.SetupSeconds,
                StreamIdleSeconds = source.Timeouts.StreamIdleSeconds,
                TotalSeconds = source.Timeouts.TotalSeconds
            }
        };
}

internal sealed class CatalogBoundLanguageModel(ILanguageModel inner, ModelCapabilities capabilities) : ILanguageModel
{
    public ILanguageModel Inner { get; } = inner;

    public ModelCapabilities Capabilities { get; } = capabilities;

    public ModelRequest PrepareRequest(ModelRequest request) => Inner.PrepareRequest(request);

    public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default) =>
        Inner.GenerateAsync(request, cancellationToken);
}
