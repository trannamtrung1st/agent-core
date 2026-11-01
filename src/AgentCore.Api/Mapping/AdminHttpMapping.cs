using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Domain.Events;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Mapping;

internal static class AdminHttpMapping
{
    public static AgentInstanceSkillResponse ToInstanceSkill(InstanceSkillView value) => new(value.Key, value.Origin.ToString(), value.Name, value.Description,
        value.Procedure, value.Projection.ToString(), value.Enabled, value.RequiredCapabilities, value.Revision, value.DefinitionVersion,
        value.SourceDefinitionId, value.SourceDefinitionVersion, value.SourceDefinitionSkillId, value.MissingCapabilities,
        value.CreatedBy?.ToString(), value.CreatedAt, value.UpdatedAt, value.EnabledOverride, value.EnabledChoice);

    public static AdminAgentInstanceResponse ToAgentInstance(AgentInstance instance) =>
        new(
            instance.InstanceId.ToString("D"),
            instance.DefinitionId,
            instance.ActiveVersion,
            instance.Lifecycle.ToString(),
            instance.Revision,
            instance.PersonaRevision,
            ToPersona(instance.Persona),
            ModelManagementMapping.Map(instance.UnattendedModel), ExecutionBudgets: instance.ExecutionBudgets is null ? null : JsonSerializer.SerializeToElement(instance.ExecutionBudgets, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Cooperation: instance.Cooperation is { } cooperation ? new(cooperation.AcceptTasks, cooperation.AcceptDirectMessages, cooperation.AllowDirectMessageWake, cooperation.MaxConcurrentTasks) : new(false, false, false, 2));

    private static AdminPersonaResponse ToPersona(AgentIdentity persona) =>
        new(persona.Name, persona.Role, persona.Description, persona.Tone);
}
