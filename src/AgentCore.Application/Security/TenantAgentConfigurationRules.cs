using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public static class TenantAgentConfigurationRules
{
    public static AgentInstance Apply(AgentInstance current, TenantAgentConfigurationChange change, DateTimeOffset at)
    {
        if (current.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived Instances are read-only.");
        if (current.InstanceId != change.InstanceId || change.ExpectedInstanceRevision < 1
            || (change.Persona is null) == (change.ActiveVersion is null)
            || change.Persona is not null && (change.ExpectedPersonaRevision is null || change.DefinitionSkills is not null || change.ExpectedPublicationRevision is not null || change.AutomationPolicyChanges is not null)
            || change.ActiveVersion is not null && (change.ActiveVersion < 1 || change.DefinitionSkills is null || change.AutomationPolicyChanges is null || change.ExpectedPersonaRevision is not null))
            throw new ArgumentException("An exact persona or publication change is required.");
        if (current.Revision != change.ExpectedInstanceRevision || change.Persona is not null && current.PersonaRevision != change.ExpectedPersonaRevision)
            throw new SecurityConflictException("stale_revision");
        if (change.Persona is { } persona) AgentDefinitionValidator.ValidateIdentity(persona);
        return current with { Persona = change.Persona ?? current.Persona, ActiveVersion = change.ActiveVersion ?? current.ActiveVersion,
            PersonaRevision = current.PersonaRevision + (change.Persona is not null && change.Persona != current.Persona ? 1 : 0),
            Revision = checked(current.Revision + 1), UpdatedAt = at };
    }
}
