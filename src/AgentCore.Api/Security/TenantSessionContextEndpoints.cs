using AgentCore.Application.Security;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Security;

public static class TenantSessionContextEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/context").RequireAuthorization();
        group.MapGet("", (Guid sessionId, HttpContext http, TenantSessionContextService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var human = IdentityHttp.TenantHuman(http);
            return Results.Json(await service.ReadAsync(actor.TenantId, sessionId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), ct).ConfigureAwait(false));
        }));
        group.MapGet("/options", (Guid sessionId, Guid? spaceId, int? page, HttpContext http, TenantSessionContextService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var human = IdentityHttp.TenantHuman(http);
            return Results.Json(await service.OptionsAsync(actor.TenantId, sessionId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), spaceId, page ?? 0, ct).ConfigureAwait(false));
        }));
        group.MapPost("/switch", (Guid sessionId, SessionContextSwitchRequest body, HttpContext http, TenantSessionContextService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var human = IdentityHttp.TenantHuman(http);
            return Results.Json(await service.SwitchAsync(actor.TenantId, sessionId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), body.TransitionId,
                body.SpaceId, body.EnvironmentId, body.ExpectedRevision, body.ExpectedSessionRevision, body.ExpectedSelectionRevision, ct).ConfigureAwait(false));
        }));
    }
}
