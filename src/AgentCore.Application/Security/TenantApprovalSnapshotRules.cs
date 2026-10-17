using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantApprovalSnapshotRules
{
    public static TenantApprovalSnapshot Persona(TenantAgent agent) => new(agent.Instance.Persona.Name, agent.Instance.Persona);
    public static TenantApprovalSnapshot Preferences(TenantAgent agent, AgentDefinition baseline) => new(agent.Instance.Persona.Name,
        PreferenceDefaults: new(baseline.ConversationPolicy.ResponseLength, baseline.ConversationPolicy.Language, baseline.ConversationPolicy.AskOneQuestionAtATime),
        PreferenceOverrides: Overrides(agent.Instance.SettingsOverrides));

    public static TenantApprovalPreferenceOverrides Overrides(InstanceSettingsOverrides? value) =>
        new(value?.ResponseLength?.Value, value?.Language?.Value, value?.AskOneQuestionAtATime?.Value);

    public static void Shape(TenantApprovalRequest request)
    {
        if (request.Snapshot is not { } s) return; // Earlier saved requests may lack display evidence; never invent it.
        if (string.IsNullOrWhiteSpace(s.AgentName) || s.AgentName.Length > 256)
            throw new ArgumentException("Invalid approval display identity.");
        if (request.Operation.Kind == TenantLocalOperationKind.Persona)
        {
            if (s.BeforePersona is null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.BeforePersona.Name != s.AgentName)
                throw new ArgumentException("Invalid persona review snapshot.");
            AgentDefinitionValidator.ValidateIdentity(s.BeforePersona);
        }
        else
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not { } defaults || s.PreferenceOverrides is not { } overrides
                || defaults.ResponseLength is not ("concise" or "balanced") || !Language(defaults.Language)
                || overrides.ResponseLength is not (null or "concise" or "balanced") || overrides.Language is { } language && !Language(language))
                throw new ArgumentException("Invalid preference review snapshot.");
        }
        static bool Language(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && !value.Any(char.IsControl);
    }

    public static void RequireCurrent(TenantApprovalRequest request, TenantAgent agent)
    {
        Shape(request);
        if (request.Snapshot is not { } snapshot) return;
        if (snapshot.AgentName != agent.Instance.Persona.Name
            || request.Operation.Kind == TenantLocalOperationKind.Persona && snapshot.BeforePersona != agent.Instance.Persona
            || request.Operation.Kind == TenantLocalOperationKind.ConversationPreferences && snapshot.PreferenceOverrides != Overrides(agent.Instance.SettingsOverrides))
            throw new ArgumentException("Review snapshot does not match the witnessed target.");
    }
}
