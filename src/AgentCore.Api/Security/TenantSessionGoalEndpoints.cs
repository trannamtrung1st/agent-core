using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
namespace AgentCore.Api.Security;
public static class TenantSessionGoalEndpoints
{
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record FocusRequest(Guid RequestId, long ExpectedSecurityRevision, long ExpectedSessionRevision, long ExpectedGoalRevision);
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record ContinueRequest(Guid RequestId, long ExpectedSecurityRevision, long ExpectedSessionRevision, long ExpectedGoalRevision, long ExpectedPlanRevision);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}/sessions/{sessionId:guid}/goals").RequireAuthorization();
        group.MapGet("", (Guid sessionId, int? page, int? pageSize, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ListAsync(actor.TenantId, sessionId, Human(http, actor), page ?? 0, pageSize ?? 20, ct)); }));
        group.MapGet("/recovery", (Guid sessionId, string requestKey, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.RecoverAsync(actor.TenantId, sessionId, Human(http, actor), requestKey, ct)); }));
        group.MapGet("/{goalId:guid}", (Guid sessionId, Guid goalId, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(actor.TenantId, sessionId, goalId, Human(http, actor), ct)); }));
        group.MapPost("", (Guid sessionId, GoalCreateRequest body, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); if (body.Command.Kind != GoalCommandKind.Create) throw new ArgumentException("Create requires a Create command.");
            return Results.Json(await service.MutateAsync(actor.TenantId, sessionId, body.GoalId, Human(http, actor), body.Command, ct)); }));
        foreach (var (route, kind) in new[] { ("plan", GoalCommandKind.Plan), ("plan-decision", GoalCommandKind.PlanDecision), ("acceptance", GoalCommandKind.Acceptance), ("control", GoalCommandKind.Control) })
            group.MapMethods("/{goalId:guid}/" + route, route == "plan" ? ["PUT"] : ["POST"], (Guid sessionId, Guid goalId, GoalCommand body, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
                var actor = IdentityHttp.TenantContext(http); if (body.Kind != kind) throw new ArgumentException("Command does not match this route.");
                return Results.Json(await service.MutateAsync(actor.TenantId, sessionId, goalId, Human(http, actor), body, ct)); }));
        foreach (var (route, kind) in new[] { ("progress", GoalCommandKind.Progress), ("verify", GoalCommandKind.Verify) })
            group.MapPost("/{goalId:guid}/checkpoints/{checkpointId:guid}/" + route, (Guid sessionId, Guid goalId, Guid checkpointId, GoalCommand body, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
                var actor = IdentityHttp.TenantContext(http); if (body.Kind != kind || body.CheckpointId != checkpointId) throw new ArgumentException("Command does not match this checkpoint.");
                return Results.Json(await service.MutateAsync(actor.TenantId, sessionId, goalId, Human(http, actor), body, ct)); }));
        group.MapPost("/{goalId:guid}/focus", (Guid sessionId, Guid goalId, FocusRequest body, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.FocusAsync(actor.TenantId, sessionId, goalId, Human(http, actor), body.RequestId, body.ExpectedSecurityRevision, body.ExpectedSessionRevision, body.ExpectedGoalRevision, ct)); }));
        group.MapPost("/{goalId:guid}/continue", (Guid sessionId, Guid goalId, ContinueRequest body, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var receipt = await service.ContinueAsync(actor.TenantId, sessionId, goalId, Human(http, actor), body.RequestId, body.ExpectedSecurityRevision, body.ExpectedSessionRevision, body.ExpectedGoalRevision, body.ExpectedPlanRevision, ct);
            return Results.Json(receipt, statusCode: receipt.Created ? 202 : 200); }));
        group.MapGet("/{goalId:guid}/activity", (Guid sessionId, Guid goalId, int? page, int? pageSize, HttpContext http, TenantSessionGoalService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ActivityAsync(actor.TenantId, sessionId, goalId, Human(http, actor), page ?? 0, pageSize ?? 20, ct)); }));
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record GoalCreateRequest(Guid GoalId, GoalCommand Command);
    private static TenantSessionInitiator Human(HttpContext http, AuthenticatedTenant actor) => new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
}
