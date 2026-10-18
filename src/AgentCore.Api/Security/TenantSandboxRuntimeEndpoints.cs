using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Security;

public static class TenantSandboxRuntimeEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/sandbox").RequireAuthorization();
        group.MapGet("", (Guid sessionId, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => Read(sessionId, null, http, service, ct));
        group.MapGet("/controls", (Guid sessionId, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            return Results.Json(await service.ReadControlsAsync(actor.TenantId, sessionId, human, ct).ConfigureAwait(false));
        }));
        group.MapGet("/executions/{executionId:guid}", (Guid sessionId, Guid executionId, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => Read(sessionId, executionId, http, service, ct));
        group.MapPost("/executions/{executionId:guid}/stop", (Guid sessionId, Guid executionId, TenantSandboxProcessStopRequest request, HttpContext http, TenantSandboxControlService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var result = await service.RequestProcessStopAsync(actor.TenantId, human, new(sessionId, request.ExpectedSessionRevision, request.ExpectedGeneration,
                request.ExpectedLeaseRevision, executionId, request.ExpectedExecutionRevision), request.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/tenants/{actor.TenantId}/sessions/{sessionId}/sandbox/executions/{executionId}", result);
        }));
        group.MapPost("/profile", (Guid sessionId, TenantSandboxProfileSwitchRequest request, HttpContext http, TenantSandboxControlService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var result = await service.RequestProfileSwitchAsync(actor.TenantId, human, new(sessionId, request.ExpectedSessionRevision, request.ExpectedGeneration,
                request.ExpectedLeaseRevision, request.ExpectedExecutionId, request.ProfileId, request.ExpectedProfileRevision, request.ExpectedInstanceRevision), request.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/tenants/{actor.TenantId}/sessions/{sessionId}/sandbox", result);
        }));
        group.MapPost("/reset", (Guid sessionId, TenantSandboxResetRequest request, HttpContext http, TenantSandboxControlService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var result = await service.RequestResetAsync(actor.TenantId, human, new(sessionId, request.ExpectedSessionRevision, request.ExpectedGeneration,
                request.ExpectedLeaseRevision, request.ExpectedExecutionId), request.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/tenants/{actor.TenantId}/sessions/{sessionId}/sandbox", result);
        }));
    }
    private static Task<IResult> Read(Guid session, Guid? execution, HttpContext http, TenantSandboxRuntimeReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        var human = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
        return Results.Json(await service.ReadAsync(actor.TenantId, session, human, execution, ct).ConfigureAwait(false));
    });
}
