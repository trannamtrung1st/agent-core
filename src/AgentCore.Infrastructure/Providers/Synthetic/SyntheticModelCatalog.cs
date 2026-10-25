using AgentCore.Application.Ports;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Stable, explicit offline identities. They are never persisted provider registrations
// and Real composition never creates or resolves them.
public static class SyntheticModelCatalog
{
    public const string ScriptedAlphaOfferingId = "dca30000-0000-4000-8000-000000000001";
    public const string ScriptedBetaOfferingId = "dca30000-0000-4000-8000-000000000002";
    public const string ScriptedVisionOfferingId = "dca30000-0000-4000-8000-000000000003";
    public const string ConnectionId = "dca20000-0000-4000-8000-000000000001";
    internal static readonly Guid StandaloneTenantId = Guid.Parse("dca00000-0000-4000-8000-000000000001");

    public static IModelCatalog Create() => Create(StandaloneTenantId);
    public static IModelCatalog Create(Guid tenantId) => new ConfigurationModelCatalog(ScriptedAlphaOfferingId,
        [Descriptor(tenantId, ScriptedAlphaOfferingId, "Scripted Alpha", vision: false, structured: false, ["low", "medium", "high"], "low"),
         Descriptor(tenantId, ScriptedBetaOfferingId, "Scripted Beta", vision: false, structured: true, [], null),
         Descriptor(tenantId, ScriptedVisionOfferingId, "Scripted Vision", vision: true, structured: false, [], null)]);

    internal static bool IsExactFixture(ModelBinding binding)
    {
        var descriptor = Create(binding.Route.TenantId).Get(binding.Route.OfferingId.ToString("D"));
        return descriptor?.Route is { } route && descriptor.CapabilityProfile is { } profile
            && binding.Equals(new ModelBinding(route, profile, descriptor.DisplayName, descriptor.ConnectionLabel!));
    }

    private static ModelDescriptor Descriptor(Guid tenant, string offeringId, string label, bool vision, bool structured,
        IReadOnlyList<string> efforts, string? effort) => ModelDescriptor.FromBinding(new ModelBinding(
            new(tenant, Guid.Parse(offeringId), 1, Guid.Parse(ConnectionId), 1, null, null, null,
                ModelProviderKind.OpenAICompatible, offeringId, ModelTransport.ChatCompletions),
            new(true, vision, ModelInstructionHierarchy.NativeDeveloper,
                structured ? ModelSemanticMode.NativeStructuredOutput : ModelSemanticMode.ResponseFunction,
                structured, true, efforts, effort, ModelMetadataSource.SyntheticFixture), label, "Offline Synthetic"));
}
