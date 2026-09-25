using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Identity;

public static class AgentInstancePersonaEditor
{
    public static AgentIdentity Parse(string name, string role, string description, string tone)
    {
        static string Require(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw AgentCoreErrors.Validation($"{field} is required.");
            }

            return value.Trim();
        }

        var persona = new AgentIdentity(
            Require(name, "name"),
            Require(role, "role"),
            Require(description, "description"),
            Require(tone, "tone"));
        try
        {
            AgentDefinitionValidator.ValidateIdentity(persona);
        }
        catch (ArgumentException ex)
        {
            throw AgentCoreErrors.Validation(ex.Message);
        }

        DefinitionResourcePolicies.RejectSecretTokens(persona.Name);
        DefinitionResourcePolicies.RejectSecretTokens(persona.Role);
        DefinitionResourcePolicies.RejectSecretTokens(persona.Description);
        DefinitionResourcePolicies.RejectSecretTokens(persona.Tone);
        return persona;
    }
}
