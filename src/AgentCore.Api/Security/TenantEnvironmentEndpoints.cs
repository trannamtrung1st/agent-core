using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Spaces;

namespace AgentCore.Api.Security;

public static class TenantEnvironmentEndpoints
{
    public static void Map(WebApplication app)
    {
        var secrets = app.MapGroup("/api/v2/tenants/{tenantId:guid}/secrets").RequireAuthorization();
        secrets.MapGet("", (int? page, int? pageSize, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ListAsync(a.PrincipalId, a.TenantId, page ?? 0, pageSize ?? 30, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        secrets.MapGet("/{secretId:guid}", (Guid secretId, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(a.PrincipalId, a.TenantId, secretId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        secrets.MapGet("/{secretId:guid}/access", (Guid secretId, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.AccessAsync(a.PrincipalId, a.TenantId, secretId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        secrets.MapPost("", (SecretCreateRequest body, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.CreateAsync(a.PrincipalId, a.TenantId, body.Name, body.Description, body.ProtectedValue, body.ExpectedRevision, IdentityHttp.TenantHuman(http), ct, secretId: body.SecretId).ConfigureAwait(false), statusCode: 201);
        }));
        secrets.MapPatch("/{secretId:guid}", (Guid secretId, SecretUpdateRequest body, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.UpdateAsync(a.PrincipalId, a.TenantId, secretId, body.Name, body.Description, Parse<SecretStatus>(body.Status), body.ExpectedRevision,
                body.ExpectedSecretRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        secrets.MapPut("/{secretId:guid}/value", (Guid secretId, SecretReplaceRequest body, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReplaceAsync(a.PrincipalId, a.TenantId, secretId, body.ProtectedValue, body.ExpectedRevision, body.ExpectedSecretRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        secrets.MapDelete("/{secretId:guid}", (Guid secretId, long expectedRevision, long expectedSecretRevision, HttpContext http, TenantSecretService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            await service.DeleteAsync(a.PrincipalId, a.TenantId, secretId, expectedRevision, expectedSecretRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false); return Results.NoContent();
        }));
        var env = app.MapGroup("/api/v2/tenants/{tenantId:guid}/spaces/{spaceId:guid}/environments").RequireAuthorization();
        env.MapGet("", (Guid spaceId, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(new { items = await service.ListAsync(a.PrincipalId, a.TenantId, spaceId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false) });
        }));
        env.MapGet("/{environmentId:guid}", (Guid spaceId, Guid environmentId, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        env.MapPost("", (Guid spaceId, EnvironmentCreateRequest body, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SaveAsync(a.PrincipalId, a.TenantId, spaceId, body.EnvironmentId, body.Name, body.Description, body.ExpectedRevision, body.ExpectedSpaceRevision, 0,
                IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false), statusCode: 201);
        }));
        env.MapPatch("/{environmentId:guid}", (Guid spaceId, Guid environmentId, EnvironmentUpdateRequest body, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            if (body.ExpectedEnvironmentRevision < 1) throw new ArgumentException("An existing Environment revision is required.");
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SaveAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, body.Name, body.Description, body.ExpectedRevision, body.ExpectedSpaceRevision,
                body.ExpectedEnvironmentRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        env.MapDelete("/{environmentId:guid}", (Guid spaceId, Guid environmentId, long expectedRevision, long expectedSpaceRevision, long expectedEnvironmentRevision,
            HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            await service.DeleteAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, expectedRevision, expectedSpaceRevision, expectedEnvironmentRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false); return Results.NoContent();
        }));
        env.MapGet("/{environmentId:guid}/variables", (Guid spaceId, Guid environmentId, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(new { items = await service.VariablesAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false) });
        }));
        env.MapGet("/{environmentId:guid}/variables/{name}", (Guid spaceId, Guid environmentId, string name, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.VariableAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, name, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        env.MapPut("/{environmentId:guid}/variables/{name}", (Guid spaceId, Guid environmentId, string name, EnvironmentVariableRequest body,
            HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SaveVariableAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, name, Parse<EnvironmentVariableKind>(body.Kind), body.Description,
                body.PlainValue, body.SecretId, body.CredentialId, body.ExpectedRevision, body.ExpectedSpaceRevision, body.ExpectedEnvironmentRevision,
                body.ExpectedVariableRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false));
        }));
        env.MapDelete("/{environmentId:guid}/variables/{name}", (Guid spaceId, Guid environmentId, string name, long expectedRevision, long expectedSpaceRevision,
            long expectedEnvironmentRevision, long expectedVariableRevision, HttpContext http, TenantEnvironmentService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            await service.DeleteVariableAsync(a.PrincipalId, a.TenantId, spaceId, environmentId, name, expectedRevision, expectedSpaceRevision, expectedEnvironmentRevision,
                expectedVariableRevision, IdentityHttp.TenantHuman(http), ct).ConfigureAwait(false); return Results.NoContent();
        }));
    }
    private static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, false, out var parsed)
        && Enum.IsDefined(parsed) && Enum.GetNames<T>().Contains(value) ? parsed : throw new ArgumentException("Unknown resource kind or status.");
}
