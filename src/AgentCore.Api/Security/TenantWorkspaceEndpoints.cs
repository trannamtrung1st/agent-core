using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Security;

public static class TenantWorkspaceEndpoints
{
    public static void Map(WebApplication app)
    {
        MapReads(app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/workspace").RequireAuthorization(), false);
        MapReads(app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/workspace").RequireAuthorization(), true);
    }
    private static void MapReads(RouteGroupBuilder group, bool working)
    {
        group.MapGet("", (string? prefix, string? cursor, int? limit, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            AgentWorkspaceEndpoints.Query(http, "prefix", "cursor", "limit"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Page(await service.ListAsync(actor.TenantId, Id(http, working), working, Human(http, actor), prefix, cursor, limit ?? 100, ct).ConfigureAwait(false)));
        }));
        group.MapGet("search", (string? q, string? cursor, int? limit, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            AgentWorkspaceEndpoints.Query(http, "q", "cursor", "limit"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Page(await service.SearchAsync(actor.TenantId, Id(http, working), working, Human(http, actor), q ?? "", cursor, limit ?? 100, ct).ConfigureAwait(false)));
        }));
        group.MapGet("metadata", (string path, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            AgentWorkspaceEndpoints.Query(http, "path"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Item((await service.MetadataAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path, ct).ConfigureAwait(false)).Item));
        }));
        group.MapGet("content", (string path, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            AgentWorkspaceEndpoints.Query(http, "path"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            var content = await service.ReadAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path, ct).ConfigureAwait(false);
            return Results.File(content.Bytes, content.Item.ContentType, WorkspaceFileNames.Sanitize(Path.GetFileName(content.Item.LogicalPath)));
        }));
        group.MapGet("office-preview", (string path, string? sheet, string? range, HttpContext http, TenantWorkspaceReadService service,
            IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            AgentWorkspaceEndpoints.Query(http, "path", "sheet", "range"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            try
            {
                return await service.ProjectAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path,
                    item => policy.OfficeLimit(Path.GetFileName(item.LogicalPath), item.ContentType),
                    (source, token) => ValueTask.FromResult(OfficePreviewEndpoints.Project(Path.GetFileName(source.Item.LogicalPath),
                        source.Item.ContentType, source.Bytes, sheet, range, office, policy, token)), ct).ConfigureAwait(false);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex) { return OfficePreviewEndpoints.Problem(ex); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
        }));
    }
    private static Guid Id(HttpContext http, bool working) => Guid.Parse((string)http.Request.RouteValues[working ? "sessionId" : "instanceId"]!);
    private static HumanSessionReference Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static void Headers(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff"; }
    private static AgentWorkspaceItemResponse Item(AgentWorkspaceItem item) => new(item.AgentInstanceId, item.LogicalPath, item.ContentType,
        item.ByteSize, item.UpdatedAt, item.Directory, item.Kind, item.LinkTarget, item.Mode);
    private static AgentWorkspacePageResponse Page(AgentWorkspacePage page) => new(page.Items.Select(Item).ToArray(), page.UsedBytes, page.TotalItems,
        page.NextPath, AgentWorkspaceLimits.MaxFileBytes, AgentWorkspaceLimits.MaxInstanceBytes, page.UsageEnforcement, page.Truncated);
}
