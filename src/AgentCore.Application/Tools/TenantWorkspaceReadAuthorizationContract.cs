using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceReadIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.WorkspaceRead is { } value && value.ToolName == Tool && value.ArgumentsJson == ArgumentsJson
        || operation.AttachmentRead is not null && TenantAttachmentVirtualReadContract.Matches(Tool, ArgumentsJson, operation);
}
public static class TenantWorkspaceReadAuthorizationContract
{
    public static IReadOnlyList<string> Allowed(AgentDefinition definition) => new[] { ToolCatalog.KnowledgeRetrieve, ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceSearch, ToolCatalog.OfficeDocumentInspect, ToolCatalog.OfficeSpreadsheetInspect }.Where(t => TenantWorkspaceReadContract.Allows(definition, t)).ToArray();
    public static JsonNode Schema(IReadOnlyList<string> allowed)
    {
        var branches = new JsonArray();
        foreach (var tool in allowed)
        {
            var branch = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tool) },
                    ["arguments"] = JsonNode.Parse(TenantWorkspaceReadContract.ForModel(ToolRegistry.Get(tool).ModelDefinition).ParametersJson) },
                ["required"] = new JsonArray("tool", "arguments") };
            branches.Add(branch);
        }
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("inspect_workspace") },
                ["read"] = new JsonObject { ["oneOf"] = branches } }, ["required"] = new JsonArray("operation", "read") };
    }
    public static TenantWorkspaceReadIntent Parse(JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "read"]); var read = root.GetProperty("read");
        TenantApprovalToolContract.Require(read, ["tool", "arguments"]);
        if (read.GetProperty("tool").ValueKind != JsonValueKind.String) throw new ArgumentException("Use an exact workspace read.");
        var tool = read.GetProperty("tool").GetString()!; var args = TenantWorkspaceReadContract.Parse(tool, read.GetProperty("arguments").GetRawText());
        return new(tool, TenantWorkspaceReadContract.Canonical(tool, args));
    }
}
