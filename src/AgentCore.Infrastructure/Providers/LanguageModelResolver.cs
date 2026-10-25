using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Providers;

// Stateless: tenant authority and the admitted binding are explicit for every purpose.
public sealed class LanguageModelResolver(IModelProviderFactory providers) : ILanguageModelResolver
{
    public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose, ModelInvocation? invocation)
    {
        if (invocation?.Binding is not { } binding || selection.Binding is null || !binding.Equals(selection.Binding)
            || invocation.Route != binding.Route) throw new AccessDeniedException("model_context_required");
        binding.RequireIdentity(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId);
        return providers.Create(binding.Route, binding.Capabilities, invocation, purpose);
    }
}
