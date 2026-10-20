using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

public static class TenantEmailMonitoringEndpoints
{
    public sealed record ConfigureRequest(long ExpectedRevision, long ExpectedCredentialRevision, long ExpectedWatchRevision, bool Enabled, int IntervalSeconds);
    public sealed record CheckRequest(long ExpectedRevision, long ExpectedCredentialRevision, long ExpectedWatchRevision);
    private static HumanSessionReference Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/credentials/{credentialId:guid}/monitoring").RequireAuthorization();
        group.MapGet("", (Guid credentialId, HttpContext http, TenantEmailMonitoringService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(await service.GetAsync(IdentityHttp.TenantContext(http).TenantId, credentialId, Human(http), ct).ConfigureAwait(false));
        }));
        group.MapPut("", (Guid credentialId, ConfigureRequest body, HttpContext http, TenantEmailMonitoringService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(await service.ChangeAsync(IdentityHttp.TenantContext(http).TenantId, credentialId, Human(http),
                body.ExpectedRevision, body.ExpectedCredentialRevision, body.ExpectedWatchRevision, false, body.Enabled, body.IntervalSeconds, ct).ConfigureAwait(false))));
        group.MapPost("/check", (Guid credentialId, CheckRequest body, HttpContext http, TenantEmailMonitoringService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            Results.Json(await service.ChangeAsync(IdentityHttp.TenantContext(http).TenantId, credentialId, Human(http),
                body.ExpectedRevision, body.ExpectedCredentialRevision, body.ExpectedWatchRevision, true, ct: ct).ConfigureAwait(false), statusCode: 202)));
    }
}
