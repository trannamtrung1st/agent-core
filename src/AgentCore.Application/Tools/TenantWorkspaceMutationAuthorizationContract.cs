using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceMutationIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.WorkspaceMutation is { } value && value.ToolName == Tool
        && TenantWorkspaceMutationContract.Parse(value.SessionId, Tool, ArgumentsJson).CanonicalArguments == value.ArgumentsJson;
}

// Closed saved-intent validation for trusted continuation. Model offering is wired separately.
public static class TenantWorkspaceMutationAuthorizationContract
{
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
