using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Security;

public static class TenantWorkspaceEndpoints
{
    public static void Map(WebApplication app)
    {
        MapWorkspace(app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/workspace").RequireAuthorization(), false);
        MapWorkspace(app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/workspace").RequireAuthorization(), true);
    }
    private static void MapWorkspace(RouteGroupBuilder group, bool working)
    {
        group.MapGet("access", (HttpContext http, TenantWorkspaceMutationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http); Headers(http); var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.InspectAsync(actor.TenantId, Id(http, working), working, Human(http, actor), ct).ConfigureAwait(false));
        }));
        group.MapPut("content", (string path, long expectedRevision, HttpContext http, TenantWorkspaceMutationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "path", "expectedRevision"); Headers(http);
            var actor = IdentityHttp.TenantContext(http); var human = Human(http, actor); var id = Id(http, working);
            var workspace = await service.AuthorizeAsync(actor.TenantId, id, working, human, expectedRevision, false, ct).ConfigureAwait(false);
            path = TenantWorkspaceRules.Path(workspace, path, file: true);
            if (http.Request.ContentLength > AgentWorkspaceLimits.MaxFileBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
            await using var buffer = new MemoryStream(); var block = new byte[81920];
            while (true)
            {
                var count = await http.Request.Body.ReadAsync(block, ct).ConfigureAwait(false); if (count == 0) break;
                if (buffer.Length + count > AgentWorkspaceLimits.MaxFileBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                await buffer.WriteAsync(block.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            return await MutationResult(() => service.WriteAsync(actor.TenantId, id, working, human, expectedRevision, path,
                buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), ct)).ConfigureAwait(false);
        })).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AgentWorkspaceLimits.MaxFileBytes));
        group.MapDelete("content", (string path, long expectedRevision, HttpContext http, TenantWorkspaceMutationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "path", "expectedRevision"); Headers(http); var actor = IdentityHttp.TenantContext(http);
            return await MutationResult(() => service.DeleteAsync(actor.TenantId, Id(http, working), working, Human(http, actor), expectedRevision, path, ct)).ConfigureAwait(false);
        }));
        group.MapGet("", (string? prefix, string? cursor, int? limit, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "prefix", "cursor", "limit"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Page(await service.ListAsync(actor.TenantId, Id(http, working), working, Human(http, actor), prefix, cursor, limit ?? 100, ct).ConfigureAwait(false)));
        }));
        group.MapGet("search", (string? q, string? cursor, int? limit, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "q", "cursor", "limit"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Page(await service.SearchAsync(actor.TenantId, Id(http, working), working, Human(http, actor), q ?? "", cursor, limit ?? 100, ct).ConfigureAwait(false)));
        }));
        group.MapGet("metadata", (string path, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "path"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Item((await service.MetadataAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path, ct).ConfigureAwait(false)).Item));
        }));
        group.MapGet("content", (string path, HttpContext http, TenantWorkspaceReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "path"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            var content = await service.ReadAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path, ct).ConfigureAwait(false);
            return Results.File(content.Bytes, content.Item.ContentType, WorkspaceFileNames.Sanitize(Path.GetFileName(content.Item.LogicalPath)));
        }));
        group.MapGet("office-preview", (string path, string? sheet, string? range, HttpContext http, TenantWorkspaceReadService service,
            IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            WorkspaceQuery.Query(http, "path", "sheet", "range"); Headers(http);
            var actor = IdentityHttp.TenantContext(http);
            try
            {
                return await service.ProjectAsync(actor.TenantId, Id(http, working), working, Human(http, actor), path,
                    item => policy.OfficeLimit(Path.GetFileName(item.LogicalPath), item.ContentType),
                    (source, token) => ValueTask.FromResult(OfficePreviewMapping.Project(Path.GetFileName(source.Item.LogicalPath),
                        source.Item.ContentType, source.Bytes, sheet, range, office, policy, token)), ct).ConfigureAwait(false);
            }
            catch (AgentCore.Application.Sessions.AgentCoreException ex) { return OfficePreviewMapping.Problem(ex); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
        }));
    }
    private static Guid Id(HttpContext http, bool working) => Guid.Parse((string)http.Request.RouteValues[working ? "sessionId" : "instanceId"]!);
    private static async ValueTask<IResult> MutationResult(Func<ValueTask> execute)
    {
        try { await execute().ConfigureAwait(false); return Results.NoContent(); }
        catch (AgentCoreException error) when (error.Code == "workspace_effect_unknown")
        { return Results.Json(new { reasonCode = error.Code, title = error.Message, mutationsMayHaveOccurred = true }, statusCode: 409); }
    }
    private static HumanSessionReference Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static void Headers(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff"; }
    private static AgentWorkspaceItemResponse Item(AgentWorkspaceItem item) => new(item.AgentInstanceId, item.LogicalPath, item.ContentType,
        item.ByteSize, item.UpdatedAt, item.Directory, item.Kind, item.LinkTarget, item.Mode);
    private static AgentWorkspacePageResponse Page(AgentWorkspacePage page) => new(page.Items.Select(Item).ToArray(), page.UsedBytes, page.TotalItems,
        page.NextPath, AgentWorkspaceLimits.MaxFileBytes, AgentWorkspaceLimits.MaxInstanceBytes, page.UsageEnforcement, page.Truncated);
}
