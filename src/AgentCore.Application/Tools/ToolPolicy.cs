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

        if (!IsolationAllows(definition, toolName))
            return ToolPolicyDecision.Deny;

        if (admission?.ConfigurationContinuationTarget is { } continuationTarget && (ManagementTools.IsManagement(toolName) || descriptor.Effect != ToolEffect.ReadOnly && toolName != continuationTarget))
            return ToolPolicyDecision.Deny;
        if (ToolCatalog.IsCompletionTool(toolName) && admission?.OwnedSessionId is null) return ToolPolicyDecision.Deny;
        if (toolName == ToolCatalog.BackgroundStart && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn, OwnedSessionId: not null })
            return ToolPolicyDecision.Deny;
        if (ManagementTools.IsManagement(toolName) && (admission is not { AgentInstanceId: not null, SupportsTools: true }
            || !ManagementTools.Allows(toolName, definition, admission.Harness)
            || !HarnessChatTools.IsHarness(toolName) && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn }))
            return ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.CredentialAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true } ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;

        if (descriptor.DefinitionAuthorizable && definition.Environment?.Capabilities is not null && !RolePermissions.AllowsTool(definition, toolName)) return ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.IdentityMaintenanceAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true }
                && (admission is { Detached: false, TriggerKind: TriggerKind.UserTurn }
                    || admission.Detached && ToolResources.IsOccurrence(admission.TriggerKind))
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.ContinuityAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true } ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (descriptor.OfferRule == ToolOfferRule.ExperienceAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true } ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
        {
            if (admission is not { AgentInstanceId: not null, SupportsTools: true }
                || !((!admission.Detached && admission.TriggerKind == TriggerKind.UserTurn)
                    || (admission.Detached && ToolResources.IsOccurrence(admission.TriggerKind)))
                || !HarnessChatTools.Allows(toolName, admission.Harness)) return ToolPolicyDecision.Deny;
            if (ToolResources.IsOccurrence(admission.TriggerKind) && toolName is "harness.tool.select" or "harness.tool.configure")
                return ToolPolicyDecision.Deny;
            return HarnessChatTools.NeedsApproval(toolName, admission.Harness!) && grant is null
                ? ToolPolicyDecision.RequireApproval : ToolPolicyDecision.Allow;
        }

        if (toolName == ToolCatalog.CapabilitiesLoad)
            return definition.Environment?.Capabilities is not null && admission is { SupportsTools: true }
                && (admission is { Detached: false, TriggerKind: TriggerKind.UserTurn }
                    || admission.AgentInstanceId is not null && ToolResources.IsOccurrence(admission.TriggerKind))
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (descriptor.DefinitionAuthorizable && !RolePermissions.AllowsTool(definition, toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (ToolCatalog.IsBrowserTool(toolName) && !BrowserAdmissionAllows(admission))
        {
            return ToolPolicyDecision.Deny;
        }

        if (BrowserToolCatalog.TryGet(toolName, out var browserFeature)
            && browserFeature.Feature is BrowserFeature.FillCredential or BrowserFeature.Geolocation
            && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn })
            return ToolPolicyDecision.Deny;
        if (toolName == ToolCatalog.BrowserVisionMouse && admission?.SupportsVision != true)
            return ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.OccurrenceCapability)
        {
            return OccurrenceCompletion(admission)
                ? ToolPolicyDecision.Allow
                : ToolPolicyDecision.Deny;
        }

        if (descriptor.OfferRule == ToolOfferRule.CurrentExecutionCapability)
        {
            if (toolName == ToolCatalog.SkillsLoad) return admission is { SupportsTools: true, AgentInstanceId: not null } &&
                (admission.TriggerKind == TriggerKind.UserTurn || ToolResources.IsOccurrence(admission.TriggerKind)) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
            if (admission is null
                || admission.Detached
                || admission.TriggerKind != TriggerKind.UserTurn
                || ToolResources.IsOccurrence(admission.TriggerKind))
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
            && !configurationGate.IsExecutionConfigured(toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (admission?.Detached == true
            && admission.OwnedSessionId is not { }
            && descriptor.Scope == ToolResourceScope.Session
            && !(ToolCatalog.IsBrowserTool(toolName) && UnattendedBrowser(admission))
            && !(HarnessChatTools.IsHarness(toolName) && ToolResources.IsOccurrence(admission.TriggerKind)))
        {
            return ToolPolicyDecision.Deny;
        }

        if (admission is not null
            && ToolResources.IsOccurrence(admission.TriggerKind)
            && ToolResources.IsTriggerWrite(toolName))
        {
            return ToolPolicyDecision.Deny;
        }

        if (InstanceSkillTools.IsManagement(toolName) && descriptor.Effect != ToolEffect.ReadOnly
            && (toolName is "skills.delete" or "skills.customize" || !(admission!.Harness!.Policy.AutoApplies(ManagementAutoApply.SkillChanges)
                && (definition.SelfManagement?.AutoApplies(ManagementAutoApply.SkillChanges) ?? true))) && grant is null)
            return ToolPolicyDecision.RequireApproval;

        if (descriptor.Effect is ToolEffect.SensitiveWrite or ToolEffect.Destructive || admission?.ConfigurationContinuationTarget is not null && descriptor.Effect != ToolEffect.ReadOnly)
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
        if (!IsolationAllows(definition, descriptor.Name)) return false;
        if (descriptor.Name == ToolCatalog.SandboxExec && (definition.SandboxPolicy is null || definition.ExecutionSandboxProfile is null && !(definition.ExecutionSandboxProfiles?.Any(p => p.AdmittedForExecution != false) == true))) return false;
        if (context?.ConfigurationContinuationTarget is { } continuationTarget && (ManagementTools.IsManagement(descriptor.Name) || descriptor.Effect != ToolEffect.ReadOnly && descriptor.Name != continuationTarget)) return false;
        if (descriptor.Name == ToolCatalog.BackgroundAcknowledge && context?.HasBackgroundClaim != true) return false;
        if (ToolCatalog.IsCompletionTool(descriptor.Name) && context?.OwnedSessionId is null) return false;
        if (descriptor.Name == ToolCatalog.BackgroundStart && context is not { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn, OwnedSessionId: not null })
            return false;
        if (ManagementTools.IsManagement(descriptor.Name) && (context is not { AgentInstanceId: not null, ModelSupportsTools: true }
            || !ManagementTools.Allows(descriptor.Name, definition, context.Harness)
            || !HarnessChatTools.IsHarness(descriptor.Name) && context is not { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn }))
            return false;

        if (descriptor.OfferRule == ToolOfferRule.CredentialAuthority)
            return context is { AgentInstanceId: not null, ModelSupportsTools: true, CredentialMetadataAvailable: true };

        if (descriptor.DefinitionAuthorizable && definition.Environment?.Capabilities is not null && !RolePermissions.AllowsTool(definition, descriptor.Name)) return false;

        if (context is not null && !context.ModelSupportsTools)
        {
            return false;
        }

        if (context?.DetachedExecution == true
            && context.OwnedSessionId is not { }
            && descriptor.Scope == ToolResourceScope.Session
            && !(ToolCatalog.IsBrowserTool(descriptor.Name) && UnattendedBrowser(context))
            && !(HarnessChatTools.IsHarness(descriptor.Name) && ToolResources.IsOccurrence(context.Trigger.Kind)))
        {
            return false;
        }

        if (context is not null
            && ToolResources.IsOccurrence(context.Trigger.Kind)
            && ToolResources.IsTriggerWrite(descriptor.Name))
        {
            return false;
        }



        if (descriptor.OfferRule == ToolOfferRule.IdentityMaintenanceAuthority)
            return context is { ModelSupportsTools: true } && !string.IsNullOrEmpty(context.ContinuityContext)
                && (context is { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn }
                    || context is { DetachedExecution: true, AllowAgentConsolidation: true } && ToolResources.IsOccurrence(context.Trigger.Kind)
                        && descriptor.Name != ToolCatalog.MemoryForget);

        if (descriptor.OfferRule == ToolOfferRule.ContinuityAuthority)
            return context is { ModelSupportsTools: true } && !string.IsNullOrEmpty(context.ContinuityContext);
        if (descriptor.OfferRule == ToolOfferRule.ExperienceAuthority)
            return context is { AgentInstanceId: not null, ModelSupportsTools: true };
        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
            return context is { ModelSupportsTools: true }
                && ((!context.DetachedExecution && context.Trigger.Kind == TriggerKind.UserTurn)
                    || (context.DetachedExecution && ToolResources.IsOccurrence(context.Trigger.Kind)))
                && !(ToolResources.IsOccurrence(context.Trigger.Kind) && descriptor.Name is "harness.tool.select" or "harness.tool.configure")
                && HarnessChatTools.Allows(descriptor.Name, context.Harness);

        if (descriptor.Name == ToolCatalog.CapabilitiesLoad)
            return definition.Environment?.Capabilities is not null && context is { ModelSupportsTools: true }
                && (context is { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn }
                    || context.AgentInstanceId is not null && ToolResources.IsOccurrence(context.Trigger.Kind));

        if (descriptor.Name == ToolCatalog.WorkspaceCwd && context?.AgentWorkspaceAvailable != true)
            return false;

        if (descriptor.DefinitionAuthorizable && !RolePermissions.AllowsTool(definition, descriptor.Name))
        {
            return false;
        }

        if (ToolCatalog.IsBrowserTool(descriptor.Name) && !BrowserOfferAllows(context))
        {
            return false;
        }

        if (BrowserToolCatalog.TryGet(descriptor.Name, out var browserFeature)
            && browserFeature.Feature is BrowserFeature.FillCredential or BrowserFeature.Geolocation
            && context is not { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn })
        {
            return false;
        }

        if (string.Equals(descriptor.Name, ToolCatalog.BrowserVisionMouse, StringComparison.Ordinal)
            && context?.ModelSupportsVision != true)
        {
            return false;
        }

        if (descriptor.Name == ToolCatalog.SkillsLoad) return context is { AgentInstanceId: not null, ModelSupportsTools: true } && (context.PinnedSkillCatalog?.Count ?? 0) > 0
            && (context.Trigger.Kind == TriggerKind.UserTurn || ToolResources.IsOccurrence(context.Trigger.Kind));
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
                && (descriptor.Name != ToolCatalog.SkillsLoad || (context.PinnedSkillCatalog?.Count ?? 0) > 0)
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

    private static bool IsolationAllows(AgentDefinition definition, string tool)
    {
        var isolation = definition.StateIsolationPolicy ?? StateIsolationPolicy.Default;
        return !(tool is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet && !isolation.CrossSessionContinuity
            || tool is AgentCore.Application.Experience.ExperienceService.SourceTool or ToolCatalog.ExperienceRecent && !isolation.ExperienceRecall
            || tool == AgentCore.Application.Experience.ExperienceService.RecordTool && !isolation.ExperienceRecord
            || ToolCatalog.IsIdentityMaintenance(tool) && !isolation.CrossSessionContinuity);
    }

    private static bool BrowserAdmissionAllows(ToolExecutionAdmission? admission) =>
        admission is { Detached: false, TriggerKind: TriggerKind.UserTurn }
        || UnattendedBrowser(admission)
        || LiveOccurrence(admission);

    private static bool BrowserOfferAllows(AgentContext? context) =>
        context is { DetachedExecution: false, Trigger.Kind: TriggerKind.UserTurn }
        || UnattendedBrowser(context)
        || LiveOccurrence(context);

    private static bool UnattendedBrowser(ToolExecutionAdmission? admission) =>
        admission is { Detached: true }
        && ToolResources.IsOccurrence(admission.TriggerKind)
        && admission.AgentInstanceId is Guid agentInstanceId
        && agentInstanceId != Guid.Empty;

    private static bool UnattendedBrowser(AgentContext? context) =>
        context is { DetachedExecution: true }
        && context.AgentInstanceId is Guid id && id != Guid.Empty && ToolResources.IsOccurrence(context.Trigger.Kind);

    private static bool LiveOccurrence(ToolExecutionAdmission? admission) =>
        admission is { Detached: false }
        && ToolResources.IsOccurrence(admission.TriggerKind)
        && admission.AgentInstanceId is Guid agentInstanceId
        && agentInstanceId != Guid.Empty;

    private static bool LiveOccurrence(AgentContext? context) =>
        context is { DetachedExecution: false }
        && context.AgentInstanceId is Guid id && id != Guid.Empty && ToolResources.IsOccurrence(context.Trigger.Kind);

    private static bool OccurrenceCompletion(ToolExecutionAdmission? admission) =>
        admission is { Detached: true } && ToolResources.IsOccurrence(admission.TriggerKind);

    private static bool OccurrenceCompletion(AgentContext? context) =>
        context is { DetachedExecution: true } && ToolResources.IsOccurrence(context.Trigger.Kind);
}
