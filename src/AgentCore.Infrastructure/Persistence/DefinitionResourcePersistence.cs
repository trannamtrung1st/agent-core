using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class DefinitionResourcePersistence
{
    internal static async Task VerifyDraftResourcesAsync(
        AgentCoreDbContext db,
        IDefinitionResourceContentStore content,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draftKey = draftId.ToString("D");
        var rows = await db.AgentDefinitionDraftResources.AsNoTracking()
            .Where(row => row.DraftId == draftKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            await DefinitionResourceStoredContent.RequireForBindingAsync(
                content,
                row.ContentSha256,
                row.ByteLength,
                row.MediaType,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void BindDraftResourcesToPublication(
        AgentCoreDbContext db,
        Guid draftId,
        string definitionId,
        int version)
    {
        var draftKey = draftId.ToString("D");
        var rows = db.AgentDefinitionDraftResources.AsNoTracking()
            .Where(row => row.DraftId == draftKey)
            .ToList();
        foreach (var row in rows)
        {
            db.AgentDefinitionPublicationResources.Add(new AgentDefinitionPublicationResourceRecord
            {
                DefinitionId = definitionId,
                Version = version,
                ResourceId = row.ResourceId,
                LogicalPath = row.LogicalPath,
                Kind = row.Kind,
                MediaType = row.MediaType,
                ContentSha256 = row.ContentSha256,
                ByteLength = row.ByteLength
            });
        }
    }

    internal static AgentDefinitionDraftResource MapDraftResource(AgentDefinitionDraftResourceRecord row) =>
        new(
            Guid.Parse(row.ResourceId),
            Guid.Parse(row.DraftId),
            row.LogicalPath,
            (AgentDefinitionResourceKind)row.Kind,
            row.MediaType,
            row.ContentSha256,
            row.ByteLength,
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));

    internal static AgentDefinitionPublicationResource MapPublicationResource(AgentDefinitionPublicationResourceRecord row) =>
        new(
            row.DefinitionId,
            row.Version,
            Guid.Parse(row.ResourceId),
            row.LogicalPath,
            (AgentDefinitionResourceKind)row.Kind,
            row.MediaType,
            row.ContentSha256,
            row.ByteLength);

    internal static AgentDefinitionDraftResourceRecord MapDraftResource(AgentDefinitionDraftResource resource) =>
        new()
        {
            ResourceId = resource.ResourceId.ToString("D"),
            DraftId = resource.DraftId.ToString("D"),
            LogicalPath = resource.LogicalPath,
            Kind = (int)resource.Kind,
            MediaType = resource.MediaType,
            ContentSha256 = resource.ContentSha256,
            ByteLength = resource.ByteLength,
            UpdatedAtUtc = resource.UpdatedAt.ToUnixTimeMilliseconds()
        };
}
