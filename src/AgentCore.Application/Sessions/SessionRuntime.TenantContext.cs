using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly TenantAttachmentRuntimeContextService? _tenantAttachments;
    private async ValueTask<TenantSkillProjection> SkillContextAsync(AgentRun? run, IReadOnlyList<AgentCore.Domain.Definitions.EffectiveSkill> catalog, CancellationToken ct)
    {
        if (_runtimeOwner is null || catalog.All(AgentCore.Application.Agents.ManagementBuiltInSkills.IsBuiltIn)) return new(catalog, []);
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not ITenantRunContext source)
            throw AgentCoreErrors.Forbidden("Tenant Skill context is unavailable.");
        return await source.CurrentSkillContextAsync(run, ct).ConfigureAwait(false);
    }
    private async ValueTask<HarnessChatContext?> HarnessContextAsync(AgentRun? run, CancellationToken ct)
    {
        if (_runtimeOwner is null) return await _tools.HarnessContextAsync(_snapshot.AgentInstanceId, ct).ConfigureAwait(false);
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not ITenantRunContext source)
            throw AgentCoreErrors.Forbidden("Tenant management context is unavailable.");
        return await source.CurrentHarnessContextAsync(run, ct).ConfigureAwait(false);
    }

    private async ValueTask RequireTenantAttachmentSourcesAsync(AgentRun? run, IReadOnlyList<TenantAttachmentContextSource>? sources, CancellationToken ct)
    {
        if (_runtimeOwner is null || sources is not { Count: > 0 }) return;
        if (run is null || run.Owner != _runtimeOwner || _tenantAttachments is null) throw AgentCoreErrors.Forbidden("Tenant attachment context is unavailable.");
        await _tenantAttachments.RequireSourcesAsync(run, sources, ct).ConfigureAwait(false);
    }

    private async ValueTask<TenantRequesterContext?> RequesterContextAsync(AgentRun? run, CancellationToken ct)
    {
        if (_runtimeOwner is null) return null;
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not ITenantRunContext source)
            throw AgentCoreErrors.Forbidden("Tenant requester context is unavailable.");
        return await source.CurrentRequesterAsync(run, ct).ConfigureAwait(false);
    }
    private async ValueTask<AgentRunContinuitySettings?> CurrentContinuitySettingsAsync(AgentRun? run, CancellationToken ct)
    {
        if (_runtimeOwner is null) return null;
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not ITenantRunContext source) throw AgentCoreErrors.Forbidden("Tenant continuity context is unavailable.");
        return await source.CurrentContinuitySettingsAsync(run, ct).ConfigureAwait(false);
    }

}
