using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed class SystemModelPresetService(IIamStore security, ITenantModelStore store, IdentityService identity,
    IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<IReadOnlyList<ModelPresetSummary>> ReadSummariesAsync(HumanSessionReference human, CancellationToken ct = default)
    {
        await Require(human, "system.model-presets.read", ct);
        var result = await store.ReadModelPresetSummariesAsync(ct);
        await Require(human, "system.model-presets.read", ct);
        return result;
    }
    public async ValueTask<ModelPreset> SaveAsync(HumanSessionReference human, ModelPreset value, long expectedRevision, CancellationToken ct = default)
    {
        await Require(human, "system.model-presets.manage", ct);
        value = ModelManagementRules.Freeze(value);
        var audit = new AuthorizationAudit(ids.NewId(), null, human.PrincipalId, "system.model-presets.manage", value.PresetId.ToString("D"), "allowed", "model_preset_changed", time.GetUtcNow());
        if (!await store.TrySaveModelPresetAsync(value, expectedRevision, human, audit, ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask<AgentCore.Application.Decisions.DecisionEngineStatus> DecisionStatusAsync(HumanSessionReference human, AgentCore.Application.Decisions.IDecisionEngine engine, CancellationToken ct)
    {
        await Require(human, "system.model-presets.read", ct);
        var result = await engine.StatusAsync(ct);
        await Require(human, "system.model-presets.read", ct);
        return result;
    }
    private ValueTask Require(HumanSessionReference human, string permission, CancellationToken ct) =>
        SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, permission, ct);
}
