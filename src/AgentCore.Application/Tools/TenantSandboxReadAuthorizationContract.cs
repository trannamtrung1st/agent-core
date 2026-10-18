using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantSandboxReadIntent(SandboxReadKind Kind, Guid? ExecutionId)
{
    public bool Matches(PreparedTenantOperation operation) => operation.SandboxRead is { } read && read.Kind == Kind
        && (Kind != SandboxReadKind.Process || read.ExecutionId == ExecutionId);
}
public static class TenantSandboxReadAuthorizationContract
{
    public static string Operation(SandboxReadKind kind) => kind switch { SandboxReadKind.Profiles => "inspect_sandbox_profiles",
        SandboxReadKind.Status => "inspect_sandbox_status", SandboxReadKind.Process => "inspect_sandbox_process", _ => throw new ArgumentException("Invalid Sandbox read.") };
    public static IReadOnlyList<SandboxReadKind> Allowed(AgentDefinition definition) => Enum.GetValues<SandboxReadKind>()
        .Where(kind => TenantSandboxInspectionContract.Allows(definition, Security.TenantSandboxReadOperationRules.Tool(kind))).ToArray();
    public static bool IsOperation(string? value) => Enum.GetValues<SandboxReadKind>().Any(k => Operation(k) == value);
    public static JsonNode Schema(SandboxReadKind kind)
    {
        var fields = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Operation(kind)) } };
        var required = new JsonArray("operation");
        if (kind == SandboxReadKind.Process) { fields["executionId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }; required.Add("executionId"); }
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = fields, ["required"] = required };
    }
    public static TenantSandboxReadIntent Parse(JsonElement root)
    {
        var kind = Enum.GetValues<SandboxReadKind>().Single(k => Operation(k) == root.GetProperty("operation").GetString());
        var properties = root.EnumerateObject().ToArray();
        if (kind != SandboxReadKind.Process)
        {
            if (properties.Length != 1 || properties[0].Name != "operation") throw new ArgumentException("This inspection accepts only its operation.");
            return new(kind, null);
        }
        if (properties.Length != 2 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || properties.Any(p => p.Name is not ("operation" or "executionId")) || root.GetProperty("executionId").ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(root.GetProperty("executionId").GetString(), "D", out var id) || id == Guid.Empty)
            throw new ArgumentException("Use one exact Core execution ID.");
        return new(kind, id);
    }
}
