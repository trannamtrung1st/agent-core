using AgentCore.Api.Mapping;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;

namespace AgentCore.Api.Security;

public static class TenantCredentialEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/credentials").RequireAuthorization();
        group.MapGet("/{credentialId:guid}/access", (Guid credentialId, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.AccessAsync(actor.PrincipalId, actor.TenantId, credentialId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false));
        }));
        group.MapGet("", (int? page, int? pageSize, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.PrincipalId, actor.TenantId, page ?? 0, pageSize ?? 30, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{credentialId:guid}", (Guid credentialId, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.ReadAsync(actor.PrincipalId, actor.TenantId, credentialId, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false)));
        }));
        group.MapPost("", (TenantCredentialCreateRequest body, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var input = new TenantCredentialCreate(body.DisplayName, Parse<CredentialKind>(body.Kind), Parse<CredentialPurpose>(body.Purpose),
                body.Metadata, body.AllowedOrigins, body.ProtectedValue, Email(body.EmailAccount), Registry(body.ContainerRegistry));
            return Results.Json(Safe(await service.CreateAsync(actor.PrincipalId, actor.TenantId, input, body.ExpectedRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false)), statusCode: 201);
        }));
        group.MapPatch("/{credentialId:guid}", (Guid credentialId, TenantCredentialUpdateRequest body, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var input = new TenantCredentialUpdate(body.DisplayName, Parse<CredentialStatus>(body.Status), body.Metadata, body.AllowedOrigins, Email(body.EmailAccount), Registry(body.ContainerRegistry));
            return Results.Json(Safe(await service.UpdateAsync(actor.PrincipalId, actor.TenantId, credentialId, input, body.ExpectedRevision, body.ExpectedCredentialRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false)));
        }));
        group.MapPut("/{credentialId:guid}/value", (Guid credentialId, TenantCredentialReplaceRequest body, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.ReplaceAsync(actor.PrincipalId, actor.TenantId, credentialId, body.ProtectedValue,
                body.ExpectedRevision, body.ExpectedCredentialRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false)));
        }));
        group.MapDelete("/{credentialId:guid}", (Guid credentialId, long expectedRevision, long expectedCredentialRevision, HttpContext http, TenantCredentialService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.DeleteAsync(actor.PrincipalId, actor.TenantId, credentialId, expectedRevision, expectedCredentialRevision, ct, IdentityHttp.TenantHuman(http)).ConfigureAwait(false);
            return Results.NoContent();
        }));
    }
    private static object Safe(TenantCredentialView item) => new { item.TenantId, credential = CredentialHttpMapping.Map(item.Credential) };
    private static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, false, out var parsed)
        && Enum.IsDefined(parsed) && Enum.GetNames<T>().Contains(value) ? parsed : throw AgentCoreErrors.Validation("Unknown credential kind, purpose, status or mail option.");
    private static ContainerRegistryProfile? Registry(ContainerRegistryProfileDto? input) => input is null ? null
        : new(input.Registry, input.Username, input.RepositoryPrefixes, input.ExpiresAtUtc);
    private static EmailAccountProfile? Email(TenantEmailProfileInput? input)
    {
        if (input is null) return null;
        var preset = Parse<EmailProviderPreset>(input.Preset);
        if (preset == EmailProviderPreset.Gmail)
        {
            if (input.ImapEndpoint is not null || input.SmtpEndpoint is not null || input.ImapUsername != "" || input.SmtpUsername != "")
                throw AgentCoreErrors.Validation("Gmail authorization uses OAuth rather than mail endpoints.");
            return EmailAccountProfile.Gmail(input.EmailAddress);
        }
        if (input.ImapEndpoint is null || input.SmtpEndpoint is null) throw AgentCoreErrors.Validation("Custom mail requires TLS endpoints.");
        return new(preset, input.EmailAddress, input.ImapUsername, input.SmtpUsername,
            new(input.ImapEndpoint.Host, input.ImapEndpoint.Port, Parse<MailTlsMode>(input.ImapEndpoint.TlsMode)),
            new(input.SmtpEndpoint.Host, input.SmtpEndpoint.Port, Parse<MailTlsMode>(input.SmtpEndpoint.TlsMode)));
    }
}
