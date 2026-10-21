using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    private static void MapRuntimeState(RouteGroupBuilder group) => group.MapGet("/agents/{instanceId:guid}/runtime-state",
        (Guid instanceId, Guid? sessionId, HttpContext http, TenantRuntimeStateService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadAsync(a.TenantId, instanceId, Human(http), sessionId, ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), value.Observation, value.SecurityRevision });
        }));
}
