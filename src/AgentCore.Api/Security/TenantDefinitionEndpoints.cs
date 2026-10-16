using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Security;

public static partial class TenantDefinitionEndpoints
{
    public sealed record CreateRequest(string Name, long ExpectedRevision);
    public sealed record UpdateRequest(AgentDefinitionCandidate Candidate, long ExpectedRevision, long ExpectedDraftRevision);
    public sealed record PublishRequest(long ExpectedRevision, long ExpectedDraftRevision);
    public sealed record ForkRequest(int Version, long ExpectedRevision);
    public sealed record DeprecateRequest(long ExpectedRevision, long ExpectedMetadataRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        MapChildren(group);
        MapDeletion(group);
        MapTemplates(group);
        group.MapGet("/agent-definitions", (int? page, int? pageSize, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ListAsync(a.PrincipalId, a.TenantId, page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agent-definitions", (CreateRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.CreateAsync(a.PrincipalId, a.TenantId, body.Name, body.ExpectedRevision, ct).ConfigureAwait(false), statusCode: 201);
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/drafts", (Guid definitionId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(new { items = await service.DraftsAsync(a.PrincipalId, a.TenantId, definitionId, ct).ConfigureAwait(false) });
        }));
        group.MapGet("/definition-drafts/{draftId:guid}", (Guid draftId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadDraftAsync(a.PrincipalId, a.TenantId, draftId, ct).ConfigureAwait(false));
        }));
        group.MapPut("/definition-drafts/{draftId:guid}", (Guid draftId, UpdateRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.UpdateAsync(a.PrincipalId, a.TenantId, draftId, body.Candidate, body.ExpectedRevision, body.ExpectedDraftRevision, ct).ConfigureAwait(false));
        }));
        group.MapPost("/definition-drafts/{draftId:guid}/publish", (Guid draftId, PublishRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.PublishAsync(a.PrincipalId, a.TenantId, draftId, body.ExpectedRevision, body.ExpectedDraftRevision, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agent-definitions/{definitionId:guid}/drafts", (Guid definitionId, ForkRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ForkAsync(a.PrincipalId, a.TenantId, definitionId, body.Version, body.ExpectedRevision, ct).ConfigureAwait(false), statusCode: 201);
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/publications", (Guid definitionId, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(new { items = await service.PublicationsAsync(a.PrincipalId, a.TenantId, definitionId, ct).ConfigureAwait(false) });
        }));
        group.MapGet("/agent-definitions/{definitionId:guid}/publications/{version:int}", (Guid definitionId, int version, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadPublicationAsync(a.PrincipalId, a.TenantId, definitionId, version, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agent-definitions/{definitionId:guid}/publications/{version:int}/deprecate", (Guid definitionId, int version, DeprecateRequest body, HttpContext http, TenantDefinitionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.DeprecateAsync(a.PrincipalId, a.TenantId, definitionId, version, body.ExpectedRevision, body.ExpectedMetadataRevision, ct).ConfigureAwait(false));
        }));
    }
}
