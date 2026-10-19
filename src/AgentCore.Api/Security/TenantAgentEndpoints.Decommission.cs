using System.Text.Json.Serialization;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record DecommissionRequest(long ExpectedRevision, long ExpectedInstanceRevision, Guid OperationId);
    private static void MapDecommission(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{instanceId:guid}/decommission", (Guid instanceId, HttpContext http, TenantAgentDecommissionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(actor.TenantId, instanceId, Human(http), ct));
        }));
        group.MapDelete("/agents/{instanceId:guid}", (Guid instanceId, [Microsoft.AspNetCore.Mvc.FromBody] DecommissionRequest body, HttpContext http, TenantAgentDecommissionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.DecommissionAsync(actor.TenantId, instanceId, Human(http), body.ExpectedRevision, body.ExpectedInstanceRevision, body.OperationId, ct));
        }));
    }
}
