using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static class TenantCredentialOAuthEndpoints
{
    private const string Root = "/api/v2/integrations/tenant-google/oauth";
    private static string CookieName(Guid id) => "AgentCore.CredentialOAuth." + id.ToString("N");
    private static CookieOptions Cookie(HttpContext http, DateTimeOffset? expires = null) => new()
    { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = Root, IsEssential = true, Expires = expires };
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/credentials/{credentialId:guid}/oauth").RequireAuthorization();
        group.MapGet("", (Guid credentialId, HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SetupAsync(actor.PrincipalId, actor.TenantId, credentialId, ct).ConfigureAwait(false));
        }));
        group.MapPost("/start", (Guid credentialId, TenantCredentialOAuthRequest body, HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.StartAsync(actor.TenantId, credentialId, Initiator(http, actor), body.ExpectedRevision, body.ExpectedCredentialRevision, ct).ConfigureAwait(false);
            http.Response.Cookies.Append(CookieName(credentialId), result.Correlation, Cookie(http, result.ExpiresAt));
            return Results.Json(new { result.AuthorizationUrl, result.ExpiresAt, revision = result.SecurityRevision });
        })).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapPost("/disconnect", (Guid credentialId, TenantCredentialOAuthRequest body, HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.DisconnectAsync(actor.TenantId, credentialId, Initiator(http, actor), body.ExpectedRevision, body.ExpectedCredentialRevision, ct).ConfigureAwait(false);
            return Results.Json(new { result.Credential.TenantId, credential = AdminCredentialEndpoints.Map(result.Credential.Credential), result.ProviderRevoked });
        })).WithMetadata(new RequestSizeLimitAttribute(8192));
        app.MapGet(Root + "/callback", (HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => Purpose(http, async () =>
            await Complete(http, service, http.Request.Query["state"], http.Request.Query["code"], http.Request.Query["error"], false, ct).ConfigureAwait(false))).AllowAnonymous();
        app.MapGet(Root + "/synthetic", (HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => Purpose(http, async () =>
        {
            var state = http.Request.Query["state"].ToString();
            var purpose = await service.SyntheticConsentAsync(state, Correlation(http, state), IdentityHttp.AccountId(http.User), ct).ConfigureAwait(false);
            var label = purpose == CredentialPurpose.EmailAccount ? "Gmail" : "Google Drive";
            return Results.Content($"<!doctype html><html lang='en'><meta name='viewport' content='width=device-width'><title>Synthetic {label} authorization</title><main><h1>Connect Synthetic {label}</h1><p>This offline test uses no Google account or network connection. Connecting does not grant an agent permission to use the account.</p><form method='post'><input type='hidden' name='state' value='{WebUtility.HtmlEncode(state)}'><button name='decision' value='authorize'>Connect Synthetic account</button><button name='decision' value='cancel'>Cancel</button></form></main></html>", "text/html");
        })).AllowAnonymous();
        app.MapPost(Root + "/synthetic", (HttpContext http, [FromServices] TenantCredentialOAuthService service, CancellationToken ct) => Purpose(http, async () =>
        {
            if (http.Request.ContentLength > 4096) return Results.StatusCode(413);
            var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 4096;
            if (!http.Request.HasFormContentType) return Results.StatusCode(415);
            IFormCollection form;
            try { form = await http.Request.ReadFormAsync(new FormOptions { KeyLengthLimit = 32, ValueLengthLimit = 256, ValueCountLimit = 4,
                MultipartBodyLengthLimit = 4096, MultipartHeadersCountLimit = 8, MultipartHeadersLengthLimit = 1024 }, ct).ConfigureAwait(false); }
            catch (InvalidDataException) { throw new BadHttpRequestException("Invalid authorization form.", 400); }
            return await Complete(http, service, form["state"], form["decision"] == "authorize" ? "synthetic-consent" : null,
                form["decision"] == "authorize" ? null : "access_denied", true, ct).ConfigureAwait(false);
        })).AllowAnonymous();
    }
    private static CredentialOAuthInitiator Initiator(HttpContext http, AuthenticatedTenant actor) =>
        new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("oauth_initiator_unavailable"), actor.PrincipalId);
    private static string? Correlation(HttpContext http, string? state) => TenantCredentialOAuthService.TryState(state, out var credential, out _)
        ? http.Request.Cookies[CookieName(credential)] : null;
    private static async Task<IResult> Complete(HttpContext http, TenantCredentialOAuthService service, string? state, string? code, string? error, bool synthetic, CancellationToken ct)
    {
        var result = await service.CompleteAsync(state, Correlation(http, state), IdentityHttp.AccountId(http.User), code, error, synthetic, ct).ConfigureAwait(false);
        http.Response.Cookies.Delete(CookieName(result.CredentialId), Cookie(http));
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = $"/t/{result.TenantId:D}/integrations/credentials?credentialId={result.CredentialId:D}&connection={result.Outcome}";
        return Results.Empty;
    }
    private static Task<IResult> Purpose(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        return IdentityHttp.Respond(() =>
        {
            var policy = http.RequestServices.GetRequiredService<IdentityHttpOptions>();
            if (!http.Request.IsHttps && (!policy.AllowInsecureLocalHttp || !TrustedLocalCaller.IsLoopback(http)))
                throw new AgentCoreException("https_required", "A secure authorization connection is required.", 400);
            return action();
        });
    }
}
