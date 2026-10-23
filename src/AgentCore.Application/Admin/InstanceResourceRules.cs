using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record InstanceResourceView(string Key, string Origin, string LogicalPath, AgentDefinitionResourceKind Kind,
    string MediaType, string ContentSha256, long ByteLength, bool Enabled, bool? EnabledOverride, long Revision,
    string VirtualPath, IReadOnlyList<string> Dependencies, string? SourceDefinitionId = null,
    int? SourceDefinitionVersion = null, Guid? SourceDefinitionResourceId = null);
public sealed record InstanceResourceCatalog(long InstanceRevision, string DefinitionId, int DefinitionVersion, IReadOnlyList<InstanceResourceView> Resources);

public static class InstanceResourceRules
{
    internal static void RequireReadSize(long bytes, int? maxBytes)
    {
        if (maxBytes is <= 0) throw AgentCoreErrors.Validation("Invalid resource content bound.");
        if (maxBytes.HasValue && bytes > maxBytes.Value)
            throw new AgentCoreException("preview_too_large", "The file exceeds its effective Office preview limit. Download the original.", 413);
    }
    internal static AgentInstanceResource PrepareUpsert(AgentInstance owner, InstanceResourceSnapshot snapshot, Guid? resourceId,
        long? expectedResourceRevision, string path, AgentDefinitionResourceKind kind, string mediaType, byte[] bytes,
        bool enabled, SkillAuthor actor, Guid newResourceId, DateTimeOffset now, AgentDefinitionPublicationResource? source = null)
    {
        var id = owner.InstanceId;
        var old = resourceId is Guid existing ? snapshot.InstanceResources.SingleOrDefault(r => r.ResourceId == existing)
            ?? throw AgentCoreErrors.NotFound("Instance resource was not found.") : null;
        if (old?.Revision != expectedResourceRevision) throw AgentCoreErrors.Conflict("Resource revision is stale.");
        if (!Enum.IsDefined(kind) || kind == AgentDefinitionResourceKind.EvalFixture)
            throw AgentCoreErrors.Validation("Instance resources support Knowledge, Reference, Template and StaticAsset; evaluation fixtures belong to Definitions.");
        path = DefinitionResourcePolicies.NormalizeLogicalPath(path);
        mediaType = DefinitionResourcePolicies.NormalizeMediaType(mediaType);
        DefinitionResourcePolicies.ValidateContentSize(bytes.LongLength);
        DefinitionResourcePolicies.RejectSecretsInTextualContent(mediaType, bytes);
        if (kind == AgentDefinitionResourceKind.Knowledge && !DefinitionResourcePolicies.IsTextualKnowledgeMediaType(mediaType))
            throw AgentCoreErrors.Validation("Knowledge resources require supported text content; use Reference or StaticAsset for binary files.");
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        var resource = old is null ? new AgentInstanceResource(id, newResourceId, path, kind, mediaType, hash, bytes.LongLength,
            enabled, 1, now, now, actor, source?.DefinitionId, source?.Version, source?.ResourceId) : old with { LogicalPath = path, Kind = kind, MediaType = mediaType,
                ContentSha256 = hash, ByteLength = bytes.LongLength, Enabled = enabled, Revision = old.Revision + 1, UpdatedAt = now };
        return resource;
    }
    public static IReadOnlyList<InstanceResourceView> Views(AgentDefinition definition, IReadOnlyList<AgentDefinitionPublicationResource> published,
        InstanceResourceSnapshot local, InstanceSkillSnapshot skills)
    {
        var enabledSkills = EffectiveSkillCatalogResolver.Resolve(definition, skills);
        var inherited = published.Select(r => {
            var state = local.DefinitionStates.SingleOrDefault(s => s.ResourceId == r.ResourceId);
            return new InstanceResourceView("definition:" + r.ResourceId.ToString("D"), "Definition", r.LogicalPath, r.Kind,
                r.MediaType, r.ContentSha256, r.ByteLength, state?.EnabledOverride ?? true, state?.EnabledOverride,
                state?.Revision ?? 0, "/agent/resources/" + r.LogicalPath,
                enabledSkills.Where(s => s.Origin == SkillOrigin.Definition && s.ResourcePaths.Contains(r.LogicalPath, StringComparer.Ordinal)).Select(s => s.Key).ToArray());
        });
        return inherited.Concat(local.InstanceResources.Select(r => new InstanceResourceView("instance:" + r.ResourceId.ToString("D"), "Instance",
            r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength, r.Enabled, null, r.Revision,
            "/agent/instance/resources/" + r.LogicalPath, [], r.SourceDefinitionId, r.SourceDefinitionVersion, r.SourceDefinitionResourceId))).ToArray();
    }
    public static IReadOnlyList<EffectiveAgentResource> Resolve(AgentDefinition definition, IReadOnlyList<AgentDefinitionPublicationResource> published,
        InstanceResourceSnapshot local, InstanceSkillSnapshot skills)
    {
        var views = Views(definition, published, local, skills);
        var disabled = views.Where(r => !r.Enabled && r.Dependencies.Count > 0).ToArray();
        if (disabled.Length > 0) throw AgentCoreErrors.Validation("Enabled Skills depend on disabled resources: " + string.Join(", ", disabled.Select(r => r.Key)));
        // Built-in resources are backed by the approved file catalog. Durable publications must contain every bound Skill file.
        if (published.Count > 0)
            foreach (var skill in EffectiveSkillCatalogResolver.Resolve(definition, skills))
                foreach (var path in skill.ResourcePaths)
                    if (!views.Any(r => r.Origin == "Definition" && r.LogicalPath == path && r.Enabled))
                        throw AgentCoreErrors.Validation($"Skill '{skill.Key}' requires missing resource '{path}'.");
        return views.Where(r => r.Enabled).Select(r => new EffectiveAgentResource(r.Key, r.LogicalPath, r.Kind, r.MediaType,
            r.ContentSha256, r.ByteLength, r.VirtualPath)).ToArray();
    }
    public static void ValidateCollection(IEnumerable<AgentInstanceResource> resources)
    {
        var items = resources.ToArray();
        if (items.Length > AgentResourceLimits.MaxItemsPerDraft || items.Sum(r => r.ByteLength) > AgentResourceLimits.MaxAggregateBytes)
            throw AgentCoreErrors.Validation("Instance resource quota exceeded.");
        if (items.Select(r => r.LogicalPath).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw AgentCoreErrors.Conflict("Resource logical path is already occupied in this Instance.");
    }
    public static (string Origin, Guid Id) ParseKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 2 || parts[0] is not ("definition" or "instance") || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty)
            throw AgentCoreErrors.Validation("Use an origin-qualified resource key.");
        return (parts[0], id);
    }
    public static void Verify(string hash, long length, byte[] bytes)
    {
        if (bytes.LongLength != length || DefinitionResourceContentHasher.ComputeSha256Hex(bytes) != hash)
            throw AgentCoreErrors.Persistence("Resource content hash or length disagrees with its immutable manifest.");
    }
}
