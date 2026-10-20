using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Security;

public static class TenantAutomationEndpoints
{
    public sealed record SaveRequest(bool Enabled, string Name, string Instructions, AutomationTiming Schedule, long ExpectedRevision,
        long ExpectedInstanceRevision, long ExpectedAutomationRevision = 0, string? ModelKey = null, string? ReasoningEffort = null, bool RequiresTools = true, bool RequiresVision = false);
    public sealed record Witness(long ExpectedRevision, long ExpectedInstanceRevision, long ExpectedAutomationRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/automations").RequireAuthorization();
        group.MapGet("/editor", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Editor(await service.EditorAsync(actor.TenantId, instanceId, Human(http, actor.AccountId, actor.PrincipalId), page: page ?? 0, pageSize: pageSize ?? 10, ct: ct)));
        }));
        group.MapGet("/{automationId:guid}/editor", (Guid instanceId, Guid automationId, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Editor(await service.EditorAsync(actor.TenantId, instanceId, Human(http, actor.AccountId, actor.PrincipalId), automationId, ct: ct)));
        }));
        group.MapGet("", (Guid instanceId, int? page, int? pageSize, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var result = await service.ListAsync(actor.TenantId, instanceId, Human(http, actor.AccountId, actor.PrincipalId), page ?? 0, pageSize ?? 30, ct);
            return Results.Json(new { items = result.Items.Select(Safe), result.Total, result.Page, result.PageSize });
        }));
        group.MapGet("/{automationId:guid}", (Guid instanceId, Guid automationId, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var value = await service.ReadAsync(actor.TenantId, automationId, Human(http, actor.AccountId, actor.PrincipalId), ct);
            Route(value, instanceId); return Results.Json(Safe(value));
        }));
        group.MapPost("", (Guid instanceId, SaveRequest body, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var value = await service.SaveAsync(actor.TenantId, instanceId, null, Human(http, actor.AccountId, actor.PrincipalId), Change(body),
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedAutomationRevision, ct); return Results.Json(Safe(value), statusCode: 201);
        }));
        group.MapPut("/{automationId:guid}", (Guid instanceId, Guid automationId, SaveRequest body, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var value = await service.SaveAsync(actor.TenantId, instanceId, automationId, Human(http, actor.AccountId, actor.PrincipalId), Change(body),
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedAutomationRevision, ct); return Results.Json(Safe(value));
        }));
        group.MapDelete("/{automationId:guid}", (Guid instanceId, Guid automationId, long expectedRevision, long expectedInstanceRevision, long expectedAutomationRevision, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var human = Human(http, actor.AccountId, actor.PrincipalId);
            Route(await service.ReadAsync(actor.TenantId, automationId, human, ct), instanceId);
            return Results.Json(Safe(await service.CancelAsync(actor.TenantId, automationId, human, expectedRevision, expectedInstanceRevision, expectedAutomationRevision, ct)));
        }));
        group.MapPost("/{automationId:guid}/run", (Guid instanceId, Guid automationId, Witness body, HttpContext http, TenantAutomationService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var human = Human(http, actor.AccountId, actor.PrincipalId);
            Route(await service.ReadAsync(actor.TenantId, automationId, human, ct), instanceId);
            var result = await service.RunAsync(actor.TenantId, automationId, human, body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedAutomationRevision, ct);
            return Results.Json(new { result.Created, occurrenceId = result.Occurrence.OccurrenceId, result.SessionId, result.RunId, result.ApprovalRequestId }, statusCode: result.ApprovalRequestId is not null ? 202 : result.Created ? 201 : 200);
        }));
    }
    private static HumanSessionReference Human(HttpContext http, Guid account, Guid principal) => new(account, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), principal);
    private static TenantAutomationChange Change(SaveRequest value) => new(value.Enabled, value.Name, value.Instructions, AdminAutomationEndpoints.Parse(value.Schedule), value.ModelKey, value.ReasoningEffort, value.RequiresTools, value.RequiresVision);
    private static void Route(Automation value, Guid instance) { if (value.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable"); }
    private static object Editor(TenantAutomationEditor view) => new { agent = new { view.Agent.PrincipalId, view.Agent.SystemIdentityId, instance = AgentCore.Api.Mapping.AdminHttpMapping.ToAgentInstance(view.Agent.Instance) }, view.Policy, view.Total, view.Page, view.PageSize, view.SecurityRevision, view.CanCreate, view.CanSchedule,
        items = view.Items.Select(i => new { automation = Safe(i.Automation), i.CanUpdate, i.CanCancel, i.CanRun }) };
    private static object Safe(Automation value) => new {
        value.AutomationId, agentInstanceId = value.Owner.AgentInstanceId, value.Name, value.Instructions, status = value.Status.ToString().ToLowerInvariant(),
        value.Revision, value.TriggerRevision, value.NextOccurrenceAtUtc, value.OccurrenceCount, value.ExpiresAtUtc, value.ModelOverrideCatalogKey, value.ModelOverrideReasoningEffort,
        value.RequiresTools, value.RequiresVision, executionTarget = "backgroundSession", completionDelivery = "none", value.SuspensionReason,
        createdAt = value.Provenance.CreatedAt, updatedAt = value.Provenance.UpdatedAt, triggers = value.Triggers.Select(AdminAutomationEndpoints.TriggerRecord)
    };
}
