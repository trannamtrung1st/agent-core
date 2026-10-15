using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace
{
    private async ValueTask<IReadOnlyList<WorkspaceNode>> ListAgentAsync(
        AgentDefinition definition,
        string path,
        CancellationToken cancellationToken)
    {
        if (definition.ExecutionResources is { } manifest && (path == "/agent/instance" || path == "/agent/instance/resources" || path.StartsWith("/agent/instance/resources/", StringComparison.Ordinal)
            || path == "/agent/resources" || path.StartsWith("/agent/resources/", StringComparison.Ordinal)))
            return ListPinnedResources(manifest, path);
        if (path.StartsWith("/agent/resources", StringComparison.Ordinal))
        {
            return await ListAgentPublicationResourcesAsync(definition, path, cancellationToken).ConfigureAwait(false);
        }

        var env = RoleEnvironments.Of(definition);
        if (path is "/agent")
        {
            var nodes = new List<WorkspaceNode> { new("/agent/definition.json", false, 0, false) };
            if (definition.ExecutionResources?.Any(r => r.Key.StartsWith("instance:", StringComparison.Ordinal)) == true)
                nodes.Add(new("/agent/instance", true, 0, false));
            if (env.HarnessList.Count > 0)
            {
                nodes.Add(new WorkspaceNode("/agent/harness", true, 0, false));
            }

            if (env.KnowledgeList.Count > 0)
            {
                nodes.Add(new WorkspaceNode("/agent/knowledge", true, 0, false));
            }

            if (_publicationResources is not null)
            {
                var published = await _publicationResources.ListAsync(definition.Id, definition.Version, cancellationToken)
                    .ConfigureAwait(false);
                if (definition.ExecutionResources is { } resolved ? resolved.Any(r => r.Key.StartsWith("definition:", StringComparison.Ordinal)) : published.Count > 0)
                {
                    nodes.Add(new WorkspaceNode("/agent/resources", true, 0, false));
                }
            }

            return nodes;
        }

        if (path == "/agent/definition.json")
        {
            var content = await ReadAgentAsync(definition, path, cancellationToken).ConfigureAwait(false);
            return [new WorkspaceNode(path, false, content.Bytes.Length, false)];
        }

        if (path is "/agent/harness")
        {
            return env.HarnessList
                .Select(name => new WorkspaceNode($"/agent/harness/{name}", false, 0, false))
                .ToArray();
        }

        if (path is "/agent/knowledge")
        {
            return env.KnowledgeList
                .Select(item => new WorkspaceNode($"/agent/knowledge/{item.Identity}", false, 0, false))
                .ToArray();
        }

        var leaf = await ReadAgentAsync(definition, path, cancellationToken).ConfigureAwait(false);
        return [new WorkspaceNode(path, false, leaf.Bytes.Length, false)];
    }

    private async ValueTask<WorkspaceContent> ReadAgentAsync(
        AgentDefinition definition,
        string path,
        CancellationToken cancellationToken)
    {
        if (definition.ExecutionResources is { } manifest && (path.StartsWith("/agent/resources/", StringComparison.Ordinal)
            || path.StartsWith("/agent/instance/resources/", StringComparison.Ordinal)))
        {
            var resource = manifest.SingleOrDefault(r => r.VirtualPath == path) ?? throw AgentCoreErrors.NotFound("Pinned resource path was not found.");
            var bytes = _publicationResources is null ? throw AgentCoreErrors.Persistence("Pinned content reader is unavailable.")
                : await _publicationResources.ReadPinnedAsync(resource, cancellationToken);
            return new(path, resource.MediaType, bytes);
        }
        if (path.StartsWith("/agent/resources", StringComparison.Ordinal))
        {
            return await ReadAgentPublicationResourceAsync(definition, path, cancellationToken).ConfigureAwait(false);
        }

        var env = RoleEnvironments.Of(definition);
        if (path == "/agent/definition.json")
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    definition.Id,
                    definition.Version,
                    name = definition.Identity.Name,
                    role = definition.Identity.Role
                },
                JsonOptions);
            return new WorkspaceContent(path, "application/json", bytes);
        }

        if (path.StartsWith("/agent/harness/", StringComparison.Ordinal))
        {
            var name = path["/agent/harness/".Length..];
            if (!env.HarnessList.Contains(name, StringComparer.Ordinal))
            {
                throw AgentCoreErrors.NotFound("Harness entry was not found.");
            }

            var text = Encoding.UTF8.GetBytes($"name={name}\n");
            return new WorkspaceContent(path, "text/plain", text);
        }

        if (path.StartsWith("/agent/knowledge/", StringComparison.Ordinal))
        {
            var identity = path["/agent/knowledge/".Length..];
            var source = env.KnowledgeList.FirstOrDefault(item =>
                string.Equals(item.Identity, identity, StringComparison.Ordinal))
                ?? throw AgentCoreErrors.NotFound("Knowledge entry was not found.");
            var text = Encoding.UTF8.GetBytes($"title={source.Title}\ncitation={source.Citation}\n");
            return new WorkspaceContent(path, "text/plain", text);
        }

        throw AgentCoreErrors.NotFound("Agent path was not found.");
    }

    private static IReadOnlyList<WorkspaceNode> ListPinnedResources(IReadOnlyList<EffectiveAgentResource> resources, string path)
    {
        var nodes = new Dictionary<string, WorkspaceNode>(StringComparer.Ordinal);
        var prefix = path.TrimEnd('/') + "/";
        foreach (var resource in resources.Where(r => r.VirtualPath.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var remainder = resource.VirtualPath[prefix.Length..];
            var segment = remainder.Split('/')[0];
            var directory = remainder.Contains('/');
            nodes[prefix + segment] = new(prefix + segment, directory, directory ? 0 : resource.ByteLength, false);
        }
        return nodes.Values.OrderBy(n => n.LogicalPath, StringComparer.Ordinal).ToArray();
    }

    private async ValueTask<IReadOnlyList<WorkspaceNode>> ListAgentPublicationResourcesAsync(
        AgentDefinition definition,
        string path,
        CancellationToken cancellationToken)
    {
        if (_publicationResources is null)
        {
            throw AgentCoreErrors.NotFound("Agent path was not found.");
        }

        const string root = "/agent/resources";
        if (path != root && !path.StartsWith(root + "/", StringComparison.Ordinal))
        {
            throw AgentCoreErrors.NotFound("Agent path was not found.");
        }

        var relativePrefix = path == root
            ? string.Empty
            : path[(root.Length + 1)..];
        var items = await _publicationResources.ListAsync(definition.Id, definition.Version, cancellationToken)
            .ConfigureAwait(false);
        var nodes = new Dictionary<string, WorkspaceNode>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var logical = DefinitionPublicationResourceReader.NormalizeLogicalPath(item.LogicalPath);
            if (!string.IsNullOrEmpty(relativePrefix)
                && !logical.StartsWith(relativePrefix + "/", StringComparison.Ordinal)
                && !string.Equals(logical, relativePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = string.IsNullOrEmpty(relativePrefix)
                ? logical
                : logical.Length == relativePrefix.Length
                    ? string.Empty
                    : logical[(relativePrefix.Length + 1)..];
            if (string.IsNullOrEmpty(remainder))
            {
                continue;
            }

            if (remainder.Contains('/', StringComparison.Ordinal))
            {
                var segment = remainder.Split('/')[0];
                var dirPath = string.IsNullOrEmpty(relativePrefix)
                    ? $"{root}/{segment}"
                    : $"{root}/{relativePrefix}/{segment}";
                nodes[dirPath] = new WorkspaceNode(dirPath, true, 0, false);
            }
            else
            {
                var filePath = string.IsNullOrEmpty(relativePrefix)
                    ? $"{root}/{remainder}"
                    : $"{root}/{relativePrefix}/{remainder}";
                nodes[filePath] = new WorkspaceNode(filePath, false, item.ByteLength, false);
            }
        }

        return nodes.Values.OrderBy(node => node.LogicalPath, StringComparer.Ordinal).ToArray();
    }

    private async ValueTask<WorkspaceContent> ReadAgentPublicationResourceAsync(
        AgentDefinition definition,
        string path,
        CancellationToken cancellationToken)
    {
        if (_publicationResources is null || path == "/agent/resources")
        {
            throw AgentCoreErrors.NotFound("Agent path was not found.");
        }

        const string root = "/agent/resources/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.NotFound("Agent path was not found.");
        }

        var logicalPath = path[root.Length..];
        var resource = await _publicationResources.FindByLogicalPathAsync(
                definition.Id,
                definition.Version,
                logicalPath,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent path was not found.");
        var bytes = await _publicationResources.ReadContentAsync(resource, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent path was not found.");
        return new WorkspaceContent(path, resource.MediaType, bytes);
    }

    private async ValueTask<IReadOnlyList<WorkspaceNode>> ListAttachmentsAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            return path is "/attachments" ? [] : throw AgentCoreErrors.NotFound("Attachment path was not found.");
        }

        var records = await _attachments.ListForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (path is "/attachments")
        {
            return records
                .Select(record => new WorkspaceNode($"/attachments/{record.AttachmentId}", false, record.ByteSize, false))
                .ToArray();
        }

        var id = ParseAttachmentId(path);
        var match = records.FirstOrDefault(record => record.AttachmentId == id)
            ?? throw AgentCoreErrors.NotFound("Attachment path was not found.");
        return [new WorkspaceNode($"/attachments/{match.AttachmentId}", false, match.ByteSize, false)];
    }

    private async ValueTask<WorkspaceContent> ReadAttachmentAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            throw AgentCoreErrors.NotFound("Attachment path was not found.");
        }

        var id = ParseAttachmentId(path);
        var record = await _attachments.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Attachment path was not found.");
        await using var stream = await _attachments.OpenContentAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new WorkspaceContent(path, record.ContentType, buffer.ToArray());
    }

    private static Guid ParseAttachmentId(string path)
    {
        var token = path["/attachments/".Length..].Split('/', 2)[0];
        return Guid.TryParse(token, out var id) ? id : throw AgentCoreErrors.NotFound("Attachment path was not found.");
    }
}
