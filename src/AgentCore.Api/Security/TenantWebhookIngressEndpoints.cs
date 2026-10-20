using AgentCore.Application.Events;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static class TenantWebhookIngressEndpoints
{
    public static void Map(WebApplication app) => app.MapPost("/api/v2/hooks/{tenantId:guid}/{sourceId:guid}", Receive).AllowAnonymous();
    private static async Task<IResult> Receive(Guid tenantId, Guid sourceId, HttpContext http, TenantWebhookService service, IdentityHttpOptions options, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.Request.IsHttps && (!options.AllowInsecureLocalHttp || !Http.TrustedLocalCaller.IsLoopback(http))) return Error(400, "https_required");
        if (!HookEndpoints.TryBearer(http.Request, out var token) || !await service.CredentialsMatchAsync(tenantId, sourceId, token, ct)) return Error(401, "unauthorized");
        if (http.Request.ContentLength > ExternalEventEnvelope.MaxRawBytes) return Error(400, "payload_too_large");
        var body = await HookEndpoints.ReadBoundedAsync(http.Request.Body, ct); if (body is null) return Error(400, "payload_too_large");
        try
        {
            var result = await service.AdmitAsync(tenantId, sourceId, token, body, ct);
            return result.Kind switch {
                ExternalEventIngressKind.Admitted => Results.Json(new { result.EventId }, statusCode: 202),
                ExternalEventIngressKind.Duplicate => Results.Json(new { result.EventId }),
                ExternalEventIngressKind.Invalid => Error(result.Code == "source_event_conflict" ? 409 : 400, result.Code ?? "invalid_payload"),
                _ => Error(401, "unauthorized") };
        }
        catch (SecurityConflictException) { return Error(409, "webhook_admission_conflict"); }
    }
    private static IResult Error(int code, string reason) => Results.Json(new { reasonCode = reason }, statusCode: code);
}
