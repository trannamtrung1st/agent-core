using AgentCore.Api.Http;
using Microsoft.AspNetCore.Mvc;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Application.Ports;

namespace AgentCore.Api;

internal static class AdminCredentialEndpoints
{
    internal static void MapCredentials(this RouteGroupBuilder group)
    {
        group.MapGet("/email-accounts/{credentialId:guid}/monitoring", (Guid credentialId, HttpContext http, EmailMonitoringService monitoring, CancellationToken ct) => Run(async () =>
        { http.Response.Headers.CacheControl = "no-store"; return Results.Json(await monitoring.GetAsync(credentialId, ct)); }));
        group.MapPut("/email-accounts/{credentialId:guid}/monitoring", (Guid credentialId, AdminEmailMonitoringRequest request, EmailMonitoringService monitoring, CancellationToken ct) => Run(async () =>
            Results.Json(await monitoring.ConfigureAsync(credentialId, request.ExpectedRevision, request.Enabled, request.IntervalSeconds, ct))));
        group.MapPost("/email-accounts/{credentialId:guid}/monitoring/check", (Guid credentialId, AdminEmailMonitoringCheckRequest request, EmailMonitoringService monitoring, CancellationToken ct) => Run(async () =>
            Results.Json(await monitoring.CheckAsync(credentialId, request.ExpectedRevision, ct), statusCode: 202)));
        group.MapGet("/email-accounts/oauth/configuration", (IEmailOAuthService oauth) => Results.Json(new { configured = oauth.IsConfigured, synthetic = oauth.IsSynthetic }));
        group.MapGet("/email-accounts/{credentialId:guid}/connection", (Guid credentialId, IEmailOAuthService oauth, CancellationToken ct) => Run(async () => Results.Json(await oauth.ConnectionAsync(credentialId, ct))));
        group.MapPost("/email-accounts/{credentialId:guid}/oauth/start", (Guid credentialId, AdminEmailOAuthRequest request, HttpContext http, IEmailOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) => Run(async () =>
        {
            Positive(request.ExpectedRevision);
            var start = await oauth.StartAsync(credentialId, request.ExpectedRevision, (await profiles.GetLocalProfileAsync(ct)).ProfileId, ct);
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Cookies.Append(GoogleEmailOAuthEndpoints.CookieName, start.Correlation, GoogleEmailOAuthEndpoints.Cookie(start.SecureCookie, start.ExpiresAtUtc));
            return Results.Json(new { start.AuthorizationUrl, start.ExpiresAtUtc, start.Synthetic });
        }));
        group.MapPost("/email-accounts/{credentialId:guid}/connection/check", (Guid credentialId, AdminEmailOAuthRequest request, IEmailOAuthService oauth, CancellationToken ct) => Run(async () =>
        { Positive(request.ExpectedRevision); return Results.Json(await oauth.CheckAsync(credentialId, request.ExpectedRevision, ct)); }));
        group.MapPost("/email-accounts/{credentialId:guid}/oauth/disconnect", (Guid credentialId, AdminEmailOAuthRequest request, IEmailOAuthService oauth, CancellationToken ct) => Run(async () =>
        { Positive(request.ExpectedRevision); if (!request.Confirm) throw AgentCoreErrors.Validation("Explicit disconnect confirmation is required."); return Results.Json(await oauth.DisconnectAsync(credentialId, request.ExpectedRevision, ct)); }));
        group.MapPost("/email-accounts", (AdminCreateEmailAccountRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () =>
            Results.Json(Map(await service.CreateEmailAccountAsync(request.DisplayName, Profile(request.Profile), request.ProtectedValue, ct)), statusCode: 201)));
        group.MapPatch("/email-accounts/{credentialId:guid}", (Guid credentialId, AdminUpdateEmailAccountRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () =>
        {
            Positive(request.ExpectedRevision);
            return Results.Json(Map(await service.UpdateEmailAccountAsync(credentialId, request.ExpectedRevision, request.DisplayName, request.Status, Profile(request.Profile), ct)));
        }));
        group.MapPost("/email-accounts/{credentialId:guid}/test", (Guid credentialId, AdminTestEmailAccountRequest request, IEmailProviderFactory factory, CancellationToken ct) => Run(async () =>
        {
            Positive(request.ExpectedRevision);
            return Results.Json(await factory.TestAsync(credentialId, request.ExpectedRevision, request.Protocol, ct));
        }));
        group.MapGet("/email-send-operations/{draftId}", (string draftId, IEmailStore store, CancellationToken ct) => Run(async () =>
        {
            var operation = await store.GetSendAsync(draftId, ct) ?? throw AgentCoreErrors.NotFound("Email send receipt was not found.");
            return Results.Json(new { operation.DraftId, operation.AccountId, operation.DraftRevision, operation.ClaimedAtUtc,
                outcome = (operation.Result?.Outcome ?? EmailSendOutcome.Indeterminate).ToString(), operation.Result?.ProviderMessageId,
                operation.Result?.ErrorCode, operation.Result?.ErrorMessage });
        }));
        group.MapGet("/credentials", ([FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(new { items = (await service.ListAsync(ct)).Select(Map) })));
        group.MapGet("/credentials/{credentialId:guid}", (Guid credentialId, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(Map(await service.GetAsync(credentialId, ct)))));
        group.MapPatch("/container-registry-credentials/{credentialId:guid}", (Guid credentialId, AdminUpdateContainerRegistryRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedRevision); return Results.Json(Map(await service.UpdateContainerRegistryAsync(credentialId,request.ExpectedRevision,request.DisplayName,request.Status,
                RegistryProfile(request.Profile),ct))); }));
        group.MapPost("/container-registry-credentials", (AdminCreateContainerRegistryRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () =>
            Results.Json(Map(await service.CreateContainerRegistryAsync(request.DisplayName, request.Kind,
                RegistryProfile(request.Profile), request.ProtectedValue, ct)), statusCode: 201)));
        group.MapPost("/credentials", (AdminCreateCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(Map(await service.CreateAsync(request.DisplayName, request.Kind, request.Metadata, request.AllowedOrigins, request.ProtectedValue, ct)), statusCode: 201)));
        group.MapPatch("/credentials/{credentialId:guid}", (Guid credentialId, AdminUpdateCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedRevision); return Results.Json(Map(await service.UpdateAsync(credentialId, request.ExpectedRevision, request.DisplayName, request.Status, request.Metadata, request.AllowedOrigins, ct))); }));
        group.MapPut("/credentials/{credentialId:guid}/value", (Guid credentialId, AdminReplaceCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedRevision); return Results.Json(Map(await service.ReplaceAsync(credentialId, request.ExpectedRevision, request.ProtectedValue, ct))); }));
        group.MapDelete("/credentials/{credentialId:guid}", (Guid credentialId, long expectedRevision, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(expectedRevision); await service.DeleteAsync(credentialId, expectedRevision, ct); return Results.NoContent(); }));
        group.MapGet("/agent-instances/{instanceId:guid}/credential-bindings", (Guid instanceId, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(new { items = (await service.BindingsAsync(instanceId, ct)).Select(Map) })));
        group.MapPost("/agent-instances/{instanceId:guid}/credential-bindings", (Guid instanceId, AdminBindCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedInstanceRevision); return Results.Json(Map(await service.BindAsync(instanceId, request.CredentialId, request.Reference, request.ExpectedInstanceRevision, ct)), statusCode: 201); }));
        group.MapDelete("/agent-instances/{instanceId:guid}/credential-bindings/{bindingId:guid}", (Guid instanceId, Guid bindingId, long expectedRevision, long expectedInstanceRevision, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(expectedRevision); Positive(expectedInstanceRevision); await service.UnbindAsync(instanceId, bindingId, expectedRevision, expectedInstanceRevision, ct); return Results.NoContent(); }));
        group.MapPost("/agent-instances/{instanceId:guid}/browser-profile/reset", (Guid instanceId, AdminResetBrowserProfileRequest request, AgentBrowserProfileService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedInstanceRevision); if (!request.Confirm) throw AgentCoreErrors.Validation("Explicit browser profile reset confirmation is required.");
            await service.ResetAsync(instanceId, request.ExpectedInstanceRevision, ct); return Results.NoContent(); }));
    }
    private static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, out var parsed)
        && Enum.IsDefined(parsed) && Enum.GetNames<T>().Contains(value) ? parsed : throw AgentCoreErrors.Validation("Unknown email preset or TLS mode.");
    private static EmailAccountProfile Profile(EmailAccountProfileDto value)
    {
        if (value is null) throw AgentCoreErrors.Validation("Account profile is required.");
        var preset = Parse<EmailProviderPreset>(value.Preset);
        if (preset == EmailProviderPreset.Gmail) return EmailAccountProfile.Gmail(value.EmailAddress);
        if (value.ImapEndpoint is null || value.SmtpEndpoint is null) throw AgentCoreErrors.Validation("Typed Custom mail endpoints are required.");
        return new(preset, value.EmailAddress, value.ImapUsername, value.SmtpUsername,
            new(value.ImapEndpoint.Host, value.ImapEndpoint.Port, Parse<MailTlsMode>(value.ImapEndpoint.TlsMode)),
            new(value.SmtpEndpoint.Host, value.SmtpEndpoint.Port, Parse<MailTlsMode>(value.SmtpEndpoint.TlsMode)));
    }
    private static EmailAccountProfileDto Map(EmailAccountProfile value) => new(value.Preset.ToString(), value.EmailAddress,
        value.ImapUsername, value.SmtpUsername, value.ImapEndpoint is { } imap ? new(imap.Host, imap.Port, imap.TlsMode.ToString()) : null,
        value.SmtpEndpoint is { } smtp ? new(smtp.Host, smtp.Port, smtp.TlsMode.ToString()) : null, value.AuthMethod.ToString(),
        value.GoogleSubject, value.GrantedScopes, value.ConnectionStatus.ToString(), value.AuthEpoch, value.ConnectedAtUtc, value.Synthetic);
    private static void Positive(long revision) { if (revision < 1) throw AgentCoreErrors.Validation("Expected revision must be positive."); }
    private static ContainerRegistryProfile RegistryProfile(ContainerRegistryProfileDto? profile) => profile is null
        ? throw AgentCoreErrors.Validation("A Container Registry profile is required.")
        : new(profile.Registry, profile.Username, profile.RepositoryPrefixes, profile.ExpiresAtUtc);
    private static async Task<IResult> Run(Func<Task<IResult>> action) { try { return await action(); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } }
    internal static AdminCredentialResponse Map(CredentialView c) => new(c.CredentialId, c.DisplayName, c.Kind, c.Status, c.Metadata, c.AllowedOrigins, c.Revision, c.CreatedAtUtc, c.UpdatedAtUtc, c.BindingCount, c.Purpose, c.EmailAccount is null ? null : Map(c.EmailAccount), c.DriveAccount is null ? null : new(c.DriveAccount.EmailAddress, c.DriveAccount.ConnectionStatus.ToString(), c.DriveAccount.AuthEpoch, c.DriveAccount.ConnectedAtUtc, c.DriveAccount.ReconnectRequiredAtUtc, c.DriveAccount.Synthetic), c.ContainerRegistry is { } registry ? new(registry.Registry, registry.Username, registry.RepositoryPrefixes, registry.ExpiresAtUtc) : null);
    private static AdminCredentialBindingResponse Map(CredentialBindingView b) => new(b.BindingId, b.CredentialId, b.Reference, b.Revision, Map(b.Credential));
}
