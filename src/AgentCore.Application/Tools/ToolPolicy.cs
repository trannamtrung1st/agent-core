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
        ToolExecutionAdmission? admission = null)
    {
        if (toolName == ToolCatalog.SessionComplete)
            return admission is { TenantId: not null, AgentRunId: not null, AgentRunGeneration: not null, OwnedSessionId: not null,
                SupportsTools: true, Detached: false, TriggerKind: TriggerKind.UserTurn, ConfigurationContinuationTarget: null }
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (WorkflowToolContract.IsTool(toolName))
            return admission is { TenantId: not null, AgentRunId: not null, AgentRunGeneration: not null, SupportsTools: true }
                && RolePermissions.AllowsTool(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (TenantTaskToolContract.IsTool(toolName))
            return admission is { TenantId: not null, AgentRunId: not null, AgentRunGeneration: not null, SupportsTools: true }
                && RolePermissions.AllowsTool(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (TenantContextToolContract.IsTool(toolName))
            return admission is { TenantId: not null, AgentRunId: not null, AgentRunGeneration: not null, SupportsTools: true } && TenantContextToolContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (!ToolRegistry.TryGet(toolName, out var descriptor))
        {
            return ToolPolicyDecision.Deny;
        }
        if (admission?.TenantId is not null && TenantManagementMutationContract.IsTool(toolName))
            return TenantManagementMutationContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantManagementReadContract.IsTool(toolName))
            return TenantManagementReadContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName is ToolCatalog.SkillsLoad or ToolCatalog.CapabilitiesLoad)
            return admission is { AgentRunId: not null, AgentRunGeneration: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantAutomationMutationContract.IsTool(toolName))
            return TenantAutomationMutationContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantAutomationReadContract.IsTool(toolName))
            return TenantAutomationReadContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantExperienceToolContract.IsTool(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && definition.StateIsolationPolicy?.CrossSessionContinuity != false && definition.StateIsolationPolicy?.ExperienceRecall != false
                && (toolName == TenantExperienceToolContract.Source || definition.StateIsolationPolicy?.ExperienceRecord != false) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantMemoryToolContract.IsTool(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && (TenantContinuityToolContract.IsTool(toolName) ? TenantContinuityToolContract.Allows(definition) : TenantMemoryToolContract.Allows(definition, toolName)) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && (toolName == ToolCatalog.ExecutionWait))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && RolePermissions.AllowsTool(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantBackgroundToolContract.IsTool(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && (toolName == ToolCatalog.WorkComplete || RolePermissions.AllowsTool(definition, toolName))
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantWorkspaceMutationContract.IsMutation(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantWorkspaceMutationContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantWorkspaceReadContract.IsRead(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantWorkspaceReadContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName == ToolCatalog.AttachmentsRead)
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantAttachmentReadContract.Allows(definition) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName == ToolCatalog.ArtifactsVerify)
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantArtifactVerificationContract.Allows(definition) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName == ToolCatalog.ArtifactsCreateFromWorkspace)
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantWorkspaceArtifactContract.Allows(definition) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName == ToolCatalog.ArtifactsCreate)
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantArtifactToolContract.Allows(definition) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantCredentialDiscoveryContract.IsTool(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantBrowserInspectionContract.IsInspection(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantBrowserInspectionContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && TenantSandboxInspectionContract.IsInspection(toolName))
            return admission is { AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantSandboxInspectionContract.Allows(definition, toolName) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (admission?.TenantId is not null && toolName == ToolCatalog.AgentStateInspect)
            return admission is { AgentRunId: not null, AgentRunGeneration: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                && TenantRuntimeStateContract.Allows(definition) ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;
        if (descriptor.OfferRule == ToolOfferRule.TenantExecutionAuthority)
            return admission is { TenantId: not null, AgentRunId: not null, AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true }
                ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;

        if (!IsolationAllows(definition, toolName))
            return ToolPolicyDecision.Deny;
        if (toolName == ToolCatalog.AgentStateInspect && admission is not { AgentInstanceId: not null, OwnedSessionId: not null, SupportsTools: true })
            return ToolPolicyDecision.Deny;

        if (admission?.ConfigurationContinuationTarget is { } continuationTarget && (ManagementTools.IsManagement(toolName) || descriptor.Effect != ToolEffect.ReadOnly && toolName != continuationTarget))
            return ToolPolicyDecision.Deny;
        if (ToolCatalog.IsCompletionTool(toolName) && admission?.OwnedSessionId is null) return ToolPolicyDecision.Deny;
        if (ToolResources.IsTriggerWrite(toolName) && admission is not null
            && admission is not { Detached: false, TriggerKind: TriggerKind.UserTurn, OwnedSessionId: not null })
            return ToolPolicyDecision.Deny;
        if (ManagementTools.IsManagement(toolName)) return ToolPolicyDecision.Deny;
        if (TenantAutomationReadContract.IsTool(toolName) || TenantAutomationMutationContract.IsTool(toolName)) return ToolPolicyDecision.Deny;
        if (TenantMemoryToolContract.IsTool(toolName) || TenantExperienceToolContract.IsTool(toolName) || toolName == ToolCatalog.ExperienceRecent) return ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.CredentialAuthority)
            return admission is { AgentInstanceId: not null, SupportsTools: true } ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny;

        if (descriptor.DefinitionAuthorizable && definition.ExecutionPolicy?.Capabilities is not null && !RolePermissions.AllowsTool(definition, toolName)) return ToolPolicyDecision.Deny;

        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
            return ToolPolicyDecision.Deny;

        if (toolName == ToolCatalog.CapabilitiesLoad)
            return definition.ExecutionPolicy?.Capabilities is not null && admission is { SupportsTools: true }
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
            && !DefinitionExecutionPolicies.Of(definition).ToolList.Contains(toolName, StringComparer.Ordinal))
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
                && (definition.SelfManagement?.AutoApplies(ManagementAutoApply.SkillChanges) ?? true))))
            return ToolPolicyDecision.RequireApproval;

        if (descriptor.Effect is ToolEffect.SensitiveWrite or ToolEffect.Destructive || admission?.ConfigurationContinuationTarget is not null && descriptor.Effect != ToolEffect.ReadOnly)
        {
            return ToolPolicyDecision.RequireApproval;
        }

        return ToolPolicyDecision.Allow;
    }

    public static bool IsOffered(
        ToolDescriptor descriptor,
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        if (descriptor.Name == ToolCatalog.SessionComplete)
            return context is { ModelSupportsTools: true, ExecutionContext: not null, DetachedExecution: false }
                && context.CurrentContextTools.Contains(descriptor.Name, StringComparer.Ordinal);
        if (WorkflowToolContract.IsTool(descriptor.Name))
            return context is { ModelSupportsTools: true, ExecutionContext: not null }
                && (descriptor.Name != "workflows.start" || context.OutputContract != AgentCore.Domain.Conversation.AgentRunOutputContract.CompletionReport)
                && context.CurrentContextTools.Contains(descriptor.Name, StringComparer.Ordinal) && RolePermissions.AllowsTool(definition, descriptor.Name);
        if (TenantTaskToolContract.IsTool(descriptor.Name))
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
                && execution.SessionId == context.OwnedSessionId
                && (descriptor.Name != "tasks.delegate" || context.OutputContract != AgentCore.Domain.Conversation.AgentRunOutputContract.CompletionReport)
                && context.CurrentContextTools.Contains(descriptor.Name, StringComparer.Ordinal) && RolePermissions.AllowsTool(definition, descriptor.Name);
        if (GoalToolContract.IsTool(descriptor.Name))
            return context is { ModelSupportsTools: true, ExecutionContext: not null } && context.CurrentContextTools.Contains(descriptor.Name, StringComparer.Ordinal) && AgentCore.Application.Agents.RolePermissions.AllowsTool(definition, descriptor.Name);
        if (TenantContextToolContract.IsTool(descriptor.Name))
            return context is { ModelSupportsTools: true, ExecutionContext: not null } && context.CurrentContextTools.Contains(descriptor.Name, StringComparer.Ordinal) && TenantContextToolContract.Allows(definition, descriptor.Name);
        if (context?.ExecutionContext is { } message && descriptor.Name == ToolCatalog.AppMessageSend)
            return context is { ModelSupportsTools: true, DetachedExecution: false, IntermediateMessagingAllowed: true, ConfigurationContinuationTarget: null,
                    AgentInstanceId: not null, OwnedSessionId: not null }
                && context.Trigger.Kind == TriggerKind.UserTurn && message.SessionId == context.OwnedSessionId
                && ApplicationMessageToolPolicy.AllowsConversation(message, context.OutputContract, context.RunTaskIntent);
        if (context?.ExecutionContext is { } discovery && descriptor.Name == ToolCatalog.CapabilitiesLoad)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && discovery.SessionId == context.OwnedSessionId && definition.ExecutionPolicy?.Capabilities is not null && TenantTaskExecutionContract.Allows(context);
        if (context?.ExecutionContext is not null && TenantManagementMutationContract.IsTool(descriptor.Name))
            return TenantManagementMutationContract.Allowed(definition, context).Contains(descriptor.Name);
        if (context?.ExecutionContext is not null && descriptor.Name == ToolCatalog.SkillsLoad)
            return TenantSkillLoadContract.Allows(context) || TenantCoreProcedureContract.Visible(context).Count > 0;
        if (context?.ExecutionContext is not null && TenantManagementReadContract.IsTool(descriptor.Name))
            return TenantManagementReadContract.Allowed(definition, context).Contains(descriptor.Name);
        if (context?.ExecutionContext is { } authoring && TenantAutomationMutationContract.IsTool(descriptor.Name))
            return authoring.SessionId == context.OwnedSessionId && TenantAutomationMutationContract.Allowed(definition, context).Contains(descriptor.Name);
        if (context?.ExecutionContext is { } automation && TenantAutomationReadContract.IsTool(descriptor.Name))
            return automation.SessionId == context.OwnedSessionId && TenantAutomationReadContract.Allowed(definition, context).Contains(descriptor.Name);
        if (context?.ExecutionContext is { } experience && TenantExperienceToolContract.IsTool(descriptor.Name))
            return experience.SessionId == context.OwnedSessionId && TenantExperienceToolContract.Allowed(definition, context).Contains(descriptor.Name);
        if (context?.ExecutionContext is { } memory && TenantMemoryToolContract.IsTool(descriptor.Name))
            return memory.SessionId == context.OwnedSessionId && TenantTaskExecutionContract.Allows(context) && (TenantContinuityToolContract.IsTool(descriptor.Name) ? TenantContinuityToolContract.Allows(definition) : TenantMemoryToolContract.Allows(definition, descriptor.Name, context));
        if (context?.ExecutionContext is not null && descriptor.Name == ToolCatalog.ExecutionWait)
            return TenantWaitToolContract.IsOffered(definition, context);
        if (context?.ExecutionContext is not null && TenantCredentialDiscoveryContract.IsFamilyTool(descriptor.Name))
            return TenantCredentialDiscoveryContract.Allows(definition, context, descriptor.Name);
        if (context?.ExecutionContext is not null && TenantBackgroundToolContract.IsTool(descriptor.Name))
            return TenantBackgroundToolContract.IsOffered(definition, context, descriptor.Name);
        // Completion reports do not generate protected work. Immediate background work and
        // its approval successors use the converted file and native owners.
        if (context?.ExecutionContext?.Origin == AgentCore.Domain.Conversation.ExecutionOrigin.CompletionReport
            && !IamToolContracts.IsInspection(descriptor.Name)) return false;
        if ((TenantTaskExecutionContract.IsBackground(context) || TenantTaskExecutionContract.IsIndependent(context))
            && !IamToolContracts.IsInspection(descriptor.Name) && !TenantTaskExecutionContract.IsTool(descriptor.Name) && !TenantTaskExecutionContract.IsNativeInspection(descriptor.Name)
            && descriptor.Name != ToolCatalog.RequestAuthorization) return false;
        if (context?.ExecutionContext is { } publicRead && TenantPublicWebReadContract.IsRead(descriptor.Name))
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context) && publicRead.SessionId == context.OwnedSessionId
                && TenantPublicWebReadContract.Allows(definition, descriptor.Name);
        if(context?.ExecutionContext is not null && descriptor.Name==ToolCatalog.HttpRequest)
            return context is { ModelSupportsTools:true,AgentInstanceId:not null,OwnedSessionId:not null } && TenantTaskExecutionContract.Allows(context) && TenantHttpRequestContract.Allows(definition);
        if (context?.ExecutionContext is { } mutation && TenantWorkspaceMutationContract.IsMutation(descriptor.Name))
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && mutation.SessionId == context.OwnedSessionId && TenantWorkspaceMutationContract.Allows(definition, descriptor.Name);
        if (context?.ExecutionContext is { } workspaceExecution && TenantWorkspaceReadContract.IsRead(descriptor.Name))
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && workspaceExecution.SessionId == context.OwnedSessionId && TenantWorkspaceReadContract.Allows(definition, descriptor.Name);
        if (context?.ExecutionContext is { } attachment && descriptor.Name == ToolCatalog.AttachmentsRead)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && attachment.SessionId == context.OwnedSessionId && TenantAttachmentReadContract.Allows(definition) && ToolCatalog.SessionHasAttachments(context);
        if (context?.ExecutionContext is { } verification && descriptor.Name == ToolCatalog.ArtifactsVerify)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && verification.SessionId == context.OwnedSessionId && TenantArtifactVerificationContract.Allows(definition);
        if (context?.ExecutionContext is { } sourceExecution && descriptor.Name == ToolCatalog.ArtifactsCreateFromWorkspace)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && sourceExecution.SessionId == context.OwnedSessionId && TenantWorkspaceArtifactContract.Allows(definition);
        if (context?.ExecutionContext is { } artifactExecution && descriptor.Name == ToolCatalog.ArtifactsCreate)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null }
                && TenantTaskExecutionContract.Allows(context)
                && artifactExecution.SessionId == context.OwnedSessionId && TenantArtifactToolContract.Allows(definition);
        if (context?.ExecutionContext is { } credentialExecution && descriptor.Name == ToolCatalog.CredentialsList)
            return context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, CredentialMetadataAvailable: true }
                && credentialExecution.SessionId == context.OwnedSessionId;
        if (context?.ExecutionContext is not null && TenantBrowserInspectionContract.IsInspection(descriptor.Name))
            return TenantBrowserInspectionContract.IsOffered(definition, context, descriptor.Name);
        if (context?.ExecutionContext is not null && TenantSandboxInspectionContract.IsInspection(descriptor.Name))
            return TenantSandboxInspectionContract.IsOffered(definition, context, descriptor.Name);
        if (context?.ExecutionContext is not null && descriptor.Name == ToolCatalog.AgentStateInspect)
            return TenantRuntimeStateContract.IsOffered(definition, context);
        if (context?.ExecutionContext is not null && descriptor.OfferRule != ToolOfferRule.TenantExecutionAuthority) return false;
        if (descriptor.OfferRule == ToolOfferRule.TenantExecutionAuthority)
            return (descriptor.Name != ToolCatalog.RequestAuthorization || TenantApprovalToolContract.Allows(definition, context))
                && (descriptor.Name != TenantPreferenceToolContract.Name || TenantPreferenceToolContract.Allows(definition))
                && context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
                && execution.SessionId == context.OwnedSessionId;
        if (!IsolationAllows(definition, descriptor.Name)) return false;
        if (descriptor.Name == ToolCatalog.AgentStateInspect && context is not { AgentInstanceId: not null, OwnedSessionId: not null, ModelSupportsTools: true }) return false;
        if (descriptor.Name == ToolCatalog.SandboxExec && (definition.SandboxPolicy is null || definition.ExecutionSandboxProfile is null && !(definition.ExecutionSandboxProfiles?.Any(p => p.AdmittedForExecution != false) == true))) return false;
        if (context?.ConfigurationContinuationTarget is { } continuationTarget && (ManagementTools.IsManagement(descriptor.Name) || descriptor.Effect != ToolEffect.ReadOnly && descriptor.Name != continuationTarget)) return false;
        if (ToolCatalog.IsCompletionTool(descriptor.Name) && context?.OwnedSessionId is null) return false;
        if (ManagementTools.IsManagement(descriptor.Name)) return false;
        if (TenantAutomationReadContract.IsTool(descriptor.Name) || TenantAutomationMutationContract.IsTool(descriptor.Name)) return false;
        if (TenantMemoryToolContract.IsTool(descriptor.Name) || TenantExperienceToolContract.IsTool(descriptor.Name) || descriptor.Name == ToolCatalog.ExperienceRecent) return false;

        if (descriptor.OfferRule == ToolOfferRule.CredentialAuthority)
            return context is { AgentInstanceId: not null, ModelSupportsTools: true, CredentialMetadataAvailable: true };

        if (descriptor.DefinitionAuthorizable && definition.ExecutionPolicy?.Capabilities is not null && !RolePermissions.AllowsTool(definition, descriptor.Name)) return false;

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



        if (descriptor.OfferRule == ToolOfferRule.HarnessAuthority)
            return false;

        if (descriptor.Name == ToolCatalog.CapabilitiesLoad)
            return definition.ExecutionPolicy?.Capabilities is not null && context is { ModelSupportsTools: true }
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
            ToolOfferRule.RoleAllowlist => DefinitionExecutionPolicies.Of(definition).ToolList.Contains(
                descriptor.Name,
                StringComparer.Ordinal),
            ToolOfferRule.SessionAttachmentsWhenRoleAllows => context is not null
                && ToolCatalog.SessionHasAttachments(context),
            ToolOfferRule.ConfigurationWhenRoleAllows =>
                DefinitionExecutionPolicies.Of(definition).ToolList.Contains(descriptor.Name, StringComparer.Ordinal)
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
        return !(tool == ToolCatalog.AgentStateInspect && isolation.AgentStateVisibility == AgentStateVisibility.Off
            || tool is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet && !isolation.CrossSessionContinuity
            || tool is AgentCore.Application.Tools.TenantExperienceToolContract.Source or ToolCatalog.ExperienceRecent && !isolation.ExperienceRecall
            || tool == AgentCore.Application.Tools.TenantExperienceToolContract.Record && !isolation.ExperienceRecord
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
