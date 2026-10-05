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
        ToolApprovalGrant? grant = null,
        ToolExecutionAdmission? admission = null)
    {
        if (!ToolRegistry.TryGet(toolName, out var descriptor))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.ExperienceAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true } ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
        {
            if (admission is not { AgentInstanceId: not null, SupportsTools: true }
                || !((!admission.Detached && admission.TriggerKind == TriggerKind.UserTurn)
                    || (admission.Detached && admission.TriggerKind == TriggerKind.ThoughtActivation))
                || !HarnessChatTools.Allows(toolName, admission.Harness)) return ToolPolicyDecision.Deny;
            if (admission.TriggerKind == TriggerKind.ThoughtActivation && toolName is "harness.tool.select" or "harness.tool.configure")
                return ToolPolicyDecision.Deny;
            return HarnessChatTools.NeedsApproval(toolName, admission.Harness!) && grant is null
                ? ToolPolicyDecision.RequireApproval : ToolPolicyDecision.Allow;
        }

        if (!RolePermissions.AllowsTool(definition, toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (ToolCatalog.IsBrowserTool(toolName) && !BrowserAdmissionAllows(admission))
        {
            return ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.OccurrenceCapability)
        {
            return OccurrenceCompletion(admission)
                ? ToolPolicyDecision.Allow
                : ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.CurrentExecutionCapability)
        {
            if (admission is null
                || admission.Detached
                || admission.TriggerKind != TriggerKind.UserTurn
                || ToolResources.IsOccurrence(admission.TriggerKind))
            {
                return ToolPolicyDecision.Deny;
            }

            if (string.Equals(toolName, ToolCatalog.SkillsLoad, StringComparison.Ordinal)
                && definition.SkillList.Count == 0)
            {
                return ToolPolicyDecision.Deny;
            }

            if (string.Equals(toolName, ToolCatalog.AppMessageSend, StringComparison.Ordinal)
                && admission is not { IntermediateMessagingAllowed: true })
            {
                return ToolPolicyDecision.Deny;
            }

            return ToolPolicyDecision.Allow;
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

        if (admission?.Detached == true
            && descriptor.Scope == ToolResourceScope.Session
            && !(ToolCatalog.IsBrowserTool(toolName) && UnattendedBrowser(admission))
            && !(HarnessChatTools.IsHarness(toolName) && admission.TriggerKind == TriggerKind.ThoughtActivation))
        {
            return ToolPolicyDecision.Deny;
        }

        if (admission is not null
            && ToolResources.IsOccurrence(admission.TriggerKind)
            && ToolResources.IsTriggerWrite(toolName))
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

        if (context?.DetachedExecution == true
            && descriptor.Scope == ToolResourceScope.Session
            && !(ToolCatalog.IsBrowserTool(descriptor.Name) && UnattendedBrowser(context))
            && !(HarnessChatTools.IsHarness(descriptor.Name) && context.Trigger.Kind == TriggerKind.ThoughtActivation))
        {
            return false;
        }

        if (context is not null
            && ToolResources.IsOccurrence(context.Trigger.Kind)
            && ToolResources.IsTriggerWrite(descriptor.Name))
        {
            return false;
        }

        if (descriptor.OfferRule == ToolOfferRule.ExperienceAuthority)
            return context is { ModelSupportsTools: true } && !string.IsNullOrEmpty(context.ExperienceContext);
        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
            return context is { ModelSupportsTools: true }
                && ((!context.DetachedExecution && context.Trigger.Kind == TriggerKind.UserTurn)
                    || (context.DetachedExecution && context.Trigger.Kind == TriggerKind.ThoughtActivation))
                && !(context.Trigger.Kind == TriggerKind.ThoughtActivation && descriptor.Name is "harness.tool.select" or "harness.tool.configure")
                && HarnessChatTools.Allows(descriptor.Name, context.Harness);

        if (!RolePermissions.AllowsTool(definition, descriptor.Name))
        {
            return false;
        }

        if (ToolCatalog.IsBrowserTool(descriptor.Name) && !BrowserOfferAllows(context))
        {
            return false;
        }

        if (string.Equals(descriptor.Name, ToolCatalog.BrowserCapture, StringComparison.Ordinal)
            && context?.ModelSupportsVision != true)
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
            ToolOfferRule.CurrentExecutionCapability => context is not null
                && context.Trigger.Kind == TriggerKind.UserTurn
                && !context.DetachedExecution
                && !ToolResources.IsOccurrence(context.Trigger.Kind)
                && (descriptor.Name != ToolCatalog.SkillsLoad || definition.SkillList.Count > 0)
                && (descriptor.Name != ToolCatalog.AppMessageSend || context.IntermediateMessagingAllowed),
            ToolOfferRule.OccurrenceCapability => OccurrenceCompletion(context),
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

    private static bool BrowserAdmissionAllows(ToolExecutionAdmission? admission) =>
        admission is { Detached: false, TriggerKind: TriggerKind.UserTurn }
        || UnattendedBrowser(admission)
        || LiveTrustedOccurrence(admission);

    private static bool BrowserOfferAllows(AgentContext? context) =>
        context is { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn }
        || UnattendedBrowser(context)
        || LiveTrustedOccurrence(context);

    private static bool UnattendedBrowser(ToolExecutionAdmission? admission) =>
        admission is { Detached: true, TrustedConnection: true }
        && ToolResources.IsOccurrence(admission.TriggerKind)
        && admission.AgentInstanceId is Guid agentInstanceId
        && agentInstanceId != Guid.Empty;

    private static bool UnattendedBrowser(AgentContext? context) =>
        context is { DetachedExecution: true, TrustedConnection: true }
        && ToolResources.IsOccurrence(context.Trigger.Kind);

    private static bool LiveTrustedOccurrence(ToolExecutionAdmission? admission) =>
        admission is { Detached: false, TrustedConnection: true }
        && ToolResources.IsOccurrence(admission.TriggerKind)
        && admission.AgentInstanceId is Guid agentInstanceId
        && agentInstanceId != Guid.Empty;

    private static bool LiveTrustedOccurrence(AgentContext? context) =>
        context is { DetachedExecution: false, TrustedConnection: true }
        && ToolResources.IsOccurrence(context.Trigger.Kind);

    private static bool OccurrenceCompletion(ToolExecutionAdmission? admission) =>
        admission is { Detached: true } && ToolResources.IsOccurrence(admission.TriggerKind);

    private static bool OccurrenceCompletion(AgentContext? context) =>
        context is { DetachedExecution: true } && ToolResources.IsOccurrence(context.Trigger.Kind);
}
