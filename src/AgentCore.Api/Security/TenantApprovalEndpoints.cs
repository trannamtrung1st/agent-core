using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Security;

namespace AgentCore.Api.Security;

public static class TenantApprovalEndpoints
{
    public sealed record DecisionRequest(string Decision, string ActionHash, long ExpectedRevision, long ExpectedRequestRevision);
    public sealed record ActionWitness(string ActionHash, long ExpectedRevision, long ExpectedRequestRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/approvals").RequireAuthorization();
        group.MapGet("/", (int? page, int? pageSize, string? status, string? kind, HttpContext http, TenantApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var query = new TenantApprovalQuery(page ?? 0, pageSize ?? 20, Parse<TenantApprovalStatus>(status), Parse<ApprovalKind>(kind));
            var result = await service.ListAsync(actor.TenantId, Initiator(http, actor), query, ct).ConfigureAwait(false);
            return Results.Json(new TenantApprovalPageDto(result.Items.Select(Project).ToArray(), result.Total, result.Page, result.PageSize));
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
    private static TenantApprovalDto Project(TenantApprovalView view)
    {
        var r = view.Request; var op = r.Operation;
        var p = op.Persona; var pref = op.Preferences; var review = view.Review;
        return new(r.RequestId, r.TenantId, r.RunId, r.SessionId, r.RootActivationId, r.ExecutorPrincipalId, r.OriginalInitiatorPrincipalId,
            Camel(r.Kind), Camel(r.Status), r.Revision, view.SecurityRevision, r.CreatedAt, r.ExpiresAt,
            op.Kind == TenantLocalOperationKind.Persona ? "Update agent persona" : "Update conversation preferences", TenantApprovalRequest.Permission,
            new(TenantApprovalRequest.ResourceType, r.ResourceId), new(op.OperationId, op.Kind == TenantLocalOperationKind.Persona ? "update_persona" : "update_preferences",
                p is null ? null : new(p.Name, p.Role, p.Description, p.Tone), pref is null ? null : new(pref.ResponseLength, pref.Language, pref.AskOneQuestionAtATime,
                    pref.ClearResponseLength, pref.ClearLanguage, pref.ClearAskOneQuestionAtATime), op.ExpectedInstanceRevision, op.ExpectedPersonaRevision, op.ActionHash),
            view.Decision is { } d ? new(d.HumanPrincipalId, Camel(d.Status), d.DecidedAt) : null,
            view.OperationResult is { } result ? new(result.OperationId, Camel(result.Outcome), result.ReasonCode, result.InstanceRevision, result.PersonaRevision, result.RecordedAt) : null,
            review is null ? null : new(review.AgentName, review.RequesterName, review.ReviewerName,
                review.Changes.Select(c => new TenantApprovalChangeDto(c.Field, c.Before, c.After, c.BeforeAvailable, c.AfterAvailable, c.RestoresDefault)).ToArray(),
                review.CanDecide, review.CanCancel, review.DecisionUnavailableReason, review.IndependentReview, review.ServerNow, review.ExecutionState));
    }
    private static T? Parse<T>(string? value) where T : struct, Enum => value is null ? null
        : Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _) ? parsed
        : throw new ArgumentException("Unknown approval filter.");
    private static string Camel<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
