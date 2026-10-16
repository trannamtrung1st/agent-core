using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Api.Mapping;

namespace AgentCore.Api.Security;

public static partial class TenantAgentEndpoints
{
    public sealed record PersonaRequest(AgentIdentity Persona, long ExpectedRevision, long ExpectedInstanceRevision, long ExpectedPersonaRevision);
    public sealed record VersionRequest(int Version, long ExpectedRevision, long ExpectedInstanceRevision);
    private static void MapConfiguration(RouteGroupBuilder group)
    {
        group.MapGet("/agents/{instanceId:guid}/effective-config", (Guid instanceId, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            var value = await service.ReadAsync(a.PrincipalId, a.TenantId, instanceId, ct).ConfigureAwait(false);
            // Configuration eligibility is not tenant execution admission or an IAM grant.
            // Do not export the legacy Admin projection's CanAcceptNewTriggeredWork flag here.
            return Results.Json(new { agent = Safe(value.Agent), value.Definition,
                effective = new { value.Effective.DefinitionSource, value.Effective.DefinitionStatus, value.Effective.EffectiveModel,
                    value.Effective.ProviderPreferences, value.Effective.EffectiveToolAllowlist, value.Effective.MemoryPolicy,
                    value.Effective.TriggerPolicy, value.Effective.KnowledgeSources }, value.Skills, value.Resources, value.SecurityRevision });
        }));
        group.MapPut("/agents/{instanceId:guid}/persona", (Guid instanceId, PersonaRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.UpdatePersonaAsync(a.PrincipalId, a.TenantId, instanceId, body.Persona,
                body.ExpectedRevision, body.ExpectedInstanceRevision, body.ExpectedPersonaRevision, ct).ConfigureAwait(false)));
        }));
        group.MapPut("/agents/{instanceId:guid}/active-version", (Guid instanceId, VersionRequest body, HttpContext http, TenantAgentConfigurationService service, CancellationToken ct) => IdentityHttp.Respond(async () =>
        {
            var a = IdentityHttp.TenantContext(http);
            return Results.Json(Safe(await service.AdoptVersionAsync(a.PrincipalId, a.TenantId, instanceId, body.Version,
                body.ExpectedRevision, body.ExpectedInstanceRevision, ct).ConfigureAwait(false)));
        }));
    }
}
