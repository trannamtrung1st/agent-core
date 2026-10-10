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

public sealed class AgentInstanceResourceService(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IAgentDefinitionResourceAdminStore publications, IDefinitionResourceContentStore content, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<InstanceResourceCatalog> ListAsync(Guid id, CancellationToken ct = default)
    {
        var owner = await RequireAsync(id, ct);
        var definition = await DefinitionAsync(owner, ct);
        var local = await instances.ReadResourcesAsync(id, ct);
        var skills = await instances.ReadSkillsAsync(id, ct);
        var published = await publications.ListPublicationResourcesAsync(definition.Id, definition.Version, ct);
        if ((await RequireAsync(id, ct)).Revision != owner.Revision) throw AgentCoreErrors.Conflict("Instance changed during the read. Reload resources.");
        return new(owner.Revision, definition.Id, definition.Version, Views(definition, published, local, skills));
    }
    public async ValueTask<byte[]> ReadAsync(Guid id, string key, CancellationToken ct = default) => (await ReadContentAsync(id, key, ct)).Bytes;
    public async ValueTask<(InstanceResourceView Resource, byte[] Bytes)> ReadContentAsync(Guid id, string key, CancellationToken ct = default)
    {
        var resource = (await ListAsync(id, ct)).Resources.SingleOrDefault(r => r.Key == key) ?? throw AgentCoreErrors.NotFound("Resource was not found in this Instance.");
        var bytes = await content.ReadAsync(resource.ContentSha256, ct) ?? throw AgentCoreErrors.Persistence("Resource bytes are unavailable.");
        Verify(resource.ContentSha256, resource.ByteLength, bytes);
        return (resource, bytes);
    }
    public async ValueTask<InstanceResourceCatalog> UpsertAsync(Guid id, long expectedInstanceRevision, Guid? resourceId,
        long? expectedResourceRevision, string path, AgentDefinitionResourceKind kind, string mediaType,
        byte[] bytes, bool enabled = true, SkillAuthor actor = SkillAuthor.Admin, CancellationToken ct = default,
        AgentDefinitionPublicationResource? source = null, HarnessManagementState? harnessManagement = null)
    {
        var owner = await WritableAsync(id, expectedInstanceRevision, ct);
        var snapshot = await instances.ReadResourcesAsync(id, ct);
        var old = resourceId is Guid existing ? snapshot.InstanceResources.SingleOrDefault(r => r.ResourceId == existing)
            ?? throw AgentCoreErrors.NotFound("Instance resource was not found.") : null;
        if (old?.Revision != expectedResourceRevision) throw AgentCoreErrors.Conflict("Resource revision is stale.");
        if (!Enum.IsDefined(kind)) throw AgentCoreErrors.Validation("Resource kind is invalid.");
        path = DefinitionResourcePolicies.NormalizeLogicalPath(path);
        mediaType = DefinitionResourcePolicies.NormalizeMediaType(mediaType);
        DefinitionResourcePolicies.ValidateContentSize(bytes.LongLength);
        DefinitionResourcePolicies.RejectSecretsInTextualContent(mediaType, bytes);
        if (kind == AgentDefinitionResourceKind.Knowledge && !DefinitionResourcePolicies.IsTextualKnowledgeMediaType(mediaType))
            throw AgentCoreErrors.Validation("Knowledge resources require supported text content; use Reference or StaticAsset for binary files.");
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        var now = time.GetUtcNow();
        var resource = old is null ? new AgentInstanceResource(id, ids.NewId(), path, kind, mediaType, hash, bytes.LongLength,
            enabled, 1, now, now, actor, source?.DefinitionId, source?.Version, source?.ResourceId) : old with { LogicalPath = path, Kind = kind, MediaType = mediaType,
                ContentSha256 = hash, ByteLength = bytes.LongLength, Enabled = enabled, Revision = old.Revision + 1, UpdatedAt = now };
        await content.StoreVerifiedAsync(hash, bytes.ToArray(), ct);
        await instances.MutateResourcesAsync(new(id, owner.Revision, Resource: resource, ExpectedResourceRevision: old?.Revision,
            History: History(owner, resource.ResourceId, "upsert", actor), HarnessManagement: harnessManagement), ct);
        return await ListAsync(id, ct);
    }
    public async ValueTask<InstanceResourceCatalog> CopyAsync(Guid id, string key, long expectedInstanceRevision,
        string logicalPath, CancellationToken ct = default)
    {
        var owner = await WritableAsync(id, expectedInstanceRevision, ct);
        var parsed = ParseKey(key);
        if (parsed.Origin != "definition") throw AgentCoreErrors.Validation("Copy requires a Definition resource.");
        var resource = (await publications.ListPublicationResourcesAsync(owner.DefinitionId, owner.ActiveVersion, ct))
            .SingleOrDefault(r => r.ResourceId == parsed.Id) ?? throw AgentCoreErrors.NotFound("Source resource was not found.");
        var bytes = await content.ReadAsync(resource.ContentSha256, ct) ?? throw AgentCoreErrors.Persistence("Source bytes are unavailable.");
        Verify(resource.ContentSha256, resource.ByteLength, bytes);
        return await UpsertAsync(id, expectedInstanceRevision, null, null, logicalPath, resource.Kind, resource.MediaType,
            bytes, true, SkillAuthor.Admin, ct, resource);
    }
    public async ValueTask<InstanceResourceCatalog> SetEnabledAsync(Guid id, string key, long expectedInstanceRevision,
        long expectedResourceRevision, bool? enabled, SkillAuthor actor = SkillAuthor.Admin, CancellationToken ct = default, HarnessManagementState? harnessManagement = null)
    {
        var owner = await WritableAsync(id, expectedInstanceRevision, ct);
        var catalog = await ListAsync(id, ct);
        var view = catalog.Resources.SingleOrDefault(r => r.Key == key) ?? throw AgentCoreErrors.NotFound("Resource was not found.");
        if (view.Revision != expectedResourceRevision) throw AgentCoreErrors.Conflict("Resource revision is stale.");
        if (enabled == false && view.Dependencies.Count > 0) throw AgentCoreErrors.Validation("Disable dependent Skills first: " + string.Join(", ", view.Dependencies));
        var state = await instances.ReadResourcesAsync(id, ct);
        var resourceId = ParseKey(key).Id;
        if (view.Origin == "Instance")
        {
            if (enabled is null) throw AgentCoreErrors.Validation("Instance-owned resources do not inherit enabled state.");
            var resource = state.InstanceResources.Single(r => r.ResourceId == resourceId);
            if (resource.Enabled == enabled) return catalog;
            await instances.MutateResourcesAsync(new(id, owner.Revision, Resource: resource with { Enabled = enabled.Value,
                Revision = resource.Revision + 1, UpdatedAt = time.GetUtcNow() }, ExpectedResourceRevision: resource.Revision,
                History: History(owner, resourceId, "set_enabled", actor), HarnessManagement: harnessManagement), ct);
        }
        else
        {
            var previous = state.DefinitionStates.SingleOrDefault(r => r.ResourceId == resourceId);
            if (previous?.EnabledOverride == enabled) return catalog;
            await instances.MutateResourcesAsync(new(id, owner.Revision, DefinitionState: new(id, resourceId, enabled,
                (previous?.Revision ?? 0) + 1, time.GetUtcNow()), ExpectedStateRevision: previous?.Revision,
                History: History(owner, resourceId, enabled is null ? "reset_enabled" : "set_enabled", actor), HarnessManagement: harnessManagement), ct);
        }
        return await ListAsync(id, ct);
    }
    public async ValueTask<InstanceResourceCatalog> DeleteAsync(Guid id, string key, long expectedInstanceRevision,
        long expectedResourceRevision, SkillAuthor actor = SkillAuthor.Admin, CancellationToken ct = default, HarnessManagementState? harnessManagement = null)
    {
        var owner = await WritableAsync(id, expectedInstanceRevision, ct);
        var parsed = ParseKey(key);
        if (parsed.Origin != "instance") throw AgentCoreErrors.Validation("Inherited resource bytes are read-only.");
        await instances.MutateResourcesAsync(new(id, owner.Revision, DeleteResourceId: parsed.Id,
            ExpectedResourceRevision: expectedResourceRevision, History: History(owner, parsed.Id, "delete", actor), HarnessManagement: harnessManagement), ct);
        // Content addresses remain retained for admitted Runs. No mutation deletes hash-addressed bytes.
        return await ListAsync(id, ct);
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
    private AdminEventAppend History(AgentInstance owner, Guid resourceId, string operation, SkillAuthor actor) =>
        new(ids.NewId(), time.GetUtcNow(), actor == SkillAuthor.Agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.InstanceResourcesChanged, "agent.instance", owner.InstanceId.ToString("D"), owner.Revision + 1,
            owner.ActiveVersion, JsonSerializer.Serialize(new { resourceId, operation }));
    private async ValueTask<AgentInstance> RequireAsync(Guid id, CancellationToken ct) => await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
    private async ValueTask<AgentInstance> WritableAsync(Guid id, long revision, CancellationToken ct)
    {
        var owner = await RequireAsync(id, ct);
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances are read-only.");
        if (owner.Revision != revision) throw AgentCoreErrors.Conflict("Instance revision is stale. Reload while retaining your draft.");
        return owner;
    }
    private async ValueTask<AgentDefinition> DefinitionAsync(AgentInstance owner, CancellationToken ct) => await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Selected Definition was not found.");
}
