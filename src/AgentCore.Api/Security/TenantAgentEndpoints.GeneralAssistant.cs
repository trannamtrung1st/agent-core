using AgentCore.Application.Security;
namespace AgentCore.Api.Security;
public static partial class TenantAgentEndpoints
{
    public sealed record ResetDefaultsRequest(Guid OperationId, long ExpectedRevision, long ExpectedInstanceRevision);
    private static void MapGeneralAssistant(RouteGroupBuilder group)
    {
        group.MapPost("/general-assistant/reconcile", (HttpContext http, TenantAgentService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            var ready = await service.RetryGeneralAssistantSetupAsync(actor.TenantId, Human(http), ct);
            return Results.Json(new { status = ready ? "ready" : "setup_pending" });
        }));
        group.MapPost("/agents/{instanceId:guid}/reset-defaults", (Guid instanceId, ResetDefaultsRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ResetDefaultsAsync(actor.PrincipalId, actor.TenantId, instanceId, body.OperationId,
                body.ExpectedRevision, body.ExpectedInstanceRevision, ct, Human(http)));
        }));
    }
}
