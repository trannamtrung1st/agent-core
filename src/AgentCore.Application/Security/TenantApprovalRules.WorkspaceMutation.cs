using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record WorkspaceMutationScopeTarget(string Permission, string Root)
{
    public bool Working => Root == "/working";
};

public static partial class TenantApprovalRules
{
    public static IReadOnlyList<WorkspaceMutationScopeTarget> MutationScopes(TenantWorkspaceMutationArguments args) => args.Targets
        .Select(t => new WorkspaceMutationScopeTarget(t.Permission, TenantWorkspaceRules.LogicalRoot(t.Path)))
        .Distinct().OrderBy(t => t.Permission, StringComparer.Ordinal).ThenBy(t => t.Root, StringComparer.Ordinal).ToArray();

    public static PreparedWorkspaceMutation MutationParameters(Guid session, TenantWorkspaceMutationArguments args, string observation)
    {
        var primary = args.Targets.FirstOrDefault(t => t.Permission == "workspaces.delete");
        if (primary.Path is null) primary = args.Targets.First(t => t.Permission == "workspaces.write");
        return new(session, primary.Path.StartsWith("/working/", StringComparison.Ordinal), args.Tool, args.CanonicalArguments,
            observation, primary.Permission, JsonSerializer.Serialize(MutationScopes(args)));
    }

    public static PreparedTenantOperation PrepareWorkspaceMutation(Guid id, Guid instance, PreparedWorkspaceMutation value, long revision, long personaRevision)
    {
        var args = TenantWorkspaceMutationContract.Parse(value.SessionId, value.ToolName, value.ArgumentsJson);
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || personaRevision < 1 || value.ObservationHash is not { Length: 64 }
            || value.ObservationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || value != MutationParameters(value.SessionId, args, value.ObservationHash)) throw new ArgumentException("Invalid exact workspace mutation.");
        var json = JsonSerializer.Serialize(new { operation = "workspace.mutation", mutation = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, TenantWorkspaceMutationContract.Hash(json), WorkspaceMutation: value);
    }
}
