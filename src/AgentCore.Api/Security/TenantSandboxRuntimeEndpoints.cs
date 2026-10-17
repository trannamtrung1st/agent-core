using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static class TenantSandboxRuntimeEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/sandbox").RequireAuthorization();
        group.MapGet("", (Guid sessionId, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => Read(sessionId, null, http, service, ct));
        group.MapGet("/executions/{executionId:guid}", (Guid sessionId, Guid executionId, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => Read(sessionId, executionId, http, service, ct));
    }
    private static Task<IResult> Read(Guid session, Guid? execution, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
        return Results.Json(await service.ReadAsync(actor.TenantId, session, human, execution, ct).ConfigureAwait(false));
    });
}
