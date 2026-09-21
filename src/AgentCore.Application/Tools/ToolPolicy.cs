using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolPolicy
{
    public static ToolPolicyDecision EvaluateExecution(
        AgentDefinition definition,
        string toolName,
        IToolConfigurationGate configurationGate,
        ToolApprovalGrant? grant = null)
    {
        if (!ToolRegistry.TryGet(toolName, out var descriptor))
        {
            return ToolPolicyDecision.Deny;
        }

        if (!RolePermissions.AllowsTool(definition, toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule is ToolOfferRule.RoleAllowlist or ToolOfferRule.ConfigurationWhenRoleAllows
            && !RoleEnvironments.Of(definition).ToolList.Contains(toolName, StringComparer.Ordinal))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.ConfigurationWhenRoleAllows
            && !configurationGate.IsConfigured(toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.Effect is ToolEffect.SensitiveWrite or ToolEffect.Destructive)
        {
            if (grant is null
                || !string.Equals(grant.ToolName, toolName, StringComparison.Ordinal)
                || grant.ActionHash.Length == 0)
            {
                return ToolPolicyDecision.RequireApproval;
            }

            return ToolPolicyDecision.Allow;
        }

        return ToolPolicyDecision.Allow;
    }

    public static bool IsOffered(
        ToolDescriptor descriptor,
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        if (context is not null && !context.ModelSupportsTools)
        {
            return false;
        }

        if (!RolePermissions.AllowsTool(definition, descriptor.Name))
        {
            return false;
        }

        return descriptor.OfferRule switch
        {
            ToolOfferRule.RoleAllowlist => RoleEnvironments.Of(definition).ToolList.Contains(
                descriptor.Name,
                StringComparer.Ordinal),
            ToolOfferRule.SessionAttachmentsWhenRoleAllows => context is not null
                && ToolCatalog.SessionHasAttachments(context),
            ToolOfferRule.ConfigurationWhenRoleAllows =>
                RoleEnvironments.Of(definition).ToolList.Contains(descriptor.Name, StringComparer.Ordinal)
                && configurationGate.IsConfigured(descriptor.Name),
            _ => false
        };
    }

    public static bool IsOffered(
        AgentDefinition definition,
        AgentContext context,
        string toolName,
        IToolConfigurationGate configurationGate) =>
        ToolRegistry.TryGet(toolName, out var descriptor)
        && IsOffered(descriptor, definition, context, configurationGate);
}
