using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public static class TenantDefinitionChildRules
{
    public static string Fingerprint(TenantDefinitionDraftSnapshot snapshot) => DefinitionDraftConfigurationFingerprint.Compute(snapshot.Draft.Candidate, snapshot.Resources);
    public static void ValidateResourceBatch(Guid draftId, IReadOnlyList<AgentDefinitionDraftResource> current, IReadOnlyList<AgentDefinitionDraftResource> incoming)
    {
        if (incoming.Any(r => r.DraftId != draftId || r.ResourceId == Guid.Empty || !Enum.IsDefined(r.Kind)
            || r.LogicalPath != DefinitionResourcePolicies.NormalizeLogicalPath(r.LogicalPath)
            || r.MediaType != DefinitionResourcePolicies.NormalizeMediaType(r.MediaType))) throw new ArgumentException("Invalid resource binding.");
        DefinitionResourcePolicies.ValidateBatchManifest(current, incoming.Select(r => new PreparedBatchResource(r.ResourceId,
            r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength)).ToArray());
    }
}
