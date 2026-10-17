using AgentCore.Application.Security;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    private static void MapSandboxVerification(RouteGroupBuilder group)
    {
        group.MapPost("/agents/{instanceId:guid}/sandbox/verify", (Guid instanceId, TenantSandboxInstanceVerificationRequest body, HttpContext http,
            TenantSandboxInstanceVerificationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(SafeSandboxVerification(await service.VerifyAsync(actor.TenantId, instanceId, body.ProfileId, Human(http), body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.ExpectedProfileRevision, ct).ConfigureAwait(false)));
        }));
        group.MapGet("/agents/{instanceId:guid}/sandbox/verification", (Guid instanceId, string profileId, HttpContext http,
            TenantSandboxInstanceVerificationService service, CancellationToken ct) => ReadSandboxVerification(instanceId, profileId, null, http, service, ct));
        group.MapGet("/agents/{instanceId:guid}/sandbox/verification/{attemptId:guid}", (Guid instanceId, string profileId, Guid attemptId, HttpContext http,
            TenantSandboxInstanceVerificationService service, CancellationToken ct) => ReadSandboxVerification(instanceId, profileId, attemptId, http, service, ct));
    }
    private static Task<IResult> ReadSandboxVerification(Guid instance, string profile, Guid? attempt, HttpContext http,
        TenantSandboxInstanceVerificationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        return Results.Json(SafeSandboxVerification(await service.ReadAsync(actor.TenantId, instance, profile, Human(http), attempt, ct).ConfigureAwait(false)));
    });
    private static object SafeSandboxVerification(TenantSandboxInstanceVerificationView view) => new { view.InstanceId, attempt = TenantSandboxProfileEndpoints.SafeAttempt(view.Attempt),
        observation = view.Observation is null ? null : TenantSandboxProfileEndpoints.Verification(view.Observation), view.Status, view.CanVerify, view.CurrentInstanceRevision, view.CurrentProfileRevision, view.SecurityRevision };
}
