using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using System.Runtime.CompilerServices;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Explicit offline profile only. No HTTP provider construction, secret lookup or Real recovery branch.
public sealed class SyntheticLanguageModelResolver(ILanguageModel fixture, ITenantModelRouteAuthority routes) : ILanguageModelResolver
{
    public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose, ModelInvocation? invocation)
    {
        var binding = selection.Binding ?? throw new AccessDeniedException("model_context_required");
        binding.RequireIdentity(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId);
        if (invocation is null && binding.Route.TenantId == SyntheticModelCatalog.StandaloneTenantId && SyntheticModelCatalog.IsExactFixture(binding))
            return new SemanticResponseLanguageModel(new SyntheticCatalogLanguageModel(fixture, fixture.Capabilities with
                { Tools = binding.Capabilities.Tools, Vision = binding.Capabilities.Vision, StructuredOutput = binding.Capabilities.StructuredOutput }),
                binding.Capabilities.SemanticMode == ModelSemanticMode.ResponseFunction);
        if (invocation?.Binding is null || !binding.Equals(invocation.Binding) || invocation.Route != binding.Route
            || invocation.Context.TenantId != binding.Route.TenantId)
            throw new AccessDeniedException("model_context_required");
        return new SemanticResponseLanguageModel(new BoundOfflineModel(fixture, binding, invocation, routes, purpose),
            binding.Capabilities.SemanticMode == ModelSemanticMode.ResponseFunction);
    }

    // Exercise the admitted tenant route and its normal authority fences without
    // constructing a provider transport or unprotecting a credential in Synthetic.
    private sealed class BoundOfflineModel(ILanguageModel fixture, ModelBinding binding, ModelInvocation invocation,
        ITenantModelRouteAuthority routes, ModelPurpose purpose) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = fixture.Capabilities with
        { Tools = binding.Capabilities.Tools, Vision = binding.Capabilities.Vision, StructuredOutput = binding.Capabilities.StructuredOutput };
        public ModelInferenceTransport? InferenceTransport => ModelDescriptor.FromBinding(binding).Transport;
        private async ValueTask<ProviderFailure?> Check(CancellationToken ct)
        {
            try
            {
                if (binding.Capabilities.Source == ModelMetadataSource.SyntheticFixture)
                {
                    if (!SyntheticModelCatalog.IsExactFixture(binding))
                        return new(ProviderErrorCode.InvalidRequest, "The selected offline fixture binding is invalid.");
                    await invocation.RequireCurrentAsync(ct).ConfigureAwait(false);
                    return null;
                }
                var current = await routes.RequireAsync(binding.Route, invocation, purpose, ct).ConfigureAwait(false);
                if (!binding.Equals(new ModelBinding(binding.Route, current.Offering.Capabilities, binding.ModelLabel, binding.ConnectionLabel)))
                    return new(ProviderErrorCode.InvalidRequest, "The admitted model capability profile changed. Start a new Run.");
                return null;
            }
            catch (AccessDeniedException) { return new(ProviderErrorCode.Authentication, "The admitted model route is no longer authorized."); }
            catch (SecurityConflictException) { return new(ProviderErrorCode.Authentication, "The admitted model route changed."); }
            catch (AgentCoreException) { return new(ProviderErrorCode.Unavailable, "The admitted model route is unavailable."); }
        }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (await Check(cancellationToken).ConfigureAwait(false) is { } initial)
            { yield return new ModelFailed(initial); yield break; }
            if (!Enum.IsDefined(request.ToolChoice)
                || request.ToolChoice != ModelToolChoice.Auto && !binding.Capabilities.ForcedToolChoice)
            {
                yield return new ModelFailed(new(ProviderErrorCode.UnsupportedCapability, "The admitted model does not support the requested tool choice."));
                yield break;
            }
            await foreach (var item in fixture.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (await Check(cancellationToken).ConfigureAwait(false) is { } failure)
                { yield return new ModelFailed(failure); yield break; }
                yield return item;
            }
        }
    }
}

internal sealed class SyntheticCatalogLanguageModel(ILanguageModel inner, ModelCapabilities capabilities) : ILanguageModel
{
    public ILanguageModel Inner { get; } = inner;
    public ModelCapabilities Capabilities { get; } = capabilities;
    public ModelInferenceTransport? InferenceTransport => Inner.InferenceTransport;
    public ModelRequest PrepareRequest(ModelRequest request) => Inner.PrepareRequest(request);
    public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) => Inner.GenerateAsync(request, cancellationToken);
}
