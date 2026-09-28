using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record PreparedBatchResource(
    Guid? ResourceId,
    string LogicalPath,
    AgentDefinitionResourceKind Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength);

public static class DefinitionResourceBatch
{
    public static async ValueTask<IReadOnlyList<PreparedBatchResource>> PrepareAsync(
        IDefinitionResourceContentStore content,
        IReadOnlyList<AgentDefinitionDraftResourceBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            throw AgentCoreErrors.Validation("Batch must include at least one resource.");
        }

        var prepared = new List<PreparedBatchResource>(items.Count);
        foreach (var item in items)
        {
            var logicalPath = DefinitionResourcePolicies.NormalizeLogicalPath(item.LogicalPath);
            var mediaType = DefinitionResourcePolicies.NormalizeMediaType(item.MediaType);
            DefinitionResourcePolicies.ValidateContentSize(item.ByteLength);
            await DefinitionResourceStoredContent.RequireForBindingAsync(
                content,
                item.ContentSha256,
                item.ByteLength,
                mediaType,
                cancellationToken).ConfigureAwait(false);
            prepared.Add(new PreparedBatchResource(
                item.ResourceId,
                logicalPath,
                item.Kind,
                mediaType,
                item.ContentSha256,
                item.ByteLength));
        }

        return prepared;
    }
}
