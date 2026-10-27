using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolProjectionService
{
    private static readonly HashSet<string> BrowserBootstrap = new(StringComparer.Ordinal)
    { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserWait, ToolCatalog.BrowserClose };

    public static IReadOnlyList<ModelToolDefinition> Project(AgentDefinition definition, AgentContext? context, IToolConfigurationGate gate)
    {
        var eligible = ToolCatalog.Eligible(definition, context, gate);
        if (definition.ExecutionPolicy?.Capabilities is null) return eligible.Where(t => !ManagementTools.IsManagement(t.Name) || ToolRegistry.Get(t.Name).OfferRule == ToolOfferRule.TenantExecutionAuthority || (context?.LoadedCapabilityIds ?? []).Contains(t.Name) || (context?.PinnedSkillCatalog ?? []).Where(s => (context?.ActiveSkillKeys ?? []).Contains(s.Key)).SelectMany(s => s.RequiredCapabilities).Contains(t.Name)).Select(t => ForContext(t, context)).ToArray();
        var always = definition.ExecutionPolicy.Projection?.AlwaysCapabilities ?? [];
        var skill = (context?.PinnedSkillCatalog ?? []).Where(s => context?.ActiveSkillKeys?.Contains(s.Key, StringComparer.Ordinal) == true)
            .SelectMany(s => s.RequiredCapabilities ?? []).ToHashSet(StringComparer.Ordinal);
        var loaded = context?.LoadedCapabilityIds ?? [];
        return eligible.Where(t => (!ManagementTools.IsManagement(t.Name) || ToolRegistry.Get(t.Name).OfferRule == ToolOfferRule.TenantExecutionAuthority || loaded.Contains(t.Name, StringComparer.Ordinal) || skill.Contains(t.Name))
            && (t.Name == ToolCatalog.CapabilitiesLoad
            || BrowserBootstrap.Contains(t.Name)
            || always.Contains(t.Name, StringComparer.Ordinal) || skill.Contains(t.Name)
            || loaded.Contains(t.Name, StringComparer.Ordinal)
            || !ToolRegistry.Get(t.Name).Discoverable || context?.ExecutionContext is not null && (GoalToolContract.IsTool(t.Name) || TenantContextToolContract.IsTool(t.Name) || TenantExperienceToolContract.IsTool(t.Name) || TenantMemoryToolContract.IsTool(t.Name) || TenantCredentialDiscoveryContract.IsFamilyTool(t.Name) || TenantPublicWebReadContract.IsRead(t.Name) || TenantWorkspaceMutationContract.IsMutation(t.Name) || TenantWorkspaceReadContract.IsRead(t.Name) || t.Name is ToolCatalog.ArtifactsVerify or ToolCatalog.ArtifactsCreateFromWorkspace || TenantSandboxInspectionContract.IsInspection(t.Name) || TenantBrowserInspectionContract.IsInspection(t.Name) || t.Name == ToolCatalog.AgentStateInspect)))
            .Select(t => ForContext(t, context))
            .DistinctBy(t => t.Name).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }
    private static ModelToolDefinition ForContext(ModelToolDefinition tool, AgentContext? context) => context?.ExecutionContext is null ? tool
        : TenantManagementMutationContract.IsTool(tool.Name) ? TenantManagementMutationContract.ForModel(tool)
        : tool.Name == ToolCatalog.SkillsLoad ? TenantSkillLoadContract.ForModel(tool, context)
        : TenantManagementReadContract.IsTool(tool.Name) ? TenantManagementReadContract.ForModel(tool)
        : TenantExperienceToolContract.IsTool(tool.Name) ? TenantExperienceToolContract.ForModel(tool)
        : TenantContinuityToolContract.IsTool(tool.Name) ? TenantContinuityToolContract.ForModel(tool, context.CurrentContinuitySettings?.ExperienceEnabled == true && context.Definition.StateIsolationPolicy?.ExperienceRecall != false)
        : TenantMemoryToolContract.IsTool(tool.Name) ? TenantMemoryToolContract.ForModel(tool)
        : TenantCredentialDiscoveryContract.IsFamilyTool(tool.Name) ? TenantCredentialDiscoveryContract.Descriptor(tool.Name)
        : TenantPublicWebReadContract.IsRead(tool.Name) ? TenantPublicWebReadContract.ForModel(tool)
        : tool.Name==ToolCatalog.HttpRequest ? TenantHttpRequestContract.ForModel(tool)
        : TenantWorkspaceMutationContract.IsMutation(tool.Name) ? TenantWorkspaceMutationContract.ForModel(tool)
        : TenantWorkspaceReadContract.IsRead(tool.Name) ? TenantWorkspaceReadContract.ForModel(tool)
        : tool.Name == ToolCatalog.AttachmentsRead ? TenantAttachmentReadContract.ForModel(tool)
        : tool.Name == ToolCatalog.ArtifactsVerify ? TenantArtifactVerificationContract.ForModel()
        : tool.Name == ToolCatalog.ArtifactsCreateFromWorkspace ? TenantWorkspaceArtifactContract.ForModel()
        : tool.Name == ToolCatalog.ArtifactsCreate ? TenantArtifactToolContract.ForModel()
        : TenantSandboxInspectionContract.IsInspection(tool.Name) ? TenantSandboxInspectionContract.ForModel(tool)
        : TenantBrowserInspectionContract.IsInspection(tool.Name) ? TenantBrowserInspectionContract.ForModel(tool) : tool;

}
