using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public static class TenantAgentConfigurationRules
{
    public static AgentInstance Apply(AgentInstance current, TenantAgentConfigurationChange change, DateTimeOffset at)
    {
        if (current.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived Instances are read-only.");
        var operations = (change.Persona is null ? 0 : 1) + (change.ActiveVersion is null ? 0 : 1)
            + (change.SetSettingsOverrides ? 1 : 0) + (change.SetExecutionBudgets ? 1 : 0)
            + (change.SetUnattendedModel ? 1 : 0) + (change.HarnessManagement is null ? 0 : 1)
            + (change.Skill is null ? 0 : 1) + (change.Resource is null ? 0 : 1);
        if (current.InstanceId != change.InstanceId || change.ExpectedInstanceRevision < 1 || operations != 1
            || change.Persona is not null && change.ExpectedPersonaRevision is null
            || change.Persona is null && change.ExpectedPersonaRevision is not null
            || change.ActiveVersion is not null && (change.ActiveVersion < 1 || change.DefinitionSkills is null || change.AutomationPolicyChanges is null)
            || change.ActiveVersion is null && (change.DefinitionSkills is not null || change.ExpectedPublicationRevision is not null)
            || change.SetSettingsOverrides && change.AutomationPolicyChanges is null
            || !change.SetSettingsOverrides && change.SettingsOverrides is not null
            || !change.SetExecutionBudgets && change.ExecutionBudgets is not null
            || !change.SetUnattendedModel && (change.UnattendedModelCatalogKey is not null || change.UnattendedReasoningEffort is not null)
            || change.ActiveVersion is null && !change.SetSettingsOverrides && change.AutomationPolicyChanges is not null)
            throw new ArgumentException("One exact Instance configuration change is required.");
        if (change.Skill is { } skill && (skill.InstanceId != current.InstanceId || skill.ExpectedInstanceRevision != current.Revision || skill.History is not null)
            || change.Resource is { } resource && (resource.InstanceId != current.InstanceId || resource.ExpectedInstanceRevision != current.Revision || resource.History is not null || resource.HarnessManagement is not null))
            throw new ArgumentException("Child ownership and revision must match the protected Instance.");
        if (change.Skill?.InstanceSkill is { } local && local.AgentInstanceId != current.InstanceId
            || change.Skill?.DefinitionState is { } inherited && inherited.AgentInstanceId != current.InstanceId)
            throw new ArgumentException("Skill state belongs to another Instance.");
        if (change.Skill is { } skillChange && (skillChange.DefinitionState is null && skillChange.InstanceSkill is null && skillChange.DeleteSkillId is null
                || skillChange.DeleteSkillId is not null && (skillChange.InstanceSkill is not null || skillChange.DefinitionState is not null))
            || change.Resource is { } resourceChange && (resourceChange.Resource is null ? 0 : 1)
                + (resourceChange.DefinitionState is null ? 0 : 1) + (resourceChange.DeleteResourceId is null ? 0 : 1) != 1)
            throw new ArgumentException("One child operation is required; only Skill customization combines a source choice and a local copy.");
        if (current.Revision != change.ExpectedInstanceRevision || change.Persona is not null && current.PersonaRevision != change.ExpectedPersonaRevision)
            throw new SecurityConflictException("stale_revision");
        if (change.Persona is { } persona) AgentDefinitionValidator.ValidateIdentity(persona);
        if (change.SetExecutionBudgets) change.ExecutionBudgets?.Validate();
        if (change.HarnessManagement?.Overrides is { } overrides) Admin.SelfManagementService.Validate(overrides);
        return current with { Persona = change.Persona ?? current.Persona, ActiveVersion = change.ActiveVersion ?? current.ActiveVersion,
            PersonaRevision = current.PersonaRevision + (change.Persona is not null && change.Persona != current.Persona ? 1 : 0),
            Revision = checked(current.Revision + 1), UpdatedAt = at,
            SettingsOverrides = change.SetSettingsOverrides ? change.SettingsOverrides?.Copy() : current.SettingsOverrides,
            ExecutionBudgets = change.SetExecutionBudgets ? change.ExecutionBudgets : current.ExecutionBudgets,
            UnattendedModelCatalogKey = change.SetUnattendedModel ? Normalize(change.UnattendedModelCatalogKey) : current.UnattendedModelCatalogKey,
            UnattendedReasoningEffort = change.SetUnattendedModel ? Normalize(change.UnattendedReasoningEffort) : current.UnattendedReasoningEffort,
            HarnessManagement = change.HarnessManagement ?? current.HarnessManagement };
    }
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
