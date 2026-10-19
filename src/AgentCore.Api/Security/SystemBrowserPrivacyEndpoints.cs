using AgentCore.Application.Security;
using AgentCore.Api.Security;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

internal static class SystemBrowserPrivacyEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/system/browser/privacy", (HttpContext http, IIamStore security, SystemBrowserPrivacyService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(Map(await service.ReadAsync(await Human(http, security, ct), ct))))).RequireAuthorization();
        app.MapPut("/api/v2/system/browser/privacy", (SaveBrowserPrivacyRequest request, HttpContext http, IIamStore security, SystemBrowserPrivacyService service, CancellationToken ct) =>
            IdentityHttp.Respond(async () => Results.Json(Map(await service.SaveAsync(await Human(http, security, ct), new(request.ExpectedRevision, request.Mode,
                request.UnmaskedOrigins, request.TrustedGraphicsOrigins, request.AcknowledgeExposure), ct))))).RequireAuthorization();
    }
    private static async ValueTask<HumanSessionReference> Human(HttpContext http, IIamStore security, CancellationToken ct)
    {
        var account = IdentityHttp.AccountId(http.User) ?? throw new AccessDeniedException("initiator_unavailable");
        var principal = (await security.ReadSystemAsync(ct)).Principals.SingleOrDefault(p => p.AccountId == account && p.TenantId is null
            && p.Kind == AgentCore.Domain.Security.PrincipalKind.User && p.Status == AgentCore.Domain.Security.IdentityStatus.Active)
            ?? throw new AccessDeniedException("initiator_unavailable");
        return new(account, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), principal.PrincipalId);
    }
    private static BrowserPrivacyResponse Map(BrowserPrivacyView value) => new(
        Policy(value.Saved), Policy(value.Effective), new(value.Deployment.CaptureAllowed, value.Deployment.UnmaskedAllowed,
            value.Deployment.UnmaskedOriginCeiling, value.Deployment.GraphicsOriginCeiling), value.RestartRequired, value.Activation, value.Durable, value.ConstrainedByDeployment);
    private static BrowserScreenshotPolicyResponse Policy(BrowserScreenshotPolicy value) =>
        new(value.Mode.ToString(), value.UnmaskedOrigins, value.TrustedGraphicsOrigins, value.Revision);
}
