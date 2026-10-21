using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;
public static class TenantExperienceEndpoints
{
    public static void Map(WebApplication app)
    {
        const string path = "/api/v2/tenants/{tenantId:guid}/agents/{instanceId:guid}/experience";
        app.MapGet(path, (Guid instanceId, HttpContext http, TenantExperienceManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.ReadAsync(actor.TenantId, instanceId, Human(http), Query(http), ct).ConfigureAwait(false)));
        })).RequireAuthorization();
        app.MapGet(path + "/{experienceId:guid}", (Guid instanceId, Guid experienceId, HttpContext http, TenantExperienceManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
            var actor = IdentityHttp.TenantContext(http);
            var page = await service.ReadAsync(actor.TenantId, instanceId, Human(http), Query(http) with { ExperienceId = experienceId, IncludeHistorical = true, Page = 1 }, ct).ConfigureAwait(false);
            if (page.Items.Count != 1) throw new AccessDeniedException("experience_unavailable"); return Results.Json(Safe(page));
        })).RequireAuthorization();
        foreach (var reset in new[] { false, true })
        {
            app.MapPost(path + (reset ? "/reset" : "/visibility"), (Guid instanceId, JsonElement body, HttpContext http, TenantExperienceManagementService service, CancellationToken ct) => IdentityHttp.Respond(async () => {
                if (http.Request.Query.Count != 0) throw AgentCoreErrors.Validation("Supply the exact Experience command in its body.");
                var actor = IdentityHttp.TenantContext(http);
                return Results.Json(await service.ChangeAsync(actor.TenantId, instanceId, Human(http), Change(body, reset), ct).ConfigureAwait(false));
            })).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(4096));
        }
    }
    private static object Safe(TenantExperiencePage page) => new { items = page.Items.Select(i => new {
        i.Experience.ExperienceId, scope = i.Experience.UserPrincipalId is null ? "Instance" : "AgentUser", visibility = i.Experience.Visibility.ToString(),
        i.Experience.Content, i.Experience.Revision, i.Experience.CreatedAt, i.Experience.UpdatedAt, sourceKind = i.Experience.SourceKind.ToString(),
        i.Experience.SourceId, i.Experience.ThroughCursor, sourceExperienceIds = i.Experience.Parents, i.CanManage }),
        page.Total, page.RetainedCount, page.SecurityRevision, page.Page, page.PageSize, page.AgentName, page.CanReset };
    private static HumanSessionReference Human(HttpContext http)
    {
        var a = IdentityHttp.TenantContext(http); return new(a.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), a.PrincipalId);
    }
    private static TenantExperienceQuery Query(HttpContext http)
    {
        foreach (var p in http.Request.Query) if (p.Value.Count != 1 || p.Key is not ("page" or "pageSize" or "includeHistorical")) throw AgentCoreErrors.Validation("Unknown or duplicate Experience query.");
        int Number(string key, int fallback) => http.Request.Query.TryGetValue(key, out var v) ? int.TryParse(v, out var n) ? n : throw AgentCoreErrors.Validation("Invalid pagination.") : fallback;
        var historical = http.Request.Query.TryGetValue("includeHistorical", out var value) ? value.ToString() switch { "true" => true, "false" => false, _ => throw AgentCoreErrors.Validation("Use true or false for historical Experience.") } : false;
        var query = new TenantExperienceQuery(Number("page", 1), Number("pageSize", 25), IncludeHistorical: historical); TenantExperienceManagementRules.Validate(query); return query;
    }
    private static TenantExperienceChange Change(JsonElement body, bool reset)
    {
        var required = reset ? new[] { "expectedRevision", "expectedRetainedCount" } : new[] { "expectedRevision", "experienceId", "expectedExperienceRevision", "visibility" };
        if (body.ValueKind != JsonValueKind.Object) throw AgentCoreErrors.Validation("Supply an exact Experience command.");
        var names = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || required.Except(names, StringComparer.Ordinal).Any())
            throw AgentCoreErrors.Validation("Use only the exact required Experience command fields.");
        long Number(string key) => body.GetProperty(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : throw AgentCoreErrors.Validation("Use exact Experience revisions and counts.");
        var revision = Number("expectedRevision");
        if (reset)
        {
            var count = Number("expectedRetainedCount"); if (count is < 1 or > int.MaxValue) throw AgentCoreErrors.Validation("Invalid retained Experience count.");
            return new(revision, ExpectedRetainedCount: (int)count);
        }
        var id = body.GetProperty("experienceId");
        if (id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "D", out var parsed) || parsed == Guid.Empty) throw AgentCoreErrors.Validation("Select one exact Experience observation.");
        var state = body.GetProperty("visibility");
        var visibility = state.ValueKind == JsonValueKind.String ? state.GetString() switch {
            "Eligible" => ExperienceVisibility.Eligible, "Suppressed" => ExperienceVisibility.Suppressed, "Deleted" => ExperienceVisibility.Deleted,
            _ => throw AgentCoreErrors.Validation("Select Eligible, Suppressed or Deleted.") } : throw AgentCoreErrors.Validation("Select an Experience visibility.");
        return new(revision, parsed, Number("expectedExperienceRevision"), visibility);
    }
}
