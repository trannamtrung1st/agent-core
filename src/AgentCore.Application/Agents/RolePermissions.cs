using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
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

        return RoleEnvironments.Of(definition).ToolList.Contains(tool.Trim(), StringComparer.Ordinal);
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') && !normalized.StartsWith("/agent/", StringComparison.Ordinal)
            && !normalized.StartsWith("/attachments/", StringComparison.Ordinal)
            && !normalized.StartsWith("/workspace/", StringComparison.Ordinal)
            && normalized is not "/" and not "/agent" and not "/attachments" and not "/workspace")
        {
            return false;
        }

        if (normalized.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized) && !normalized.StartsWith('/'))
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains(".git", StringComparison.Ordinal)
            || lower.Contains("src/agentcore", StringComparison.Ordinal)
            || lower.Contains("local/tdp-workspace", StringComparison.Ordinal)
            || lower.Contains(".env", StringComparison.Ordinal)
            || lower.Contains("user-secrets", StringComparison.Ordinal)
            || lower.Contains("appsettings.", StringComparison.Ordinal) && lower.Contains("secret"))
        {
            return false;
        }

        foreach (var token in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(token, out var found) && found != sessionId)
            {
                return false;
            }
        }

        return true;
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

public sealed class RoleKnowledgeService(IApprovedKnowledgeCatalog catalog, TimeProvider time)
{
    public async ValueTask<KnowledgeDocument> RetrieveAsync(
        AgentDefinition definition,
        string identity,
        CancellationToken cancellationToken = default)
    {
        RolePermissions.EnsureToolAllowed(definition, "knowledge.retrieve");
        var source = RoleEnvironments.Of(definition).KnowledgeList
            .FirstOrDefault(item => string.Equals(item.Identity, identity, StringComparison.Ordinal))
            ?? throw AgentCoreErrors.Forbidden("Knowledge identity is not approved for this role.");
        var content = await catalog.ReadContentAsync(identity, cancellationToken).ConfigureAwait(false)
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
