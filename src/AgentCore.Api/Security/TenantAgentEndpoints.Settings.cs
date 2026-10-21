using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    public sealed record SettingsRequest(long ExpectedRevision, long ExpectedInstanceRevision,
        IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear);
    public sealed record BudgetsRequest(long ExpectedRevision, long ExpectedInstanceRevision, ExecutionBudgetPolicy? Budgets);
    public sealed record UnattendedRequest(long ExpectedRevision, long ExpectedInstanceRevision, string? CatalogKey, string? ReasoningEffort);
    public sealed record ManagementRequest(long ExpectedRevision, long ExpectedInstanceRevision, ManagementPolicyOverrides Overrides, bool? Frozen = null);
    public sealed record FreezeRequest(long ExpectedRevision, long ExpectedInstanceRevision);
    private static void MapSettings(RouteGroupBuilder group)
    {
        MapSandboxVerification(group);
        MapIsolation(group);
        MapRuntimeState(group);
        group.MapGet("/agents/{instanceId:guid}/sandbox", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadSandboxAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false);
            return Results.Json(new { value.InstanceId, value.InstanceName, value.DefinitionVersion, value.InstanceRevision, lifecycle = value.Lifecycle.ToString(),
                value.Section, value.Profiles, value.CompleteCatalog, value.CanManage, value.SecurityRevision });
        }));
        group.MapGet("/agents/{instanceId:guid}/settings", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReadSettingsAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false));
        }));
        group.MapPatch("/agents/{instanceId:guid}/settings/{section}", (Guid instanceId, string section, SettingsRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.PatchSettingsAsync(a.PrincipalId, a.TenantId, instanceId, section, body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.Set, body.Clear, ct, Human(http)).ConfigureAwait(false), checked(body.ExpectedRevision + 1)));
        }));
        group.MapGet("/agents/{instanceId:guid}/execution-budgets", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReadBudgetsAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false));
        }));
        group.MapPut("/agents/{instanceId:guid}/execution-budgets", (Guid instanceId, BudgetsRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.UpdateBudgetsAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.Budgets, ct, Human(http)).ConfigureAwait(false)));
        }));
        group.MapPut("/agents/{instanceId:guid}/unattended-model", (Guid instanceId, UnattendedRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.UpdateUnattendedModelAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.CatalogKey, body.ReasoningEffort, ct, Human(http)).ConfigureAwait(false)));
        }));
        group.MapGet("/agents/{instanceId:guid}/self-management", (Guid instanceId, HttpContext http, TenantAgentReadService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(await service.ReadManagementAsync(a.TenantId, instanceId, Human(http), ct).ConfigureAwait(false));
        }));
        group.MapPut("/agents/{instanceId:guid}/self-management", (Guid instanceId, ManagementRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.UpdateManagementAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.ExpectedInstanceRevision, body.Overrides, body.Frozen, ct, Human(http)).ConfigureAwait(false)));
        }));
        group.MapPost("/agents/{instanceId:guid}/self-management/freeze", (Guid instanceId, FreezeRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.FreezeManagementAsync(a.PrincipalId, a.TenantId, instanceId, body.ExpectedRevision,
                body.ExpectedInstanceRevision, ct, Human(http)).ConfigureAwait(false)));
        }));
    }
}
