using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static class TenantBrowserEndpoints
{
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record ProfileResetRequest(Guid OperationId, long ExpectedRevision, long ExpectedInstanceRevision, string Witness);
    private static AgentCore.Application.Ports.HumanSessionReference ProfileHuman(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    public static void MapProfile(WebApplication app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/browser/profile", (Guid instanceId, HttpContext http, TenantBrowserProfileService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(await service.ReadAsync(IdentityHttp.TenantContext(http).TenantId, instanceId, ProfileHuman(http), ct)))).RequireAuthorization();
        app.MapPost("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/browser/profile/reset", (Guid instanceId, ProfileResetRequest body, HttpContext http, TenantBrowserProfileService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(await service.ResetAsync(IdentityHttp.TenantContext(http).TenantId, instanceId, ProfileHuman(http), body.OperationId,
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.Witness, ct)))).RequireAuthorization();
    }
    public static void Map(WebApplication app)
    {
        MapProfile(app);
        app.MapGet("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/browser",
        (Guid sessionId, HttpContext http, TenantBrowserStateService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new AgentCore.Application.Ports.HumanSessionReference(actor.AccountId,
                IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Results.Json(await service.ReadAsync(actor.TenantId, sessionId, human, ct).ConfigureAwait(false));
        })).RequireAuthorization();
    }
}
