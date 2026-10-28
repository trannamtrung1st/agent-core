using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;

namespace AgentCore.Api.Security;

public static class TenantWorkflowEndpoints
{
    private sealed record DecisionSubmit(bool Approved, string? Rationale, string RequestKey, long ExpectedRevision, long ExpectedStepRevision, long ExpectedSecurityRevision);
    private sealed record CancelSubmit(string RequestKey, long ExpectedRevision, long ExpectedSecurityRevision);
    private static object StartReceipt(WorkflowExecution value) => new { value.ExecutionId, value.WorkflowId, value.Version, value.GraphHash, Status = value.Status.ToString(), value.Revision };
    private static HumanSessionReference Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    private static T Command<T>(JsonElement payload)
    {
        try { return payload.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20
        }) ?? throw new ArgumentException("Use the closed workflow command."); }
        catch (JsonException error) { throw new ArgumentException("Use unique closed workflow command fields.", error); }
    }
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/workflows").RequireAuthorization();
        group.MapGet("/readiness", (HttpContext http, IFormalWorkflowRuntime runtime, CancellationToken ct) => IdentityHttp.Respond(async () => {
            _ = IdentityHttp.TenantContext(http); return Results.Json(new { ready = await runtime.IsReadyAsync(ct) }); }));
        group.MapGet("/", (int? page, int? pageSize, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ListAsync(actor.TenantId, Human(http), page ?? 0, pageSize ?? 20, ct)); }));
        group.MapGet("/available", (int? page, int? pageSize, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.AvailableHumanAsync(actor.TenantId, Human(http), page ?? 0, pageSize ?? 20, ct)); }));
        group.MapGet("/editor", (HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ControlsAsync(actor.TenantId, null, Human(http), ct)); }));
        group.MapGet("/{workflowId:guid}/editor", (Guid workflowId, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ControlsAsync(actor.TenantId, workflowId, Human(http), ct)); }));
        group.MapGet("/requests/{key}", (string key, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var receipt = await service.RecoverAsync(actor.TenantId, Human(http), key, ct); return receipt is null ? Results.Text("null", "application/json") : Results.Json(receipt); }));
        group.MapGet("/{workflowId:guid}", (Guid workflowId, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(actor.TenantId, workflowId, Human(http), ct)); }));
        group.MapGet("/{workflowId:guid}/versions/{version:int}", (Guid workflowId, int version, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.VersionAsync(actor.TenantId, workflowId, version, Human(http), ct)); }));
        group.MapPut("/{workflowId:guid}/draft", (Guid workflowId, JsonElement payload, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.SaveAsync(actor.TenantId, workflowId, Human(http), Command<WorkflowDraftWrite>(payload), ct)); }));
        group.MapPost("/{workflowId:guid}/validate", (Guid workflowId, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ValidateAsync(actor.TenantId, workflowId, Human(http), ct)); }));
        group.MapPost("/{workflowId:guid}/publish", (Guid workflowId, JsonElement payload, HttpContext http, TenantWorkflowCatalogService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.PublishAsync(actor.TenantId, workflowId, Human(http), Command<WorkflowPublish>(payload), ct)); }));
        group.MapPost("/{workflowId:guid}/executions", (Guid workflowId, JsonElement payload, HttpContext http, TenantWorkflowExecutionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(StartReceipt(await service.StartHumanAsync(actor.TenantId, workflowId, Human(http), Command<WorkflowStart>(payload), ct))); }));
        var executions = app.MapGroup("/api/v2/tenants/{tenantId:guid}/workflow-executions").RequireAuthorization();
        executions.MapGet("/", (int? page, int? pageSize, HttpContext http, TenantWorkflowExecutionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ListHumanAsync(actor.TenantId, Human(http), page ?? 0, pageSize ?? 20, ct)); }));
        executions.MapGet("/start-requests/{key}", (string key, HttpContext http, TenantWorkflowExecutionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var value = await service.RecoverHumanStartAsync(actor.TenantId, Human(http), key, ct); return value is null ? Results.Text("null", "application/json") : Results.Json(StartReceipt(value)); }));
        executions.MapGet("/{executionId:guid}", (Guid executionId, HttpContext http, TenantWorkflowExecutionService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadHumanAsync(actor.TenantId, executionId, Human(http), ct)); }));
        executions.MapPost("/{executionId:guid}/nodes/{nodeId}/decision", (Guid executionId, string nodeId, JsonElement payload, HttpContext http, TenantWorkflowExecutionService service, IIdGenerator ids, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var request = Command<DecisionSubmit>(payload);
            var result = await service.DecideHumanAsync(new(actor.TenantId, executionId, nodeId, Human(http), request.Approved, request.Rationale, request.RequestKey,
                request.ExpectedRevision, request.ExpectedStepRevision, request.ExpectedSecurityRevision, ids.NewId()), ct);
            return Results.Json(new { result.ExecutionId, result.NodeId, result.Approved, result.At }); }));
        executions.MapPost("/{executionId:guid}/cancel", (Guid executionId, JsonElement payload, HttpContext http, TenantWorkflowExecutionService service, IIdGenerator ids, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var request = Command<CancelSubmit>(payload);
            return Results.Json(StartReceipt(await service.CancelHumanAsync(new(actor.TenantId, executionId, Human(http), request.ExpectedRevision, request.ExpectedSecurityRevision, request.RequestKey, ids.NewId()), ct))); }));
    }
}
