using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public static class RolePermissions
{
    private static readonly HashSet<string> ForbiddenTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "process",
        "shell",
        "bash",
        "cmd",
        "powershell",
        "exec"
    };

    public static bool AllowsTool(AgentDefinition definition, string tool)
    {
        if (string.IsNullOrWhiteSpace(tool) || ForbiddenTools.Contains(tool.Trim()))
        {
            return false;
        }

        var normalized = tool.Trim();
        if (definition.ExecutionPolicy?.Capabilities is not null) return definition.ExecutionPolicy.ToolList.Contains(normalized, StringComparer.Ordinal);
        if (string.Equals(normalized, ToolCatalog.AttachmentsRead, StringComparison.Ordinal)
            || string.Equals(normalized, ToolCatalog.SkillsLoad, StringComparison.Ordinal)
            || string.Equals(normalized, ToolCatalog.AppMessageSend, StringComparison.Ordinal)
            || string.Equals(normalized, ToolCatalog.WorkComplete, StringComparison.Ordinal))
        {
            return true;
        }

        return DefinitionExecutionPolicies.Of(definition).ToolList.Contains(normalized, StringComparer.Ordinal);
    }

    public static void EnsureToolAllowed(AgentDefinition definition, string tool)
    {
        if (!AllowsTool(definition, tool))
        {
            throw AgentCoreErrors.Forbidden("Tool is not permitted for this role.");
        }
    }

    public static bool AllowsLogicalPath(string path, Guid sessionId)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path.Contains('\\') || WorkspaceLogicalPath.HasParentSegment(path)) return false;
        if (path == "/") return true;
        return new[] { "/home", "/private", "/working", "/agent", "/attachments" }
            .Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    public static void EnsureLogicalPathAllowed(string path, Guid sessionId)
    {
        if (!AllowsLogicalPath(path, sessionId))
        {
            throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
        }
    }
}

public sealed record KnowledgeDocument(
    string Identity,
    string Title,
    string Citation,
    string Content,
    string? SourceVersion,
    DateTimeOffset RetrievedAt);

public interface IApprovedKnowledgeCatalog
{
    ValueTask<string?> ReadContentAsync(string identity, CancellationToken cancellationToken = default);
}

public sealed class RoleKnowledgeService(IRoleKnowledgeContentResolver contentResolver, TimeProvider time)
{
    public static RoleKnowledgeService FromApprovedCatalog(IApprovedKnowledgeCatalog catalog, TimeProvider time) =>
        new(new FileOnlyRoleKnowledgeContentResolver(catalog), time);

    public async ValueTask<KnowledgeDocument> RetrieveAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default)
    {
        RolePermissions.EnsureToolAllowed(definition, "knowledge.retrieve");
        var source = DefinitionExecutionPolicies.Of(definition).KnowledgeList
            .FirstOrDefault(item => string.Equals(item.Identity, identity, StringComparison.Ordinal))
            ?? throw AgentCoreErrors.Forbidden("Knowledge identity is not approved for this role.");
        var content = await contentResolver.ReadContentAsync(definition, identity, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Approved knowledge content was not found.");
        return new KnowledgeDocument(
            source.Identity,
            source.Title,
            source.Citation,
            content,
            SourceVersion: null,
            time.GetUtcNow());
    }
}
