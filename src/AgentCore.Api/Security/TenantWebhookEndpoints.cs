using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;

namespace AgentCore.Api.Security;

public static class TenantWebhookEndpoints
{
    public sealed record CreateRequest(string DisplayName, string EventKey, long ExpectedRevision);
    public sealed record RenameRequest(string DisplayName, long ExpectedRevision, long ExpectedSourceRevision);
    public sealed record Witness(long ExpectedRevision, long ExpectedSourceRevision);
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:guid}/events/builtin", (HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(new { items = await service.BuiltinsAsync(actor.TenantId, Human(http, actor), ct) });
        })).RequireAuthorization();
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/webhooks").RequireAuthorization();
        group.MapGet("", (int? page, int? pageSize, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ListAsync(actor.TenantId, Human(http, actor), page ?? 0, pageSize ?? 20, ct);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize, result.SecurityRevision, result.CanCreate });
        }));
        group.MapGet("/{sourceId:guid}", (Guid sourceId, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, sourceId, Human(http, actor), ct)));
        }));
        group.MapGet("/{sourceId:guid}/receipts", (Guid sourceId, int? page, int? pageSize, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ReceiptsAsync(actor.TenantId, sourceId, Human(http, actor), page ?? 0, pageSize ?? 20, ct);
            return Results.Json(new { source = Safe(result.Source), items = result.Receipts.Items.Select(Receipt), result.Receipts.Total, result.Receipts.Page, result.Receipts.PageSize });
        }));
        group.MapPost("", (CreateRequest body, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var result = await service.CreateAsync(actor.TenantId, Human(http, actor), body.DisplayName, body.EventKey, body.ExpectedRevision, ct);
            return Results.Json(new { source = Safe(result.View), result.Token }, statusCode: 201);
        }));
        group.MapPut("/{sourceId:guid}", (Guid sourceId, RenameRequest body, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.RenameAsync(actor.TenantId, sourceId, Human(http, actor), body.DisplayName, body.ExpectedRevision, body.ExpectedSourceRevision, ct)));
        }));
        group.MapPost("/{sourceId:guid}/rotate", (Guid sourceId, Witness body, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var result = await service.RotateAsync(actor.TenantId, sourceId, Human(http, actor), body.ExpectedRevision, body.ExpectedSourceRevision, ct);
            return Results.Json(new { source = Safe(result.View), result.Token });
        }));
        group.MapPost("/{sourceId:guid}/revoke", (Guid sourceId, Witness body, HttpContext http, TenantWebhookService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.RevokeAsync(actor.TenantId, sourceId, Human(http, actor), body.ExpectedRevision, body.ExpectedSourceRevision, ct)));
        }));
    }
    private static HumanSessionReference Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(TenantWebhookWrite value) => new { value.Source.ResourceId, value.Source.DisplayName, value.Source.EventKey,
        status = value.Source.Status == WebhookEventStatus.Active ? "active" : "revoked", value.Source.Revision, value.Source.CreatedAtUtc, value.Source.UpdatedAtUtc,
        value.CanManage, value.SecurityRevision, ingressPath = $"/api/v2/hooks/{value.Source.TenantId:D}/{value.Source.ResourceId:D}" };
    private static object Receipt(ExternalEvent value) => new { value.EventId, value.SourceEventId, value.SourceRevision, value.OccurredAtUtc, value.AdmittedAtUtc,
        data = JsonSerializer.Deserialize<JsonElement>(value.EvidenceJson).GetProperty("data") };
}
