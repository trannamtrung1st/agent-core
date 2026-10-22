using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

// Human authoring and exact model-operation preparation share the same semantic plan.
internal sealed record TenantPreparedSkillChange(TenantAgentConfigurationChange? Change,
    InstanceSkillSnapshot Next, InstanceSkillView Skill, DefinitionSkillMutationState? DefinitionSkill);

internal static class TenantAgentSkillPreparation
{
    internal static TenantPreparedSkillChange Prepare(AgentInstance owner, AgentDefinition baseline, InstanceSkillSnapshot snapshot,
        string operation, string? key, long? expectedRevision, InstanceSkillInput? input, bool? enabled, SkillAuthor actor, DateTimeOffset at)
    {
        var definition = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with {
            SelfManagement = ManagementPolicyResolver.Resolve(baseline, owner)
        };
        if (actor == SkillAuthor.Agent && !definition.SelfManagement!.Allows(HarnessManagementScope.Skills))
            throw AgentCoreErrors.Forbidden("Skill administration is outside the effective self-management grant.");
        if (input is not null) { ArgumentNullException.ThrowIfNull(input.Procedure); DefinitionResourcePolicies.RejectSecretTokens(input.Procedure); }
        if (key?.StartsWith("builtin:", StringComparison.Ordinal) == true)
        {
            if (operation is not ("set_enabled" or "reset_enabled")) throw AgentCoreErrors.Forbidden("Built-in procedures are read-only.");
            var existing = AgentInstanceSkillService.BuiltIns(definition, definition.SelfManagement!, owner).SingleOrDefault(s => s.Key == key)
                ?? throw AgentCoreErrors.NotFound("Skill was not found.");
            if (existing.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
            var overrides = owner.HarnessManagement is { } stored ? stored.Overrides ?? ManagementPolicyOverrides.FromLegacy(definition.SelfManagement!) : new();
            bool? choice = operation == "reset_enabled" ? null : enabled ?? throw AgentCoreErrors.Validation("Enabled is required.");
            if (choice is { } selected && existing.EnabledOverride == selected
                || choice is null && existing.EnabledOverride is null && (owner.HarnessManagement is null || owner.HarnessManagement.Overrides is not null))
                return new(null, snapshot, existing, null);
            overrides = key switch {
                ManagementBuiltInSkills.ConfigurationKey => overrides with { ConfigurationAssistance = choice is null ? null : new(choice.Value) },
                ManagementBuiltInSkills.HarnessKey => overrides with { HarnessAuthoring = choice is null ? null : new(choice.Value) },
                _ => throw AgentCoreErrors.Validation("Unknown Built-in Skill.")
            };
            // A Skill choice cannot enable management or remove the local frozen lock.
            var policy = SelfManagementService.Prepare(owner, baseline, overrides, null, false);
            var future = owner with { HarnessManagement = policy };
            var written = AgentInstanceSkillService.BuiltIns(definition, ManagementPolicyResolver.Resolve(baseline, future), future).Single(s => s.Key == key);
            return new(new(owner.InstanceId, owner.Revision, HarnessManagement: policy), snapshot, written, null);
        }
        var prepared = AgentInstanceSkillService.Prepare(owner, definition, snapshot, operation, key, expectedRevision, input, enabled, actor, at);
        if (prepared.Mutation is { } mutation)
        {
            var unchangedLocal = mutation.InstanceSkill is { } local && snapshot.InstanceSkills.SingleOrDefault(s => s.SkillId == local.SkillId) is { } previous
                && JsonSerializer.Serialize(local with { Revision = previous.Revision, UpdatedAt = previous.UpdatedAt }) == JsonSerializer.Serialize(previous);
            var unchangedChoice = mutation.InstanceSkill is null && mutation.DefinitionState is { } choice
                && snapshot.DefinitionStates.Single(s => s.DefinitionSkillId == choice.DefinitionSkillId).EnabledOverride == choice.EnabledOverride;
            if (unchangedLocal || unchangedChoice)
                return new(null, snapshot, AgentInstanceSkillService.Views(definition, snapshot).Single(s => s.Key == key), null);
        }
        _ = EffectiveSkillCatalogResolver.Resolve(definition, prepared.Next);
        return new(prepared.Mutation is null ? null : new(owner.InstanceId, owner.Revision, Skill: prepared.Mutation),
            prepared.Next, prepared.Skill, prepared.DefinitionSkill);
    }
}
