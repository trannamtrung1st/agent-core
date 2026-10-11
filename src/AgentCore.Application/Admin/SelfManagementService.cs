using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record ManagementPolicyView(long InstanceRevision, long PolicyRevision, string Source,
    HarnessManagementPolicy Effective, HarnessManagementPolicy DefinitionDefaults, ManagementPolicyOverrides? Overrides);

/// <summary>Policy uses the existing Instance JSON, CAS and audit transaction. No parallel configuration store.</summary>
public sealed class SelfManagementService(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<ManagementPolicyView> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var owner = await OwnerAsync(id, ct);
        var definition = await DefinitionAsync(owner, ct);
        return new(owner.Revision, owner.HarnessManagement?.PolicyRevision ?? 1,
            owner.HarnessManagement is null || owner.HarnessManagement.Overrides is { Mode: null, Scopes: null, AutoApply: null, ConfigurationAssistance: null, HarnessAuthoring: null } ? "definition" : owner.HarnessManagement.Overrides is null ? "instance" : "per-field",
            ManagementPolicyResolver.Resolve(definition, owner), definition.SelfManagement ?? HarnessManagementPolicy.Disabled,
            owner.HarnessManagement?.Overrides);
    }

    public async ValueTask<ManagementPolicyView> UpdateAsync(Guid id, long revision, ManagementPolicyOverrides overrides,
        bool? frozen = null, bool agent = false, CancellationToken ct = default, AdminEventActorKind? attribution = null)
    {
        var owner = await OwnerAsync(id, ct);
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived Instances are read-only.");
        if (owner.Revision != revision) throw AgentCoreErrors.Conflict("Instance revision is stale. Inspect again.");
        var definition = await DefinitionAsync(owner, ct);
        var effective = ManagementPolicyResolver.Resolve(definition, owner);
        if (agent && (!Tools.ManagementTools.Enabled(effective) || !effective.Allows(HarnessManagementScope.ManagementPolicy) || frozen == false))
            throw AgentCoreErrors.Forbidden("Only Admin can enable or unfreeze self-management.");
        Validate(overrides);
        var old = owner.HarnessManagement ?? new(effective);
        var policy = effective with { Frozen = frozen ?? effective.Frozen,
            // This is an eligibility ceiling, never a grant. Harness operations still require current role authority.
            EligibleTools = effective.EligibleTools.Concat(RoleEnvironments.Of(definition).ToolList.Append(ToolCatalog.AttachmentsRead))
                .Where(t => ToolRegistry.TryGet(t, out var descriptor) && descriptor.DefinitionAuthorizable
                    && RolePermissions.AllowsTool(definition, t)).Distinct().ToArray() };
        var next = old with { Policy = policy, Overrides = overrides, PolicyRevision = old.PolicyRevision + 1,
            Preparation = old.Preparation is { Status: not HarnessPreparationStatus.Published } prep ? prep with {
                Status = HarnessPreparationStatus.Cancelled,
                Approvals = prep.Approvals.Select(a => a.Status == "Pending" ? a with { Status = "Cancelled" } : a).ToArray() } : old.Preparation };
        var previous = old.Overrides;
        var fields = new List<string>();
        void Changed(string field, object? before, object? after) { if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after)) fields.Add(field); }
        Changed("mode", previous is null ? new InstanceField<HarnessManagementMode>(effective.Mode) : previous.Mode, overrides.Mode);
        Changed("scopes", previous is null ? new InstanceField<HarnessManagementScope[]>(effective.Scopes.ToArray()) : previous.Scopes, overrides.Scopes);
        Changed("managedAutoApply", previous is null ? new InstanceField<ManagementAutoApply[]>((effective.ManagedAutoApply ?? []).ToArray()) : previous.AutoApply, overrides.AutoApply);
        Changed("configurationAssistance", previous is null ? new InstanceField<bool>(effective.BuiltInSkills?.ConfigurationAssistance ?? true) : previous.ConfigurationAssistance, overrides.ConfigurationAssistance);
        Changed("harnessAuthoring", previous is null ? new InstanceField<bool>(effective.BuiltInSkills?.HarnessAuthoring ?? true) : previous.HarnessAuthoring, overrides.HarnessAuthoring);
        Changed("frozen", effective.Frozen, policy.Frozen);
        if (old.Overrides is not null && fields.Count == 0) return await ReadAsync(id, ct);
        var now = time.GetUtcNow();
        var history = new AdminEventAppend(ids.NewId(), now, attribution ?? (agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner),
            AdminEventOperationKind.HarnessPolicyChanged, "agent.instance", id.ToString("D"), revision + 1, owner.ActiveVersion,
            JsonSerializer.Serialize(new { instanceId = id, operation = "self-management.policy", outcome = "Applied", policyRevision = next.PolicyRevision, changedFields = fields }));
        await instances.UpdateWithExpectedRevisionAsync(new(id, revision, HarnessManagement: next, History: history), now, ct);
        return await ReadAsync(id, ct);
    }

    public async ValueTask<ManagementPolicyView> SetBuiltInAsync(Guid id, long revision, string key, bool? enabled, bool agent, CancellationToken ct)
    {
        var owner = await OwnerAsync(id, ct);
        var effective = ManagementPolicyResolver.Resolve(await DefinitionAsync(owner, ct), owner);
        if (agent && !effective.Allows(HarnessManagementScope.Skills)) throw AgentCoreErrors.Forbidden("Built-in enablement is outside the management grant.");
        var overrides = owner.HarnessManagement is { } stored
            ? stored.Overrides ?? ManagementPolicyOverrides.FromLegacy(effective)
            : new ManagementPolicyOverrides();
        overrides = key switch {
            AgentCore.Application.Agents.ManagementBuiltInSkills.ConfigurationKey => overrides with { ConfigurationAssistance = enabled is null ? null : new(enabled.Value) },
            AgentCore.Application.Agents.ManagementBuiltInSkills.HarnessKey => overrides with { HarnessAuthoring = enabled is null ? null : new(enabled.Value) },
            _ => throw AgentCoreErrors.Validation("Unknown Built-in Skill.")
        };
        return await UpdateAsync(id, revision, overrides, ct: ct, attribution: agent ? AdminEventActorKind.Agent : AdminEventActorKind.LocalOwner);
    }

    public async ValueTask<ManagementPolicyView> FreezeAsync(Guid id, long revision, CancellationToken ct = default)
    {
        var owner = await OwnerAsync(id, ct);
        var effective = ManagementPolicyResolver.Resolve(await DefinitionAsync(owner, ct), owner);
        if (!Tools.ManagementTools.Enabled(effective)) throw AgentCoreErrors.Forbidden("Self-management is disabled or frozen.");
        // Shutdown is authorized independently of policy-widening scope. CAS defeats concurrent Admin changes.
        if (owner.Revision != revision) throw AgentCoreErrors.Conflict("Instance revision is stale. Inspect again.");
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived Instances are read-only.");
        var old = owner.HarnessManagement ?? new(effective);
        var next = old with { Policy = effective with { Frozen = true }, PolicyRevision = old.PolicyRevision + 1,
            Overrides = owner.HarnessManagement is null ? new() : old.Overrides,
            Preparation = old.Preparation is { Status: not HarnessPreparationStatus.Published } prep ? prep with {
                Status = HarnessPreparationStatus.Cancelled,
                Approvals = prep.Approvals.Select(a => a.Status == "Pending" ? a with { Status = "Cancelled" } : a).ToArray() } : old.Preparation };
        var now = time.GetUtcNow();
        await instances.UpdateWithExpectedRevisionAsync(new(id, revision, HarnessManagement: next, History: new(ids.NewId(), now,
            AdminEventActorKind.Agent, AdminEventOperationKind.HarnessPolicyChanged, "agent.instance", id.ToString("D"), revision + 1,
            owner.ActiveVersion, JsonSerializer.Serialize(new { instanceId = id, operation = "management.freeze", outcome = "Frozen", policyRevision = next.PolicyRevision, changedFields = new[] { "frozen" } }))), now, ct);
        return await ReadAsync(id, ct);
    }

    public static void Validate(ManagementPolicyOverrides overrides)
    {
        if (overrides.Mode is { } mode && !Enum.IsDefined(mode.Value)
            || overrides.Scopes is { } scopes && (scopes.Value is null || scopes.Value.Length > 9 || scopes.Value.Distinct().Count() != scopes.Value.Length || scopes.Value.Any(s => !Enum.IsDefined(s)))
            || overrides.AutoApply is { } auto && (auto.Value is null || auto.Value.Length > 3 || auto.Value.Distinct().Count() != auto.Value.Length || auto.Value.Any(s => !Enum.IsDefined(s))))
            throw AgentCoreErrors.Validation("Self-management policy contains an invalid or duplicate operation.");
    }
    private async ValueTask<AgentInstance> OwnerAsync(Guid id, CancellationToken ct) =>
        await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
    private async ValueTask<AgentDefinition> DefinitionAsync(AgentInstance owner, CancellationToken ct) =>
        await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
}
