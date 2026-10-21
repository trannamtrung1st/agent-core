using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static class TenantMemoryEndpoints
{
    public static void Map(WebApplication app)
    {
        const string path = "/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/memory";
        app.MapGet(path, (Guid instanceId, HttpContext http, TenantMemoryManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); return Results.Json(Safe(await service.ReadAsync(actor.TenantId, instanceId, Human(http), Query(http), ct).ConfigureAwait(false)));
        })).RequireAuthorization();
        app.MapGet(path + "/{memoryId:guid}", (Guid instanceId, Guid memoryId, HttpContext http, TenantMemoryManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http); var page = await service.ReadAsync(actor.TenantId, instanceId, Human(http), Query(http) with { MemoryId = memoryId, Page = 1 }, ct).ConfigureAwait(false);
            if (page.Items.Count != 1) throw new AccessDeniedException("memory_unavailable"); return Results.Json(Safe(page));
        })).RequireAuthorization();
        app.MapPost(path + "/forget", (Guid instanceId, JsonElement body, HttpContext http, TenantMemoryManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            if (http.Request.Query.Count != 0) throw AgentCoreErrors.Validation("Supply the exact forget command in its body.");
            var actor = IdentityHttp.TenantContext(http); return Results.Json(await service.ForgetAsync(actor.TenantId, instanceId, Human(http), Change(body), ct).ConfigureAwait(false));
        })).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(4096));
    }
    private static object Safe(TenantMemoryPage page) => new { items = page.Items.Select(i => new {
        i.Memory.MemoryId, scope = TenantMemoryRules.ScopeName(i.Memory.Scope), kind = i.Memory.Kind.ToString(), status = i.Memory.Status.ToString(), i.Memory.Subject, i.Memory.Content,
        i.Memory.Revision, i.Memory.CreatedAt, i.Memory.UpdatedAt, originAgentId = i.Memory.AgentInstanceId, i.Memory.Source, i.Memory.SupersedesMemoryId, i.CanForget }),
        page.Total, page.RetainedCount, page.Page, page.PageSize, page.SecurityRevision, page.CanReset, page.AgentName };
    private static HumanSessionReference Human(HttpContext http) { var a = IdentityHttp.TenantContext(http); return new(a.AccountId,
        IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), a.PrincipalId); }
    private static MemoryScope Scope(string? value) => value switch { "session" => MemoryScope.Session, "identityUser" => MemoryScope.IdentityUser, "user" => MemoryScope.User,
        _ => throw AgentCoreErrors.Validation("Select session, identityUser or user Memory scope.") };
    private static TenantMemoryQuery Query(HttpContext http)
    {
        foreach (var p in http.Request.Query) if (p.Value.Count != 1 || p.Key is not ("scope" or "sessionId" or "page" or "pageSize")) throw AgentCoreErrors.Validation("Unknown or duplicate Memory query.");
        var scope = Scope(http.Request.Query["scope"]);
        Guid? session = http.Request.Query.TryGetValue("sessionId", out var id) ? Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty ? parsed : throw AgentCoreErrors.Validation("Invalid Session identity.") : null;
        int Number(string key, int fallback) => http.Request.Query.TryGetValue(key, out var v) ? int.TryParse(v, out var n) ? n : throw AgentCoreErrors.Validation("Invalid pagination.") : fallback;
        var query = new TenantMemoryQuery(scope, session, Number("page", 1), Number("pageSize", 25)); TenantMemoryManagementRules.Validate(query); return query;
    }
    private static TenantMemoryForget Change(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw AgentCoreErrors.Validation("Supply an exact Memory forget command.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in body.EnumerateObject()) if (!names.Add(p.Name) || p.Name is not ("scope" or "sessionId" or "expectedRevision" or "memoryId" or "expectedMemoryRevision" or "expectedRetainedCount")) throw AgentCoreErrors.Validation("Unknown or duplicate Memory command member.");
        string? Text(string key) => !body.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : throw AgentCoreErrors.Validation("Invalid Memory command text.");
        long? Number(string key) => !body.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : throw AgentCoreErrors.Validation("Invalid Memory revision/count.");
        Guid? Id(string key) => Text(key) is { } id ? Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty ? parsed : throw AgentCoreErrors.Validation("Invalid Memory identity.") : null;
        var count = Number("expectedRetainedCount"); if (count is > int.MaxValue or < 0) throw AgentCoreErrors.Validation("Invalid Memory count.");
        return new(new(Scope(Text("scope")), Id("sessionId")), Number("expectedRevision") ?? 0, Id("memoryId"), Number("expectedMemoryRevision"), (int?)count);
    }
}
