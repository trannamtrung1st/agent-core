using AgentCore.Api.Http;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

internal static class AdminBrowserPrivacyEndpoints
{
    internal static void MapBrowserPrivacy(this RouteGroupBuilder admin)
    {
        admin.MapGet("/browser/privacy", (BrowserPrivacyService service, CancellationToken ct) =>
            Run(async () => Results.Json(Map(await service.ReadAsync(ct)))));
        admin.MapPut("/browser/privacy", (AdminSaveBrowserPrivacyRequest request, BrowserPrivacyService service, CancellationToken ct) =>
            Run(async () => Results.Json(Map(await service.SaveAsync(request.ExpectedRevision, request.Mode,
                request.UnmaskedOrigins, request.TrustedGraphicsOrigins, request.AcknowledgeExposure, ct)))));
    }
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    { try { return await action(); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } }
    private static AdminBrowserPrivacyResponse Map(BrowserPrivacyView value) => new(
        Policy(value.Saved), Policy(value.Effective), new(value.Deployment.CaptureAllowed, value.Deployment.UnmaskedAllowed,
            value.Deployment.UnmaskedOriginCeiling, value.Deployment.GraphicsOriginCeiling), value.RestartRequired, value.Activation, value.Durable, value.ConstrainedByDeployment);
    private static AdminBrowserScreenshotPolicy Policy(BrowserScreenshotPolicy value) =>
        new(value.Mode.ToString(), value.UnmaskedOrigins, value.TrustedGraphicsOrigins, value.Revision);
}
