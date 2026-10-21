using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<IReadOnlySet<AccessScope>> ChildCatalogAsync(AgentCoreDbContext db, Guid tenant, Guid instance, TenantAgentConfigurationChange? change, CancellationToken ct)
    {
        var key = instance.ToString("D"); var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == key, ct).ConfigureAwait(false);
        if (row is null) return new HashSet<AccessScope>();
        var snapshot = new TenantAgentConfigurationSnapshot(Owned(row), await SqliteAgentInstanceStore.ReadSkillsAsync(db, instance, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, instance, ct).ConfigureAwait(false), await ReadIsolationMetadataAsync(db, key, ct).ConfigureAwait(false));
        if (change is not null) snapshot = TenantAgentChildScopes.After(snapshot, change);
        var owner = snapshot.Agent.Instance; AgentDefinition? definition;
        IReadOnlyList<AgentDefinitionPublicationResource> resources;
        if (Guid.TryParse(owner.DefinitionId, out var id))
        {
            var definitionKey = id.ToString("D");
            var publication = await db.AgentDefinitionPublications.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.DefinitionId == definitionKey && p.Version == owner.ActiveVersion
                && db.Set<TenantDefinitionRecord>().Any(d => d.TenantId == tenant && d.DefinitionId == p.DefinitionId && d.DeletedAt == null), ct).ConfigureAwait(false);
            definition = publication is null ? null : AgentDefinitionAdminMapping.MapPublication(publication).Payload;
            var items = definition is null ? [] : await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.DefinitionId == definitionKey && r.Version == owner.ActiveVersion).ToArrayAsync(ct).ConfigureAwait(false);
            resources = items.Select(DefinitionResourcePersistence.MapPublicationResource).ToArray();
        }
        else
        {
            definition = builtIns is null ? null : await builtIns.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct).ConfigureAwait(false);
            resources = definition is not null && inheritedResources is not null ? await inheritedResources.ListAsync(definition, ct).ConfigureAwait(false) : [];
        }
        return TenantAgentChildScopes.Catalog(tenant, snapshot, definition, resources);
    }
}
