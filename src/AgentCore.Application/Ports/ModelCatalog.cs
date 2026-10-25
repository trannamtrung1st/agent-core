using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Application.Models;

namespace AgentCore.Application.Ports;

public enum ModelPurpose
{
    Conversation,
    Initiative,
    CompletionEvaluation,
    TriggerAuthorization
}

public enum ModelInferenceTransport { ChatCompletions, Responses, AnthropicMessages, GeminiContent }

public sealed record ModelDescriptor(
    string OfferingId,
    string DisplayName,
    string ConnectionId,
    string ExternalModelId,
    bool Tools,
    bool Vision,
    bool StructuredOutput,
    bool Reasoning,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? ContextCategory = null,
    string? CostCategory = null,
    ModelInferenceTransport Transport = ModelInferenceTransport.ChatCompletions,
    bool PreferResponseFunction = false, bool DeveloperRoleSupport = false,
    ModelRoutePin? Route = null, ModelCapabilityProfile? CapabilityProfile = null, string? ConnectionLabel = null)
{
    public static ModelDescriptor FromBinding(ModelBinding binding)
    {
        var r = binding.Route; var p = binding.Capabilities;
        return new(r.OfferingId.ToString("D"), binding.ModelLabel, r.ConnectionId.ToString("D"), r.ExternalModelId,
            p.Tools, p.Vision, p.StructuredOutput, p.ReasoningEfforts.Count > 0, p.ReasoningEfforts, p.DefaultReasoningEffort,
            Transport: r.Transport switch
            {
                ModelTransport.Responses => ModelInferenceTransport.Responses,
                ModelTransport.AnthropicMessages => ModelInferenceTransport.AnthropicMessages,
                ModelTransport.GeminiContent => ModelInferenceTransport.GeminiContent,
                _ => ModelInferenceTransport.ChatCompletions
            }, PreferResponseFunction: p.SemanticMode == ModelSemanticMode.ResponseFunction,
            DeveloperRoleSupport: p.Hierarchy == ModelInstructionHierarchy.NativeDeveloper,
            Route: r, CapabilityProfile: p, ConnectionLabel: binding.ConnectionLabel);
    }
    public bool SupportsInstructionHierarchy => CapabilityProfile is null && Route is null ? DeveloperRoleSupport
        : CapabilityProfile is { } profile && Route is { } route && (profile.Source == ModelMetadataSource.SyntheticFixture
            ? profile.Hierarchy == ModelInstructionHierarchy.NativeDeveloper && route.Kind == ModelProviderKind.OpenAICompatible
                && route.CredentialId is null && profile.Tools
            : ModelRouteRules.Supports(profile, route.Kind, ModelPurpose.Conversation));
}

// The caller has already admitted this tenant operation through its existing authority owner.
// Reading a projection never grants inference authority or replaces Run admission.
public interface ITenantModelCatalogReader
{
    ValueTask<IModelCatalog> ReadAsync(TenantSecurityState authorizedState, CancellationToken ct = default);
}

public interface IModelCatalog
{
    string DefaultOfferingId { get; }

    ModelSelectionSource DefaultSelectionSource => ModelSelectionSource.TenantDefault;

    string? DefaultReasoningEffort => Default.DefaultReasoningEffort;

    IReadOnlyList<ModelDescriptor> Models { get; }

    ModelDescriptor Default { get; }

    ModelDescriptor? Get(string key);
}

public interface ILanguageModelResolver
{
    ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose, ModelInvocation? invocation);
}

public sealed class StaticLanguageModelResolver(ILanguageModel model) : ILanguageModelResolver
{
    public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose, ModelInvocation? invocation)
    {
        _ = selection;
        _ = purpose;
        return model;
    }
}
