using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Api.Http;

namespace AgentCore.Api;

internal static class GoogleEmailOAuthEndpoints
{
    internal const string CookieName = "AgentCore.EmailOAuth";
    internal static CookieOptions Cookie(bool secure, DateTimeOffset? expires = null) => new()
    { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Path = "/api/v2/integrations/google-email/oauth", Expires = expires, IsEssential = true };
    internal static void Map(WebApplication app)
    {
        const string root = "/api/v2/integrations/google-email/oauth";
        app.MapGet(root + "/callback", (HttpContext http, IEmailOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) => Complete(http, oauth, profiles, http.Request.Query["code"], http.Request.Query["error"], ct));
        app.MapGet(root + "/synthetic", async (HttpContext http, IEmailOAuthService oauth, CancellationToken ct) =>
        {
            // Only Synthetic-issued transactions can be completed with Synthetic consent.
            if (!await IsSynthetic(oauth, http, ct)) return Results.NotFound();
            NoStore(http);
            var state = WebUtility.HtmlEncode(http.Request.Query["state"].ToString());
            return Results.Content($"<!doctype html><html lang='en'><meta name='viewport' content='width=device-width'><title>Synthetic email authorization</title><main><h1>Synthetic email authorization</h1><p>No Google account or network connection is used. This offline account exercises the owner connection journey. Sending still requires separate exact approval.</p><form method='post'><input type='hidden' name='state' value='{state}'><button name='decision' value='authorize'>Authorize Synthetic account</button><button name='decision' value='cancel'>Cancel</button></form></main></html>", "text/html");
        });
        app.MapPost(root + "/synthetic", async (HttpContext http, IEmailOAuthService oauth, ILocalUserProfileService profiles, CancellationToken ct) =>
        {
            if (!await IsSynthetic(oauth, http, ct)) return Results.NotFound();
            var form = await http.Request.ReadFormAsync(ct);
            return await Complete(http, oauth, profiles, form["decision"] == "authorize" ? "synthetic-consent" : null,
                form["decision"] == "authorize" ? null : "access_denied", ct, form["state"]);
        });
    }
    private static ValueTask<bool> IsSynthetic(IEmailOAuthService oauth, HttpContext http, CancellationToken ct) =>
        ValueTask.FromResult(oauth.IsSynthetic);
    private static void NoStore(HttpContext http)
    { http.Response.Headers.CacheControl = "no-store"; http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"; }
    private static async Task<IResult> Complete(HttpContext http, IEmailOAuthService oauth, ILocalUserProfileService profiles, string? code, string? error, CancellationToken ct, string? formState = null)
    {
        NoStore(http);
        try
        {
            var id = await oauth.CompleteAsync(formState ?? http.Request.Query["state"].ToString(), http.Request.Cookies[CookieName],
                (await profiles.GetLocalProfileAsync(ct)).ProfileId, code, error, ct);
            http.Response.Cookies.Delete(CookieName, Cookie(http.Request.IsHttps));
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = oauth.ReturnUrl + "?emailAccount=" + id.ToString("D");
            return Results.Empty;
        }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
    }
}
