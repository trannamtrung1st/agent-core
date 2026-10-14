using System.Net;
using Microsoft.AspNetCore.Mvc;
using AgentCore.Api.Http;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
namespace AgentCore.Api;

internal static class GoogleDriveOAuthEndpoints
{
    private const string Root = "/api/v2/integrations/google-drive/oauth";
    private const string CookieName = "AgentCore.DriveOAuth";
    private static CookieOptions Cookie(bool secure, DateTimeOffset? expires = null) => new() { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Path = Root, Expires = expires, IsEssential = true };
    private sealed record CreateAccount(string DisplayName);
    private sealed record Revision(long ExpectedRevision);
    internal static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v2/admin/credentials").AddEndpointFilter<OwnerCapabilityFilter>()
            .AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff"; return await next(context); });
        g.MapPost("/google-drive", async (CreateAccount body, [FromServices] CredentialService credentials, CancellationToken ct) => { try { return Results.Json(AdminCredentialEndpoints.Map(await credentials.CreateGoogleDriveAccountAsync(body.DisplayName, ct)), statusCode: 201); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } });
        g.MapGet("/{id:guid}/drive-connection", async (Guid id, IGoogleDriveOAuthService oauth, CancellationToken ct) => { try { return Results.Json(await oauth.ConnectionAsync(id, ct)); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } });
        g.MapPost("/{id:guid}/drive-connect", async (Guid id, Revision body, HttpContext http, IGoogleDriveOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) =>
        {
            try { var start = await oauth.StartAsync(id, body.ExpectedRevision, (await profiles.GetLocalProfileAsync(ct)).ProfileId, ct); http.Response.Cookies.Append(CookieName, start.Correlation, Cookie(start.SecureCookie, start.ExpiresAtUtc)); http.Response.Headers.CacheControl = "no-store"; return Results.Json(new { start.AuthorizationUrl, start.Synthetic }); } catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        });
        g.MapPost("/{id:guid}/drive-check", async (Guid id, Revision body, IGoogleDriveOAuthService oauth, CancellationToken ct) => { try { return Results.Json(await oauth.CheckAsync(id, body.ExpectedRevision, ct)); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } });
        g.MapPost("/{id:guid}/drive-disconnect", async (Guid id, Revision body, IGoogleDriveOAuthService oauth, CancellationToken ct) => { try { return Results.Json(await oauth.DisconnectAsync(id, body.ExpectedRevision, ct)); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } });
        app.MapGet(Root + "/callback", (HttpContext http, IGoogleDriveOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) => Complete(http, oauth, profiles, http.Request.Query["code"], http.Request.Query["error"], ct));
        app.MapGet(Root + "/synthetic", (HttpContext http, IGoogleDriveOAuthService oauth) =>
        {
            if (!oauth.IsSynthetic || !oauth.IsConfigured) return Results.NotFound(); NoStore(http); var state = WebUtility.HtmlEncode(http.Request.Query["state"].ToString());
            return Results.Content($"<!doctype html><html lang='en'><meta name='viewport' content='width=device-width'><title>Synthetic Drive authorization</title><main><h1>Synthetic Drive authorization</h1><p>No Google account or hosted API is used. This offline fixture authorizes only files available to Agent Core; tools and Agent Instance bindings remain separate.</p><form method='post'><input type='hidden' name='state' value='{state}'><button name='decision' value='authorize'>Authorize Synthetic Drive</button><button name='decision' value='cancel'>Cancel</button></form></main></html>", "text/html");
        });
        app.MapPost(Root + "/synthetic", async (HttpContext http, IGoogleDriveOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) =>
        { if (!oauth.IsSynthetic || !oauth.IsConfigured) return Results.NotFound(); var form = await http.Request.ReadFormAsync(ct); return await Complete(http, oauth, profiles, form["decision"] == "authorize" ? "synthetic-consent" : null, form["decision"] == "authorize" ? null : "access_denied", ct, form["state"]); });
    }
    private static void NoStore(HttpContext http) { http.Response.Headers.CacheControl = "no-store"; http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"; }
    private static async Task<IResult> Complete(HttpContext http, IGoogleDriveOAuthService oauth, ILocalUserProfileService profiles, string? code, string? error, CancellationToken ct, string? formState = null)
    {
        NoStore(http); try { var id = await oauth.CompleteAsync(formState ?? http.Request.Query["state"].ToString(), http.Request.Cookies[CookieName], (await profiles.GetLocalProfileAsync(ct)).ProfileId, code, error, ct); http.Response.Cookies.Delete(CookieName, Cookie(http.Request.IsHttps)); http.Response.StatusCode = 303; http.Response.Headers.Location = oauth.ReturnUrl + "?driveAccount=" + id.ToString("D"); return Results.Empty; } catch (AgentCoreException ex) { return ProblemResults.From(ex); }
    }
}
