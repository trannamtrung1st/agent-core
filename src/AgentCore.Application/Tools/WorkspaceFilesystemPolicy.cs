using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>Existing move authority stays file-only until the definition opts into structural tools.</summary>
public static class WorkspaceFilesystemPolicy
{
    public static bool AllowsStructure(AgentDefinition definition) =>
        RoleEnvironments.Of(definition).ToolList.Any(name => name is ToolCatalog.WorkspaceMkdir
            or ToolCatalog.WorkspaceCopy or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch);

    public static ModelToolDefinition ForDefinition(AgentDefinition definition, ToolDescriptor descriptor) =>
        descriptor.Name == ToolCatalog.WorkspaceMove && !AllowsStructure(definition)
            ? new ModelToolDefinition(ToolCatalog.WorkspaceMove,
                "Move a workspace file to another relative path. Does not overwrite an existing destination.",
                """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"]}""")
            : descriptor.ModelDefinition;
}
