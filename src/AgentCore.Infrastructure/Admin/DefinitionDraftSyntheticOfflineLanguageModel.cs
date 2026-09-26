using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Admin;

/// <summary>
/// Dedicated offline model for draft behavior evaluation. Never uses profile-bound hosted resolvers.
/// </summary>
public sealed class DefinitionDraftSyntheticOfflineLanguageModel : ILanguageModel
{
    private readonly ScriptedLanguageModel _scripted = new();

    public ModelCapabilities Capabilities => _scripted.Capabilities;

    public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default) =>
        _scripted.GenerateAsync(request, cancellationToken);
}
