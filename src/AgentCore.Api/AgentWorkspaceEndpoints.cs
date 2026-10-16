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
        home.MapGet("", async (Guid instanceId, string? prefix, string? cursor, int? limit, HttpContext http, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try
            {
                Query(http, "prefix", "cursor", "limit");
                var page = await workspace.ListAsync(instanceId, prefix ?? "/home", cursor, limit ?? AgentWorkspaceLimits.MaxPageItems, ct);
                return Results.Json(new AgentWorkspacePageResponse(page.Items.Select(MapItem).ToArray(), page.UsedBytes, page.TotalItems, page.NextPath,
                    AgentWorkspaceLimits.MaxFileBytes, AgentWorkspaceLimits.MaxInstanceBytes, page.UsageEnforcement, page.Truncated));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        home.MapGet("content", async (Guid instanceId, string path, HttpContext http, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try
            {
                Query(http, "path");
                var content = await workspace.ReadAsync(instanceId, path, ct);
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Bytes(content.Bytes, content.Item.ContentType, Path.GetFileName(content.Item.LogicalPath));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        home.MapGet("metadata", async (Guid instanceId, string path, HttpContext http, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try
            {
                Query(http, "path");
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Json(MapItem((await workspace.InspectAsync(instanceId, path, cancellationToken: ct)).Item));
            }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        home.MapDelete("content", async (Guid instanceId, string path, HttpContext http, AgentInstanceWorkspaceService workspace, CancellationToken ct) =>
        {
            try { Query(http, "path"); await workspace.DeleteAsync(instanceId, path, ct); return Results.NoContent(); }
            catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
    }
    internal static void Query(HttpContext http, params string[] allowed)
    {
        if (http.Request.Query.Any(q => !allowed.Contains(q.Key, StringComparer.Ordinal) || q.Value.Count != 1))
            throw AgentCoreErrors.Validation("Unknown or repeated workspace query parameter.");
    }
    private static AgentWorkspaceItemResponse MapItem(AgentWorkspaceItem item) => new(item.AgentInstanceId, item.LogicalPath,
        item.ContentType, item.ByteSize, item.UpdatedAt, item.Directory, item.Kind, item.LinkTarget, item.Mode);
}
