using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantSandboxControlIntent(SandboxControlKind Kind, Guid? ExecutionId)
{
    public bool Matches(PreparedTenantOperation operation) => operation.SandboxControl is { } control && control.Kind == Kind
        && (Kind == SandboxControlKind.ResetEnvironment || control.ExecutionId == ExecutionId);
}
public static class TenantSandboxControlContract
{
    public static bool Allows(AgentDefinition definition, SandboxControlKind kind) => RolePermissions.AllowsTool(definition,
        kind == SandboxControlKind.StopProcess ? ToolCatalog.SandboxProcessStop : ToolCatalog.SandboxReset);
    public static JsonNode Schema(SandboxControlKind kind) => JsonNode.Parse(kind == SandboxControlKind.StopProcess
        ? """{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["stop_sandbox_process"]},"executionId":{"type":"string","format":"uuid"}},"required":["operation","executionId"]}"""
        : """{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["reset_sandbox_environment"]}},"required":["operation"]}""")!;
    public static TenantSandboxControlIntent Parse(JsonElement value)
    {
        var kind = value.GetProperty("operation").GetString() == "stop_sandbox_process" ? SandboxControlKind.StopProcess : SandboxControlKind.ResetEnvironment;
        var properties = value.EnumerateObject().ToArray();
        if (kind == SandboxControlKind.ResetEnvironment)
        {
            if (properties.Length != 1 || properties[0].Name != "operation") throw new ArgumentException("Reset accepts only its operation.");
            return new(kind, null);
        }
        if (properties.Length != 2 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || properties.Any(p => p.Name is not ("operation" or "executionId"))
            || value.GetProperty("executionId").ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetProperty("executionId").GetString(), "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Stop requires one exact Core execution ID.");
        return new(kind, id);
    }
}
