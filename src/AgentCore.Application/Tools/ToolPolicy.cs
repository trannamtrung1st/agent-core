using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolPolicy
{
    public static ToolPolicyDecision EvaluateExecution(AgentDefinition definition, string toolName)
    {
        if (!ToolRegistry.TryGet(toolName, out var descriptor))
        {
            return ToolPolicyDecision.Deny;
        }

        if (!RolePermissions.AllowsTool(definition, toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.RoleAllowlist
            && !RoleEnvironments.Of(definition).ToolList.Contains(toolName, StringComparer.Ordinal))
        {
            return ToolPolicyDecision.Deny;
        }

        return ToolPolicyDecision.Allow;
    }

    public static bool IsOffered(ToolDescriptor descriptor, AgentDefinition definition, AgentContext? context)
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
            _ => false
        };
    }

    public static bool IsOffered(AgentDefinition definition, AgentContext context, string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) && IsOffered(descriptor, definition, context);
}
