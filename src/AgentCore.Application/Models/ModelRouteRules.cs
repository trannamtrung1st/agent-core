using AgentCore.Application.Ports;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Models;

public static class ModelRouteRules
{
    public static bool Supports(ModelCapabilityProfile profile, ModelProviderKind kind, ModelPurpose purpose)
    {
        var native = kind is ModelProviderKind.Anthropic or ModelProviderKind.Google;
        return Enum.IsDefined(purpose) && profile.Source is not (ModelMetadataSource.Unknown or ModelMetadataSource.SyntheticFixture)
            && profile.Hierarchy == (native ? ModelInstructionHierarchy.OrderedTrustedSections : ModelInstructionHierarchy.NativeDeveloper)
            && (!native || profile.SemanticMode == ModelSemanticMode.NativeStructuredOutput)
            && (profile.SemanticMode == ModelSemanticMode.NativeStructuredOutput ? profile.StructuredOutput : profile.Tools && profile.ForcedToolChoice)
            && (purpose != ModelPurpose.Conversation || profile.Tools);
    }

    public static ModelRoutePin Pin(ModelRouteSnapshot source) => new(source.Connection.TenantId,
        source.Offering.OfferingId, source.Offering.Revision, source.Connection.ConnectionId, source.Connection.Revision,
        source.Credential.CredentialId, source.Credential.Revision, source.Credential.SecretRevision,
        source.Connection.Kind, source.Offering.ExternalModelId, source.Offering.Transport);
}
