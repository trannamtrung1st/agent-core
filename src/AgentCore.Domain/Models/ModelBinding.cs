namespace AgentCore.Domain.Models;

// Captured nonsecret configuration travels with the admitted work and historical provenance.
public sealed class ModelBinding : IEquatable<ModelBinding>
{
    public ModelBinding(ModelRoutePin route, ModelCapabilityProfile capabilities, string modelLabel, string connectionLabel)
    {
        ArgumentNullException.ThrowIfNull(route);
        ModelManagementRules.RequireTransport(route.Kind, route.Transport);
        if (route.TenantId == Guid.Empty || route.OfferingId == Guid.Empty || route.ConnectionId == Guid.Empty
            || route.OfferingRevision < 1 || route.ConnectionRevision < 1 || route.CredentialId == Guid.Empty
            || route.CredentialId is null && (route.CredentialRevision is not null || route.SecretRevision is not null)
            || route.CredentialId is not null && (route.CredentialRevision is not > 0 || route.SecretRevision is not >= 0)
            || route.CredentialId is null && route.Kind != ModelProviderKind.OpenAICompatible
            || route.ExternalModelId != ModelManagementRules.ExternalId(route.ExternalModelId))
            throw new ArgumentException("An exact owned model route and credential epoch are required.");
        Route = route; Capabilities = ModelManagementRules.Freeze(capabilities);
        ModelLabel = ModelManagementRules.Label(modelLabel); ConnectionLabel = ModelManagementRules.Label(connectionLabel);
    }
    public ModelRoutePin Route { get; }
    public ModelCapabilityProfile Capabilities { get; }
    public string ModelLabel { get; }
    public string ConnectionLabel { get; }

    public bool Equals(ModelBinding? other) => other is not null && Route == other.Route
        && ModelLabel == other.ModelLabel && ConnectionLabel == other.ConnectionLabel
        && Capabilities.Tools == other.Capabilities.Tools && Capabilities.Vision == other.Capabilities.Vision
        && Capabilities.Hierarchy == other.Capabilities.Hierarchy && Capabilities.SemanticMode == other.Capabilities.SemanticMode
        && Capabilities.StructuredOutput == other.Capabilities.StructuredOutput && Capabilities.ForcedToolChoice == other.Capabilities.ForcedToolChoice
        && Capabilities.DefaultReasoningEffort == other.Capabilities.DefaultReasoningEffort && Capabilities.Source == other.Capabilities.Source
        && Capabilities.VerifiedAt == other.Capabilities.VerifiedAt && Capabilities.ReasoningEfforts.SequenceEqual(other.Capabilities.ReasoningEfforts, StringComparer.Ordinal);
    public override bool Equals(object? obj) => obj is ModelBinding other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Route); hash.Add(ModelLabel); hash.Add(ConnectionLabel);
        hash.Add(Capabilities.Tools); hash.Add(Capabilities.Vision); hash.Add(Capabilities.Hierarchy); hash.Add(Capabilities.SemanticMode);
        hash.Add(Capabilities.StructuredOutput); hash.Add(Capabilities.ForcedToolChoice); hash.Add(Capabilities.DefaultReasoningEffort);
        hash.Add(Capabilities.Source); hash.Add(Capabilities.VerifiedAt);
        foreach (var effort in Capabilities.ReasoningEfforts) hash.Add(effort, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public void RequireIdentity(string offeringId, string connectionId, string externalModelId)
    {
        if (offeringId != Route.OfferingId.ToString("D") || connectionId != Route.ConnectionId.ToString("D") || externalModelId != Route.ExternalModelId)
            throw new ArgumentException("Model identity does not match the captured route.");
    }
}
