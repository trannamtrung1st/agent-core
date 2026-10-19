using System.Text.Json.Nodes;

namespace AgentCore.Application.Tools;

public static class TenantAttachmentReadAuthorizationContract
{
    public static JsonObject Schema() => new() { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("read_attachment") },
            ["read"] = JsonNode.Parse(ToolRegistry.Get(ToolCatalog.AttachmentsRead).ModelDefinition.ParametersJson) },
        ["required"] = new JsonArray("operation", "read") };
}
