using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;

namespace AgentCore.Api.Security;

public static class TenantActivityEndpoints
{
    public static void Map(WebApplication app)
    {
        var root = app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/activity").RequireAuthorization();
        var group = root.MapGroup("/sessions");
        root.MapGet("/runs", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListAgentRunsAsync(actor.TenantId, instanceId, Human(http, actor), page ?? 0, pageSize ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Run), result.Total, result.Page, result.PageSize, result.SecurityRevision });
        }));
        root.MapGet("/runs/{runId:guid}", (Guid instanceId, Guid runId, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Run(await service.ReadAgentRunAsync(actor.TenantId, instanceId, runId, Human(http, actor), ct).ConfigureAwait(false)));
        }));
        root.MapGet("/background-sessions", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListBackgroundAsync(actor.TenantId, instanceId, Human(http, actor), page ?? 0, pageSize ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Background), result.Total, result.Page, result.PageSize, result.SecurityRevision });
        }));
        group.MapGet("", (Guid instanceId, int? page, int? pageSize, bool? backgroundOnly, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListSessionsAsync(actor.TenantId, instanceId, Human(http, actor), page ?? 0, pageSize ?? 30, backgroundOnly ?? false, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Session), result.Total, result.Page, result.PageSize, result.SecurityRevision });
        }));
        group.MapGet("/{sessionId:guid}", (Guid instanceId, Guid sessionId, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Session(await service.ReadSessionAsync(actor.TenantId, instanceId, sessionId, Human(http, actor), ct).ConfigureAwait(false)));
        }));
        group.MapGet("/{sessionId:guid}/background", (Guid instanceId, Guid sessionId, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ReadBackgroundAsync(actor.TenantId, instanceId, sessionId, Human(http, actor), ct).ConfigureAwait(false);
            return Results.Json(Background(result));
        }));
        group.MapGet("/{sessionId:guid}/runs", (Guid instanceId, Guid sessionId, int? page, int? pageSize, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            var result = await service.ListRunsAsync(actor.TenantId, instanceId, sessionId, Human(http, actor), page ?? 0, pageSize ?? 30, ct).ConfigureAwait(false);
            return Results.Json(new { items = result.Items.Select(Run), result.Total, result.Page, result.PageSize, result.SecurityRevision });
        }));
        group.MapGet("/{sessionId:guid}/runs/{runId:guid}", (Guid instanceId, Guid sessionId, Guid runId, HttpContext http, TenantActivityService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Run(await service.ReadRunAsync(actor.TenantId, instanceId, sessionId, runId, Human(http, actor), ct).ConfigureAwait(false)));
        }));
    }
    private static TenantSessionInitiator Human(HttpContext http, AuthenticatedTenant actor) =>
        new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    private static string Camel(string value) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value);
    private static object Background(TenantBackgroundDetail result) => new { session = Session(result.Session), initialRun = result.InitialRun is { } run ? Run(run) : null,
        result.InitialTitle, completionDelivery = result.CompletionDelivery is { } delivery ? new { status = Camel(delivery.Status), delivery.TargetSessionId, delivery.Reason } : null };
    private static object Session(TenantActivitySession row)
    {
        var s = row.Session;
        return new { s.Owner.TenantId, s.Owner.SessionId, s.Owner.AgentInstanceId, s.Title, mode = Camel(s.Mode.ToString()), s.Revision, s.CreatedAt, s.UpdatedAt,
            status = Camel(s.Status.ToString()), lifecycleStatus = LifecycleTransition.ToWire(s.LifecycleStatus), s.ArchivedAt,
            s.AgentId, s.AgentVersion, s.AgentName, s.AgentRole, s.CanManage, origin = Camel(row.Origin.ToString()), row.Surfaces,
            row.InitialRunId, row.CanContinue, row.SecurityRevision };
    }
    private static object Run(TenantActivityRun run)
    {
        var c = run.Context;
        return new { run.RunId, run.SessionId, run.AgentInstanceId, context = new { c.TenantId, c.AgentPrincipalId, c.OriginalInitiatorPrincipalId,
            c.RootActivationId, c.ActivationId, c.ParentRunId, c.ParentActivationId, origin = Camel(c.Origin.ToString()) },
            status = Camel(run.Status.ToString()), run.Revision, run.CreatedAt, run.UpdatedAt, activationKind = Camel(run.ActivationKind.ToString()),
            outputContract = Camel(run.OutputContract.ToString()), run.ModelOfferingId, run.AttemptCount, run.MaxAttempts, run.ProgressSummary, run.Objective, run.OutcomeText,
            outcomeKind = run.OutcomeKind is { } outcome ? Camel(outcome.ToString()) : null, run.FailureCode, run.FailureSummary, run.DiagnosticId, run.CancellationRequested, run.CanCancel };
    }
}
