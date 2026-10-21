using System.Security.Claims;
using System.Threading.RateLimiting;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Http.Features;

namespace AgentCore.Api.Security;

public sealed class IdentityHttpOptions
{
    // Explicit native-development exception only. Never enables remote cleartext authentication.
    public bool AllowInsecureLocalHttp { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed record AuthenticatedTenant(Guid AccountId, Guid TenantId, Guid UserId, Guid PrincipalId);

public static class IdentityHttp
{
    public const string Scheme = "AgentCore.Account";
    public const string SessionClaim = "agentcore:authentication-session";
    public const string CsrfHeader = "X-AgentCore-CSRF";
    internal const string TenantItem = "AgentCore.AuthenticatedTenant";

    public static void AddIdentityHttp(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Security:Http").Get<IdentityHttpOptions>() ?? new();
        foreach (var origin in options.AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Scheme is not ("https" or "http")
                || uri.Scheme == "http" && (!options.AllowInsecureLocalHttp || !uri.IsLoopback))
                throw new InvalidOperationException("Security:Http:AllowedOrigins must contain exact HTTPS origins or explicitly allowed loopback HTTP origins.");
        services.AddSingleton(options);
        services.AddAuthentication(Scheme).AddCookie(Scheme, cookie =>
        {
            cookie.Cookie.Name = "agentcore.session";
            cookie.Cookie.HttpOnly = true; cookie.Cookie.IsEssential = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = options.AllowInsecureLocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            cookie.SlidingExpiration = false; cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
            cookie.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    var id = SessionId(context.Principal);
                    var account = id is { } sessionId ? await context.HttpContext.RequestServices.GetRequiredService<IdentityService>()
                        .ValidateSessionAsync(sessionId, context.HttpContext.RequestAborted).ConfigureAwait(false) : null;
                    if (account is null || AccountId(context.Principal) != account.AccountId) context.RejectPrincipal();
                },
                OnRedirectToLogin = context => FailureAsync(context.Response, 401, "not_authenticated"),
                OnRedirectToAccessDenied = context => FailureAsync(context.Response, 403, "permission_denied")
            };
        });
        services.AddAuthorization();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = CsrfHeader;
            antiforgery.Cookie.Name = "agentcore.csrf"; antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = options.AllowInsecureLocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = 429;
            limits.AddPolicy("account-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
    }

    public static void UseIdentityHttp(this WebApplication app)
    {
        app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.Use(async (http, next) =>
        {
            // These canonical namespaces have no owner-token fallback. Resource cutover will move the
            // remaining mappings here; source still awaiting cutover is not a multi-tenant deployment.
            var canonical = http.Request.Path.StartsWithSegments("/api/v2/auth")
                || http.Request.Path.StartsWithSegments("/api/v2/account")
                || http.Request.Path.StartsWithSegments("/api/v2/system")
                || http.Request.Path.StartsWithSegments("/api/v2/tenants");
            if (!canonical) { await next(http).ConfigureAwait(false); return; }
            http.Response.Headers.CacheControl = "no-store";
            var policy = http.RequestServices.GetRequiredService<IdentityHttpOptions>();
            if (!http.Request.IsHttps && (!policy.AllowInsecureLocalHttp || !Http.TrustedLocalCaller.IsLoopback(http)))
            { await FailureAsync(http.Response, 400, "https_required").ConfigureAwait(false); return; }
            if (!http.Request.Path.StartsWithSegments("/api/v2/auth") && AccountId(http.User) is null)
            { await FailureAsync(http.Response, 401, "not_authenticated").ConfigureAwait(false); return; }
            if (http.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is { } bodyLimit)
            {
                if (http.Request.ContentLength > bodyLimit)
                { await FailureAsync(http.Response, 413, "request_too_large").ConfigureAwait(false); return; }
                var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = bodyLimit;
            }
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http).ConfigureAwait(false); }
                catch (AntiforgeryValidationException)
                { await FailureAsync(http.Response, 400, "invalid_csrf").ConfigureAwait(false); return; }
            }
            if (http.Request.Path.StartsWithSegments("/api/v2/tenants") && http.Request.RouteValues.TryGetValue("tenantId", out var raw))
            {
                if (AccountId(http.User) is not { } accountId || !Guid.TryParse(Convert.ToString(raw), out var tenantId))
                { await FailureAsync(http.Response, 401, "not_authenticated").ConfigureAwait(false); return; }
                var user = await http.RequestServices.GetRequiredService<IdentityService>().ResolveUserAsync(accountId, tenantId, http.RequestAborted).ConfigureAwait(false);
                if (user is null) { await FailureAsync(http.Response, 403, "not_member").ConfigureAwait(false); return; }
                http.Items[TenantItem] = new AuthenticatedTenant(accountId, tenantId, user.UserId, user.PrincipalId);
            }
            await next(http).ConfigureAwait(false);
        });
    }

    public static Guid? AccountId(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty ? id : null;
    public static Guid? SessionId(ClaimsPrincipal? user) => user?.Identity?.IsAuthenticated == true
        && Guid.TryParse(user.FindFirstValue(SessionClaim), out var id) && id != Guid.Empty ? id : null;
    public static AuthenticatedTenant TenantContext(HttpContext http) => http.Items.TryGetValue(TenantItem, out var value) && value is AuthenticatedTenant tenant
        ? tenant : throw new AccessDeniedException("not_member");
    internal static async ValueTask<HumanSessionReference> SystemHumanAsync(HttpContext http, IIamStore security, CancellationToken ct)
    {
        var account = AccountId(http.User) ?? throw new AccessDeniedException("initiator_unavailable");
        var principal = (await security.ReadSystemAsync(ct).ConfigureAwait(false)).Principals.SingleOrDefault(p => p.AccountId == account && p.TenantId is null
            && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active) ?? throw new AccessDeniedException("initiator_unavailable");
        return new(account, SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), principal.PrincipalId);
    }
    public static ClaimsPrincipal Claims(SignedInAccount account) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, account.Account.AccountId.ToString("D")), new Claim(SessionClaim, account.Session.SessionId.ToString("D"))], Scheme));
    public static Task FailureAsync(HttpResponse response, int status, string reason)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { title = reason, status, reasonCode = reason });
    }
    public static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (AccessDeniedException ex) { return Results.Json(new { reasonCode = ex.ReasonCode, title = "This operation is not authorized." }, statusCode: 403); }
        catch (SecurityConflictException ex) { return Results.Json(new { reasonCode = ex.ReasonCode, title = ex.Message }, statusCode: 409); }
        catch (DefinitionPublicationNotReadyException ex) { return Results.Json(new { reasonCode = ex.ReasonCode, title = ex.Message }, statusCode: 400); }
        catch (AgentCore.Application.Sessions.AgentCoreException ex)
        {
            var reason = ex.ValidationCode ?? (ex.Code switch { "ValidationError" => "invalid_request", "Conflict" => "resource_conflict", "NotFound" => "resource_unavailable", _ => ex.Code });
            return Http.ProblemResults.From(ex, reason);
        }
        catch (ArgumentException) { return Results.Json(new { reasonCode = "invalid_request", title = "Check the submitted values." }, statusCode: 400); }
    }
}
