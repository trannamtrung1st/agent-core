using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Security;

public static class TenantSessionEndpoints
{
    public sealed record CreateRequest(Guid AgentInstanceId, long ExpectedRevision, long ExpectedInstanceRevision, string Mode = "text", string? ModelKey = null, string? ReasoningEffort = null);
    public sealed record Witness(long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record RenameRequest(string Title, long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record MessageRequest(Guid MessageId, string Text, long ExpectedRevision, long ExpectedSessionRevision);
    public sealed record RunWitness(long ExpectedRevision, long ExpectedRunRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions").RequireAuthorization();
        group.MapGet("/{sessionId:guid}/approvals", (Guid sessionId, int? page, int? pageSize, HttpContext http,
            TenantConversationApprovalService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAsync(actor.TenantId, sessionId, Initiator(http, actor), page ?? 0, pageSize ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(item => new { item.RequestId, item.RunId, item.ResponseId,
                operation = item.Operation switch { AgentCore.Domain.Security.TenantOperationKind.Persona => "update_persona",
                    AgentCore.Domain.Security.TenantOperationKind.ConversationPreferences => "update_preferences", AgentCore.Domain.Security.TenantOperationKind.SandboxCommand => "run_sandbox_command", _ => "check_credential_connection" },
                kind = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()),
                status = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.Status.ToString()), item.CreatedAt, item.ExpiresAt,
                item.Waiting, item.CanInspect, outcome = item.Outcome is { } outcome ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) : null }),
                result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{sessionId:guid}/conversation", (Guid sessionId, HttpContext http, TenantConversationService service, IModelCatalog models, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var view = await service.ReadViewAsync(actor.TenantId, sessionId, Initiator(http, actor), ct).ConfigureAwait(false);
            return Results.Json(new { view.Owner.TenantId, view.Owner.SessionId, view.Owner.AgentInstanceId, view.Title, view.Revision, view.SecurityRevision,
                mode = view.Mode.ToString().ToLowerInvariant(), pendingMode = (string?)null, status = view.Status.ToString().ToLowerInvariant(),
                lifecycleStatus = LifecycleTransition.ToWire(view.LifecycleStatus), view.CreatedAt, view.UpdatedAt,
                agentId = view.Agent.Id, agentVersion = view.Agent.Version, agentName = view.Agent.Name, agentRole = view.Agent.Role,
                model = HttpMapping.ToModel(view.Model, models) });
        }));
        group.MapGet("/{sessionId:guid}/messages", (Guid sessionId, long? after, long? before, int? limit, HttpContext http,
            TenantConversationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var page = await service.ReadAsync(actor.TenantId, sessionId, Initiator(http, actor), after, before, limit ?? 50, ct).ConfigureAwait(false);
            return Results.Json(new { items = page.Items.Select(HttpMapping.ToHistoryItem), page.NextAfter, page.HasMore,
                page.HasOlder, page.NextBefore, page.SessionRevision, page.SecurityRevision });
        }));
        group.MapPost("/{sessionId:guid}/messages", (Guid sessionId, MessageRequest body, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var receipt = await service.SubmitTextAsync(actor.TenantId, sessionId, Initiator(http, actor), body.MessageId, body.Text,
                body.ExpectedRevision, body.ExpectedSessionRevision, ct).ConfigureAwait(false);
            return Results.Json(receipt, statusCode: receipt.Created ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }));
        app.MapGet("/api/v2/tenants/{tenantId:guid}/agent-runs/{runId:guid}", (Guid runId, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadAsync(actor.TenantId, runId, Initiator(http, actor), ct).ConfigureAwait(false);
            return Results.Json(Safe(result));
        })).RequireAuthorization();
        app.MapPost("/api/v2/tenants/{tenantId:guid}/agent-runs/{runId:guid}/cancel", (Guid runId, RunWitness body, HttpContext http, TenantRunService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.CancelAsync(actor.TenantId, runId, Initiator(http, actor), body.ExpectedRevision, body.ExpectedRunRevision, ct).ConfigureAwait(false)));
        })).RequireAuthorization();
        group.MapPatch("/{sessionId:guid}", (Guid sessionId, RenameRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, TenantSessionAction.Rename, body.Title), body.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.Json(Safe(result!));
        }));
        foreach (var action in new[] { TenantSessionAction.Archive, TenantSessionAction.Restore, TenantSessionAction.Pause, TenantSessionAction.Resume, TenantSessionAction.Complete, TenantSessionAction.Cancel })
            group.MapPost("/{sessionId:guid}/" + action.ToString().ToLowerInvariant(), (Guid sessionId, Witness body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
            {
                var actor = IdentityHttp.TenantContext(http);
                var result = await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, body.ExpectedSessionRevision, action), body.ExpectedRevision, ct).ConfigureAwait(false);
                return Results.Json(Safe(result!));
            }));
        group.MapDelete("/{sessionId:guid}", (Guid sessionId, long expectedRevision, long expectedSessionRevision, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            await service.ChangeAsync(actor.TenantId, Initiator(http, actor), new(sessionId, expectedSessionRevision, TenantSessionAction.Delete), expectedRevision, ct).ConfigureAwait(false); return Results.NoContent();
        }));
        group.MapPost("", (CreateRequest body, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var initiator = new TenantSessionInitiator(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
            var item = await service.CreateAsync(actor.TenantId, body.AgentInstanceId, initiator, HttpMapping.ParseMode(body.Mode), body.ExpectedRevision, body.ExpectedInstanceRevision, body.ModelKey, body.ReasoningEffort, ct).ConfigureAwait(false);
            return Results.Json(Safe(item), statusCode: 201);
        }));
        group.MapGet("", (int? page, int? pageSize, bool? includeArchived, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ListAsync(actor.PrincipalId, actor.TenantId, page ?? 0, pageSize ?? 30, ct, includeArchived ?? true).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext http, TenantSessionService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.PrincipalId, actor.TenantId, sessionId, ct).ConfigureAwait(false)));
        }));
    }
    private static TenantSessionInitiator Initiator(HttpContext http, AuthenticatedTenant actor) =>
        new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static object Safe(TenantRunMetadata result)
    {
        var c = result.Context;
        return new { context = new { c.TenantId, c.AgentPrincipalId, c.OriginalInitiatorPrincipalId, c.SessionId, c.RunId,
            c.ActivationId, c.RootActivationId, c.ParentRunId, c.ParentActivationId, origin = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(c.Origin.ToString()) },
            result.AgentInstanceId, status = result.Status.ToString().ToLowerInvariant(), result.Revision, result.CreatedAt, result.UpdatedAt, result.ContinuationRunId, result.CanCancel,
            outcomeKind = result.OutcomeKind is { } outcome ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) : null };
    }
    private static object Safe(TenantSessionMetadata item) => new { item.Owner.TenantId, item.Owner.SessionId, item.Owner.AgentInstanceId,
        item.Owner.ExecutorPrincipalId, item.Owner.CreatedByPrincipalId, item.Title, mode = item.Mode.ToString().ToLowerInvariant(),
        status = item.Status.ToString().ToLowerInvariant(), item.Revision, item.CreatedAt, item.UpdatedAt,
        lifecycleStatus = LifecycleTransition.ToWire(item.LifecycleStatus), item.ArchivedAt, item.AgentId, item.AgentVersion, item.AgentName, item.AgentRole, item.CanManage };
}
