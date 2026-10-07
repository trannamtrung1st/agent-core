using AgentCore.Api.Http;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public static class AgentWorkspaceEndpoints
{
    public static void Map(WebApplication app)
    {
        var home = app.MapGroup("/api/v2/agent-instances/{instanceId:guid}/workspace")
            .AddEndpointFilter<OwnerCapabilityFilter>().DisableAntiforgery();
        home.MapGet("", async (Guid instanceId, string? prefix, string? afterPath, int? limit, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try
            {
                var page = await workspace.ListAsync(instanceId, prefix ?? "/home", afterPath, limit ?? AgentWorkspaceLimits.MaxPageItems, ct);
                return Results.Json(new AgentWorkspacePageResponse(page.Items.Select(MapItem).ToArray(), page.UsedBytes, page.TotalItems, page.NextPath,
                    AgentWorkspaceLimits.MaxFileBytes, AgentWorkspaceLimits.MaxInstanceBytes, page.TreeSha256));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        home.MapGet("{itemId:guid}/content", async (Guid instanceId, Guid itemId, HttpContext http, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try
            {
                var content = await workspace.ReadAsync(instanceId, itemId, cancellationToken: ct);
                http.Response.Headers.ETag = $"\"{content.Item.Revision}\"";
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Bytes(content.Bytes, content.Item.ContentType, Path.GetFileName(content.Item.LogicalPath));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        home.MapDelete("{itemId:guid}", async (Guid instanceId, Guid itemId, long expectedRevision, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try { await workspace.DeleteAsync(instanceId, itemId, expectedRevision, ct); return Results.NoContent(); }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });

    }

    private static AgentWorkspaceItemResponse MapItem(AgentWorkspaceItem item) => new(item.ItemId, item.AgentInstanceId, item.LogicalPath,
        item.ContentType, item.ByteSize, item.Sha256Hex, item.Revision, item.CreatedAt, item.UpdatedAt, item.SourceSessionId, item.Directory);
}
