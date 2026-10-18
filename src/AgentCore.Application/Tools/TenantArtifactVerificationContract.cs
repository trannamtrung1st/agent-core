using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantArtifactVerificationContract
{
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.ArtifactsVerify);
    public static Guid Parse(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || Encoding.UTF8.GetByteCount(arguments) > 512) throw new ArgumentException("Use one exact Artifact ID.");
        using var doc = JsonDocument.Parse(arguments);
        var fields = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().ToArray() : [];
        if (fields.Length != 1 || fields[0].Name != "artifactId" || fields[0].Value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(fields[0].Value.GetString(), out var id) || id == Guid.Empty) throw new ArgumentException("Use one exact Artifact ID.");
        return id;
    }
    public static ModelToolDefinition ForModel() => new(ToolCatalog.ArtifactsVerify,
        "Inspect safe immutable metadata for one actual Artifact in this Session. Requires independent current Agent Session and Artifact read authority and policy. Returns no bytes, workspace path or source Attachment identity, and grants no recipient download access. If review is required, request verify_artifact through approvals.request_authorization with this artifactId as the sole call. A rejected or expired request must not be repeated within this task.",
        """{"type":"object","additionalProperties":false,"properties":{"artifactId":{"type":"string","format":"uuid"}},"required":["artifactId"]}""");
}
