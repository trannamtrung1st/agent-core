using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Tasks;

namespace AgentCore.Api.Security;

public static class TenantTaskEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record DelegateRequest(string Objective, JsonElement Target, string RequestKey, long ExpectedSecurityRevision,
        string? Title = null, Guid? SpaceId = null, Guid? EnvironmentId = null, Guid[]? DependsOnTaskIds = null,
        bool ReportOnCompletion = false, DateTimeOffset? DeadlineAt = null, Guid? RetryOfTaskId = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CancelRequest(string RequestKey, long ExpectedRevision, long ExpectedSecurityRevision, string? Reason = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record GuidanceRequest(string Text, string RequestKey, long ExpectedSecurityRevision);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record AcknowledgeRequest(string RequestKey, long ExpectedRevision, long ExpectedSecurityRevision);
    private static TenantSessionInitiator Human(HttpContext http)
    {
        var actor = IdentityHttp.TenantContext(http);
        return new(actor.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), actor.PrincipalId);
    }
    private static T Command<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web) {
                AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false
            }) ?? throw new ArgumentException("Use a closed Task command.");
        }
        catch (JsonException error) { throw new ArgumentException("Use unique, closed Task command fields.", error); }
    }
    private static TaskRequest Request(DelegateRequest body)
    {
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            return TenantTaskToolService.ParseDelegate(JsonSerializer.SerializeToElement(body, options));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ArgumentException("Use the closed Task request with a concrete target and bounded objective.", error); }
    }
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:guid}").RequireAuthorization();
        group.MapGet("/sessions/{sessionId:guid}/tasks", (Guid sessionId, string? scope, Guid? cursor, int? limit, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ListAsync(actor.TenantId, Human(http), sessionId, null, scope ?? "created", cursor, limit ?? 20, ct)); }));
        group.MapGet("/sessions/{sessionId:guid}/task-targets", (Guid sessionId, int? page, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.TargetsAsync(actor.TenantId, Human(http), sessionId, page ?? 0, ct)); }));
        group.MapGet("/sessions/{sessionId:guid}/tasks/recovery", (Guid sessionId, string requestKey, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.RecoverAsync(actor.TenantId, sessionId, Human(http), requestKey, ct)); }));
        group.MapPost("/sessions/{sessionId:guid}/tasks", (Guid sessionId, JsonElement payload, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var body = Command<DelegateRequest>(payload); var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.DelegateAsync(actor.TenantId, sessionId, Human(http), Request(body), body.ExpectedSecurityRevision, ct)); }));
        group.MapGet("/tasks/{taskId:guid}", (Guid taskId, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ReadAsync(actor.TenantId, Human(http), taskId, ct)); }));
        group.MapPost("/tasks/{taskId:guid}/cancel", (Guid taskId, JsonElement payload, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var body = Command<CancelRequest>(payload); var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.CancelAsync(actor.TenantId, taskId, Human(http), body.ExpectedRevision, body.ExpectedSecurityRevision, body.RequestKey, body.Reason, ct)); }));
        group.MapGet("/tasks/{taskId:guid}/messages", (Guid taskId, Guid? cursor, int? limit, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.MessagesAsync(actor.TenantId, Human(http), taskId, null, cursor, limit ?? 20, ct)); }));
        group.MapGet("/tasks/{taskId:guid}/messages/recovery", (Guid taskId, string requestKey, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.RecoverMessageAsync(actor.TenantId, taskId, Human(http), requestKey, ct)); }));
        group.MapGet("/tasks/{taskId:guid}/requests/{requestKey}", (Guid taskId, string requestKey, string kind, Guid? notificationId, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.RecoverCommandAsync(actor.TenantId, taskId, Human(http), requestKey, kind, notificationId, ct)); }));
        group.MapPost("/tasks/{taskId:guid}/messages", (Guid taskId, JsonElement payload, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var body = Command<GuidanceRequest>(payload); var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.GuideAsync(actor.TenantId, taskId, Human(http), body.Text, body.RequestKey, body.ExpectedSecurityRevision, ct)); }));
        group.MapGet("/tasks/{taskId:guid}/notifications", (Guid taskId, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.NotificationsAsync(actor.TenantId, Human(http), taskId, ct)); }));
        group.MapPost("/tasks/{taskId:guid}/notifications/{notificationId:guid}/acknowledge", (Guid taskId, Guid notificationId, JsonElement payload, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var body = Command<AcknowledgeRequest>(payload); var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.AcknowledgeAsync(actor.TenantId, Human(http), taskId, notificationId, body.ExpectedRevision, body.ExpectedSecurityRevision, body.RequestKey, ct)); }));
        group.MapGet("/agents/{instanceId:guid}/tasks", (Guid instanceId, string? scope, Guid? cursor, int? limit, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ListAsync(actor.TenantId, Human(http), null, instanceId, scope ?? "assigned", cursor, limit ?? 20, ct)); }));
        group.MapGet("/agent-threads", (Guid instanceId, Guid? cursor, int? limit, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ThreadsAsync(actor.TenantId, Human(http), instanceId, cursor, limit ?? 20, ct)); }));
        group.MapGet("/agent-threads/{threadId:guid}/messages", (Guid threadId, Guid? cursor, int? limit, HttpContext http, TenantTaskAccessService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.MessagesAsync(actor.TenantId, Human(http), null, threadId, cursor, limit ?? 20, ct)); }));
    }
}
