using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static class TenantBrowserEndpoints
{
    public static void Map(WebApplication app) => app.MapGet("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/browser",
        (Guid sessionId, HttpContext http, TenantBrowserStateService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new AgentCore.Application.Ports.HumanSessionReference(actor.AccountId,
                IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Results.Json(await service.ReadAsync(actor.TenantId, sessionId, human, ct).ConfigureAwait(false));
        })).RequireAuthorization();
}
