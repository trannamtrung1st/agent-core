using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Security;

public static class TenantUserProfileEndpoints
{
    public static void Map(WebApplication app)
    {
        const string path = "/api/v2/tenants/{tenantId:guid}/me/profile";
        app.MapGet(path, (HttpContext http, TenantUserProfileService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            NoQuery(http); var actor = IdentityHttp.TenantContext(http);
            return Safe(await service.ReadAsync(actor.TenantId, Human(http), ct).ConfigureAwait(false));
        })).RequireAuthorization();
        app.MapPatch(path, (JsonElement body, HttpContext http, TenantUserProfileService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            NoQuery(http); var actor = IdentityHttp.TenantContext(http);
            return Safe(await service.UpdateAsync(actor.TenantId, Human(http), Parse(body), ct).ConfigureAwait(false));
        })).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(4096));
    }
    private static IResult Safe(TenantUserProfileView value) => Results.Json(new { profile = new {
        value.Profile.TenantId, value.Profile.PrincipalId, value.Profile.Revision, preferences = value.Profile.Preferences.ToDictionary(p => p.Key,
            p => new { p.Value.Value, source = "UserSet", p.Value.UpdatedAt }, StringComparer.Ordinal), value.Profile.UpdatedAt }, value.SecurityRevision });
    private static HumanSessionReference Human(HttpContext http)
    {
        var a = IdentityHttp.TenantContext(http);
        return new(a.AccountId, IdentityHttp.SessionId(http.User) ?? throw new AccessDeniedException("initiator_unavailable"), a.PrincipalId);
    }
    private static void NoQuery(HttpContext http)
    { if (http.Request.Query.Count != 0) throw AgentCoreErrors.Validation("User Profile self-service accepts no owner or query parameters."); }
    private static TenantUserProfileChange Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw AgentCoreErrors.Validation("Supply a User Profile change object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in body.EnumerateObject())
            if (!names.Add(p.Name) || p.Name is not ("expectedRevision" or "expectedProfileRevision" or "values")) throw AgentCoreErrors.Validation("Unknown or duplicate User Profile member.");
        if (names.Count != 3 || body.GetProperty("expectedRevision").ValueKind != JsonValueKind.Number
            || !body.GetProperty("expectedRevision").TryGetInt64(out var security)
            || body.GetProperty("expectedProfileRevision").ValueKind != JsonValueKind.Number
            || !body.GetProperty("expectedProfileRevision").TryGetInt64(out var revision)
            || body.GetProperty("values").ValueKind != JsonValueKind.Object) throw AgentCoreErrors.Validation("Supply current revisions and Profile values.");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in body.GetProperty("values").EnumerateObject())
        {
            if (p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                || !values.TryAdd(p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString())) throw AgentCoreErrors.Validation("Duplicate or invalid Profile value.");
        }
        return new(security, revision, values);
    }
}
