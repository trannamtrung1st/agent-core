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
        if (definition.Environment?.Capabilities is null) return eligible.Where(t => !ManagementTools.IsManagement(t.Name) || ToolRegistry.Get(t.Name).OfferRule == ToolOfferRule.TenantExecutionAuthority || (context?.LoadedCapabilityIds ?? []).Contains(t.Name) || (context?.PinnedSkillCatalog ?? []).Where(s => (context?.ActiveSkillKeys ?? []).Contains(s.Key)).SelectMany(s => s.RequiredCapabilities).Contains(t.Name)).Select(t => ForContext(t, context)).ToArray();
        var always = definition.Environment.Projection?.AlwaysCapabilities ?? [];
        var skill = (context?.PinnedSkillCatalog ?? []).Where(s => context?.ActiveSkillKeys?.Contains(s.Key, StringComparer.Ordinal) == true)
            .SelectMany(s => s.RequiredCapabilities ?? []).ToHashSet(StringComparer.Ordinal);
        var loaded = context?.LoadedCapabilityIds ?? [];
        return eligible.Where(t => (!ManagementTools.IsManagement(t.Name) || ToolRegistry.Get(t.Name).OfferRule == ToolOfferRule.TenantExecutionAuthority || loaded.Contains(t.Name, StringComparer.Ordinal) || skill.Contains(t.Name))
            && (t.Name == ToolCatalog.CapabilitiesLoad
            || BrowserBootstrap.Contains(t.Name)
            || always.Contains(t.Name, StringComparer.Ordinal) || skill.Contains(t.Name)
            || loaded.Contains(t.Name, StringComparer.Ordinal)
            || !ToolRegistry.Get(t.Name).Discoverable || context?.ExecutionContext is not null && (TenantWorkspaceMutationContract.IsMutation(t.Name) || TenantWorkspaceReadContract.IsRead(t.Name) || t.Name is ToolCatalog.ArtifactsVerify or ToolCatalog.ArtifactsCreateFromWorkspace || TenantSandboxInspectionContract.IsInspection(t.Name) || TenantBrowserInspectionContract.IsInspection(t.Name))))
            .Select(t => ForContext(t, context))
            .DistinctBy(t => t.Name).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }
    private static ModelToolDefinition ForContext(ModelToolDefinition tool, AgentContext? context) => context?.ExecutionContext is null ? tool
        : TenantWorkspaceMutationContract.IsMutation(tool.Name) ? TenantWorkspaceMutationContract.ForModel(tool)
        : TenantWorkspaceReadContract.IsRead(tool.Name) ? TenantWorkspaceReadContract.ForModel(tool)
        : tool.Name == ToolCatalog.AttachmentsRead ? TenantAttachmentReadContract.ForModel(tool)
        : tool.Name == ToolCatalog.ArtifactsVerify ? TenantArtifactVerificationContract.ForModel()
        : tool.Name == ToolCatalog.ArtifactsCreateFromWorkspace ? TenantWorkspaceArtifactContract.ForModel()
        : tool.Name == ToolCatalog.ArtifactsCreate ? TenantArtifactToolContract.ForModel()
        : TenantSandboxInspectionContract.IsInspection(tool.Name) ? TenantSandboxInspectionContract.ForModel(tool)
        : TenantBrowserInspectionContract.IsInspection(tool.Name) ? TenantBrowserInspectionContract.ForModel(tool) : tool;

}
