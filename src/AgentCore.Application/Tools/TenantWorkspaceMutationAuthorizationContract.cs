using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceMutationIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.WorkspaceMutation is { } value && value.ToolName == Tool
        && TenantWorkspaceMutationContract.Parse(value.SessionId, Tool, ArgumentsJson).CanonicalArguments == value.ArgumentsJson;
}

// Closed exact proposal; identity, permission and witnesses are derived by Core.
public static class TenantWorkspaceMutationAuthorizationContract
{
    public static IReadOnlyList<string> Allowed(AgentDefinition definition, AgentContext? context) => context?.ExecutionContext is { OriginalInitiatorPrincipalId: not null,
        Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
        ? new[] { ToolCatalog.WorkspaceWrite, ToolCatalog.WorkspacePatch, ToolCatalog.WorkspaceMkdir, ToolCatalog.WorkspaceCopy, ToolCatalog.WorkspaceMove, ToolCatalog.WorkspaceDelete, ToolCatalog.WorkspaceBatch,
                ToolCatalog.OfficeDocumentCreate, ToolCatalog.OfficeDocumentEdit, ToolCatalog.OfficeSpreadsheetCreate, ToolCatalog.OfficeSpreadsheetEdit }
            .Where(t => TenantWorkspaceMutationContract.Allows(definition, t)).ToArray() : [];
    public static JsonNode Schema(IReadOnlyList<string> tools)
    {
        var branches = new JsonArray();
        foreach (var tool in tools)
            branches.Add(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject {
                ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tool) },
                ["arguments"] = JsonNode.Parse(TenantWorkspaceMutationContract.ForModel(ToolRegistry.Get(tool).ModelDefinition).ParametersJson) },
                ["required"] = new JsonArray("tool", "arguments") });
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject {
            ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mutate_workspace") },
            ["mutation"] = new JsonObject { ["oneOf"] = branches } }, ["required"] = new JsonArray("operation", "mutation") };
    }
    public static TenantWorkspaceMutationIntent Parse(JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "mutation"]);
        if (root.GetProperty("operation").ValueKind != JsonValueKind.String || root.GetProperty("operation").GetString() != "mutate_workspace")
            throw new ArgumentException("Use the exact workspace mutation operation.");
        var mutation = root.GetProperty("mutation"); TenantApprovalToolContract.Require(mutation, ["tool", "arguments"]);
        if (mutation.GetProperty("tool").ValueKind != JsonValueKind.String) throw new ArgumentException("Use an exact workspace mutation tool.");
        var tool = mutation.GetProperty("tool").GetString()!;
        // Session identity is never accepted from a proposal; this sentinel validates argument syntax only.
        var args = TenantWorkspaceMutationContract.Parse(Guid.Parse("00000000-0000-0000-0000-000000000001"), tool, mutation.GetProperty("arguments").GetRawText());
        return new(tool, args.CanonicalArguments);
    }
}
