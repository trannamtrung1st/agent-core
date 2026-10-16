using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async ValueTask<bool> OwnsDefinitionResourceAsync(AgentCoreDbContext db, Guid tenant, DefinitionResourceReference reference, CancellationToken ct)
    {
        var definition = reference.DefinitionId.ToString("D"); var resource = reference.ResourceId.ToString("D");
        if (reference.DraftId is { } draft)
        {
            var draftKey = draft.ToString("D");
            return await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AnyAsync(r => r.DraftId == draftKey && r.ResourceId == resource
                && db.AgentDefinitionDrafts.IgnoreQueryFilters().Any(d => d.DraftId == r.DraftId && d.DefinitionId == definition
                    && db.Set<TenantDefinitionRecord>().Any(root => root.DefinitionId == d.DefinitionId && root.TenantId == tenant && root.DeletedAt == null)), ct).ConfigureAwait(false);
        }
        return await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AnyAsync(r => r.DefinitionId == definition && r.Version == reference.Version && r.ResourceId == resource
            && db.AgentDefinitionPublications.IgnoreQueryFilters().Any(p => p.DefinitionId == r.DefinitionId && p.Version == r.Version)
            && db.Set<TenantDefinitionRecord>().Any(root => root.DefinitionId == r.DefinitionId && root.TenantId == tenant && root.DeletedAt == null), ct).ConfigureAwait(false);
    }
}
