using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Security;

public static class TenantApprovalEndpoints
{
    public sealed record DecisionRequest(string Decision, string ActionHash, long ExpectedRevision, long ExpectedRequestRevision);
    public sealed record ActionWitness(string ActionHash, long ExpectedRevision, long ExpectedRequestRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/approvals").RequireAuthorization();
        group.MapGet("/", (int? limit, HttpContext http, TenantApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var items = await service.ListAsync(actor.TenantId, Initiator(http, actor), Math.Clamp(limit ?? 50, 1, 100), ct).ConfigureAwait(false);
            return Results.Json(new { items = items.Select(Project).ToArray() });
        }));
        group.MapGet("/{requestId:guid}", (Guid requestId, HttpContext http, TenantApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Project(await service.ReadViewAsync(actor.TenantId, requestId, Initiator(http, actor), ct).ConfigureAwait(false)));
        }));
        group.MapPost("/{requestId:guid}/decision", (Guid requestId, DecisionRequest body, HttpContext http, TenantApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var human = Initiator(http, actor);
            var approve = body.Decision switch { "approve" => true, "reject" => false, _ => throw new ArgumentException("Choose approve or reject.") };
            var request = await service.DecideAsync(actor.TenantId, requestId, human, approve, body.ActionHash, body.ExpectedRevision, body.ExpectedRequestRevision, ct).ConfigureAwait(false);
            return Results.Json(new { requestId = request.RequestId, status = Camel(request.Status), requestRevision = request.Revision });
        }));
        group.MapPost("/{requestId:guid}/cancel", (Guid requestId, ActionWitness body, HttpContext http, TenantApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var request = await service.CancelAsync(actor.TenantId, requestId, Initiator(http, actor), body.ActionHash, body.ExpectedRevision, body.ExpectedRequestRevision, ct).ConfigureAwait(false);
            return Results.Json(new { requestId = request.RequestId, status = Camel(request.Status), requestRevision = request.Revision });
        }));
    }
    private static TenantSessionInitiator Initiator(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Project(TenantApprovalView view)
    {
        var r = view.Request; var op = r.Operation;
        return new { r.RequestId, r.TenantId, r.RunId, r.SessionId, r.RootActivationId, r.ExecutorPrincipalId, r.OriginalInitiatorPrincipalId,
            kind = Camel(r.Kind), status = Camel(r.Status), requestRevision = r.Revision, view.SecurityRevision, r.CreatedAt, r.ExpiresAt,
            summary = "Update this Agent Instance persona", permission = AgentCore.Domain.Security.TenantApprovalRequest.Permission,
            targetResource = new { type = AgentCore.Domain.Security.TenantApprovalRequest.ResourceType, id = r.ResourceId },
            preparedAction = new { op.OperationId, op.Persona, op.ExpectedInstanceRevision, op.ExpectedPersonaRevision, op.ActionHash },
            decision = view.Decision is { } d ? new { principalId = d.HumanPrincipalId, status = Camel(d.Status), d.DecidedAt } : null,
            operationResult = view.OperationResult is { } result ? new { result.OperationId, outcome = Camel(result.Outcome), result.ReasonCode,
                result.InstanceRevision, result.PersonaRevision, result.RecordedAt } : null };
    }
    private static string Camel<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
