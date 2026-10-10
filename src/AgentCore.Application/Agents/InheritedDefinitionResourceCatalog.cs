using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

/// <summary>One inherited manifest for durable publications and approved built-in knowledge.</summary>
public sealed class InheritedDefinitionResourceCatalog(IAgentDefinitionResourceAdminStore publications,
    IBuiltInAgentDefinitionStore builtIns, IApprovedKnowledgeCatalog knowledge, IDefinitionResourceContentStore content)
{
    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListAsync(AgentDefinition definition, CancellationToken ct = default)
    {
        var items = (await publications.ListPublicationResourcesAsync(definition.Id, definition.Version, ct)).ToList();
        var builtIn = await builtIns.GetAsync(definition.Id, definition.Version, ct);
        if (builtIn is null) return items;
        foreach (var source in RoleEnvironments.Of(builtIn).KnowledgeList)
        {
            var path = KnowledgeSourcePaths.ResolveBackingPath(source);
            if (items.Any(r => r.LogicalPath == path)) continue;
            var text = await knowledge.ReadContentAsync(source.Identity, ct)
                ?? throw AgentCoreErrors.Persistence($"Approved built-in knowledge '{source.Identity}' is unavailable.");
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            // Reuse the reader and deduplicated immutable storage; do not create publication rows or copies of source files.
            await content.StoreVerifiedAsync(hash, bytes, ct);
            var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"builtin-knowledge\0{definition.Id}\0{source.Identity}"));
            items.Add(new(definition.Id, definition.Version, new Guid(identity.AsSpan(0, 16)), path,
                AgentDefinitionResourceKind.Knowledge, "text/markdown", hash, bytes.LongLength));
        }
        return items.OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray();
    }
}
