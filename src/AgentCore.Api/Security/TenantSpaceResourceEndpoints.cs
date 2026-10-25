using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Security;

public static class TenantSpaceResourceEndpoints
{
    public sealed record SaveRequest(string SourcePath, string LogicalPath, string Kind, long ExpectedRevision, long ExpectedSpaceRevision, long ExpectedResourceRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/spaces/{spaceId:guid}/resources").RequireAuthorization();
        group.MapGet("", (Guid spaceId, int? page, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ResourcesAsync(actor.PrincipalId, actor.TenantId, spaceId, page ?? 0, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapGet("/{resourceId:guid}/access", (Guid spaceId, Guid resourceId, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ResourceAccessAsync(actor.PrincipalId, actor.TenantId, spaceId, resourceId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapPut("/{resourceId:guid}", (Guid spaceId, Guid resourceId, SaveRequest body, HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            if (!Enum.TryParse<AgentDefinitionResourceKind>(body.Kind, false, out var kind) || !Enum.GetNames<AgentDefinitionResourceKind>().Contains(body.Kind))
                throw new ArgumentException("Use a supported Space file resource kind.");
            return Results.Json(await service.SaveResourceAsync(actor.PrincipalId, actor.TenantId, spaceId, resourceId, body.SourcePath, body.LogicalPath, kind,
                body.ExpectedRevision, body.ExpectedSpaceRevision, body.ExpectedResourceRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        group.MapDelete("/{resourceId:guid}", (Guid spaceId, Guid resourceId, long expectedRevision, long expectedSpaceRevision, long expectedResourceRevision,
            HttpContext http, TenantSpaceService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.DeleteResourceAsync(actor.PrincipalId, actor.TenantId, spaceId, resourceId, expectedRevision, expectedSpaceRevision,
                expectedResourceRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false);
            return Results.NoContent();
        }));
    }
}
