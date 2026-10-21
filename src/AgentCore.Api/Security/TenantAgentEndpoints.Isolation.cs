using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    public sealed record IsolationApplyRequest(long ExpectedRevision, IsolationOwnerRevisions Expected, IsolationDraft Draft, string Fingerprint);
    private static void MapIsolation(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{instanceId:guid}/state-isolation", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadIsolationAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false);
            return Results.Json(new { agent = Safe(value.Agent), value.Configuration, value.SecurityRevision, value.CanUpdate });
        }));
        group.MapPost("/agents/{instanceId:guid}/state-isolation/presets/{preset}", (Guid instanceId, string preset, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.PreviewIsolationAsync(a.TenantId, instanceId, Human(http), null, preset, ct).ConfigureAwait(false));
        }));
        group.MapPost("/agents/{instanceId:guid}/state-isolation/preview", (Guid instanceId, IsolationDraft body, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.PreviewIsolationAsync(a.TenantId, instanceId, Human(http), body, ct: ct).ConfigureAwait(false));
        }));
        group.MapPost("/agents/{instanceId:guid}/state-isolation/apply", (Guid instanceId, IsolationApplyRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var updated = await service.ApplyIsolationAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.Expected, body.Draft, body.Fingerprint, Human(http), ct).ConfigureAwait(false);
            return Results.Json(Safe(updated));
        }));
    }
}
