using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Security;

public static class TenantSandboxProfileEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sandbox-profiles").RequireAuthorization();
        group.MapGet("", (int? page, int? pageSize, string? search, HttpContext http, TenantSandboxProfileService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.TenantId, Human(http), page ?? 0, pageSize ?? 30, search, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize, result.CanCreate, result.AllReadable, result.SecurityRevision });
        }));
        group.MapGet("/{profileId}", (string profileId, long? revision, HttpContext http, TenantSandboxProfileService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.ReadAsync(actor.TenantId, profileId, Human(http), revision, ct).ConfigureAwait(false)));
        }));
        group.MapPost("", (TenantSandboxProfileSaveRequest body, HttpContext http, TenantSandboxProfileService service, CancellationToken ct) => Save(null, body, http, service, ct));
        group.MapPut("/{profileId}", (string profileId, TenantSandboxProfileSaveRequest body, HttpContext http, TenantSandboxProfileService service, CancellationToken ct) => Save(profileId, body, http, service, ct));
        group.MapGet("/{profileId}/verification", (string profileId, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => ReadVerification(profileId, null, http, service, ct));
        group.MapGet("/{profileId}/verification/{attemptId:guid}", (string profileId, Guid attemptId, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => ReadVerification(profileId, attemptId, http, service, ct));
        group.MapPost("/{profileId}/verify", (string profileId, TenantSandboxVerificationRequest body, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => Verify(profileId, false, body, http, service, ct));
        group.MapPost("/{profileId}/check-image", (string profileId, TenantSandboxVerificationRequest body, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => Verify(profileId, true, body, http, service, ct));
    }
    private static Task<IResult> ReadVerification(string id, Guid? attemptId, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        return Results.Json(SafeVerification(await service.ReadAsync(actor.TenantId, id, Human(http), attemptId, ct).ConfigureAwait(false)));
    });
    private static Task<IResult> Verify(string id, bool imageOnly, TenantSandboxVerificationRequest body, HttpContext http, TenantSandboxVerificationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        return Results.Json(SafeVerification(await service.VerifyAsync(actor.TenantId, id, Human(http), body.ExpectedRevision, body.ExpectedProfileRevision, imageOnly, ct).ConfigureAwait(false)));
    });
    private static object SafeVerification(TenantSandboxVerificationView view)
    {
        return new { attempt = SafeAttempt(view.Attempt), view.CurrentProfileRevision, view.SecurityRevision };
    }
    internal static object? SafeAttempt(SandboxVerificationAttempt? a) => a is null ? null : new { a.AttemptId, a.ProfileId, a.ProfileRevision, a.InstanceId, a.InstanceRevision, a.ImageOnly, status = a.Status.ToString(), a.StartedAt,
        a.Active, accepted = a.Status == SandboxVerificationStatus.Succeeded && !a.ImageOnly, result = a.Result is null ? null : Verification(a.Result) };
    internal static SandboxSetupVerificationResponse Verification(SandboxProfileVerification r) => new(r.Passed, r.Message, r.ProfileId, r.ProfileRevision, r.InstanceRevision, r.VerifiedAt,
        r.ExitCode, r.Stdout, r.Stderr, r.Truncated, r.DurationMs, r.DiagnosticId);
    private static Task<IResult> Save(string? id, TenantSandboxProfileSaveRequest body, HttpContext http, TenantSandboxProfileService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
    {
        var actor = IdentityHttp.TenantContext(http);
        var input = new TenantSandboxProfileInput(body.Name, body.Description, body.Enabled, body.ImageReference, body.InitializationScript, body.Cpu, body.MemoryMiB,
            body.Pids, body.MaxCommandSeconds, body.IdleTtlSeconds, body.MaxLifetimeSeconds, body.StorageBytes, body.RegistryCredentialId);
        return Results.Json(Safe(await service.SaveAsync(actor.TenantId, id, Human(http), input, body.ExpectedRevision, body.ExpectedProfileRevision, ct).ConfigureAwait(false)), statusCode: id is null ? 201 : 200);
    });
    private static HumanSessionReference Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    // Credential epochs, tenant ownership, runtime admission and verification claims are never client inputs.
    private static object Safe(TenantSandboxProfileView value)
    {
        var p = value.Profile;
        return new { profile = new SandboxProfileResponse(p.Id, p.Revision, p.Name, p.Description, p.Enabled, p.ImageReference, p.InitializationScript,
            p.Cpu, p.MemoryMiB, p.Pids, p.MaxCommandSeconds, p.IdleTtlSeconds, p.MaxLifetimeSeconds, p.StorageBytes, value.Verification?.VerifiedAt, value.Verification?.Message,
            Verification: value.Verification is null ? null : Verification(value.Verification), RegistryCredentialId: p.RegistryCredentialId,
            Availability: !p.Enabled ? "Disabled" : value.Availability?.Status ?? "NeedsVerification",
            AvailabilityMessage: value.Availability?.Message ?? "Catalog configuration grants no execution authority."), value.CanManage, value.SecurityRevision };
    }
}
