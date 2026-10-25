using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

public static class TenantOperationPolicyEndpoints
{
    public sealed record PolicyRequest(string Mode, bool IndependentReview, long ExpectedRevision, long ExpectedPolicyRevision);
    public sealed record ChatRetentionRequest(int? SavedChatRetentionDays, long ExpectedRevision);
    public static void Map(WebApplication app)
    {
        var retention = app.MapGroup("/api/v2/tenants/{tenantId:guid}/governance/chat-retention").RequireAuthorization();
        retention.MapGet("", (HttpContext http, TenantOperationPolicyService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReadChatRetentionAsync(actor.TenantId, Initiator(http, actor), ct).ConfigureAwait(false));
        }));
        retention.MapPut("", (ChatRetentionRequest body, HttpContext http, TenantOperationPolicyService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(await service.SaveChatRetentionAsync(actor.TenantId, Initiator(http, actor), body.SavedChatRetentionDays, body.ExpectedRevision, ct).ConfigureAwait(false));
        }));
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/operation-policies").RequireAuthorization();
        group.MapGet("", (int? page, int? pageSize, string? q, HttpContext http, TenantOperationPolicyService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.TenantId, Initiator(http, actor), page ?? 0, pageSize ?? 20, q, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize, result.SecurityRevision });
        }));
        group.MapGet("/{permission}", (string permission, HttpContext http, TenantOperationPolicyService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, Initiator(http, actor), permission, ct).ConfigureAwait(false)));
        }));
        group.MapPut("/{permission}", (string permission, PolicyRequest body, HttpContext http, TenantOperationPolicyService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var mode = body.Mode?.Trim().ToLowerInvariant() switch { "none" => OperationApprovalMode.None, "whenrequesterlackspermission" => OperationApprovalMode.WhenRequesterLacksPermission,
                "always" => OperationApprovalMode.Always, _ => throw new ArgumentException("Unknown approval mode.") };
            return Results.Json(Safe(await service.SaveAsync(actor.TenantId, Initiator(http, actor), permission, new(mode, body.IndependentReview), body.ExpectedRevision, body.ExpectedPolicyRevision, ct).ConfigureAwait(false)));
        }));
    }
    private static TenantSessionInitiator Initiator(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(TenantOperationPolicyView view) => new { view.Permission, approvalClass = Camel(view.ApprovalClass),
        minimum = new { mode = Camel(view.Minimum.Mode), view.Minimum.IndependentReview }, effective = new { mode = Camel(view.Effective.Mode), view.Effective.IndependentReview },
        view.PolicyRevision, view.SecurityRevision, view.HumanOnly, view.Delegatable, view.CanManage };
    private static string Camel<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
