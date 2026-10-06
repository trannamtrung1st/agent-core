using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>Existing move authority stays file-only until the definition opts into structural tools.</summary>
public static class WorkspaceFilesystemPolicy
{
    public static bool AllowsStructure(AgentDefinition definition) =>
        WorkspaceSemantics.IsV2(definition) || RoleEnvironments.Of(definition).ToolList.Any(name => name is ToolCatalog.WorkspaceMkdir
            or ToolCatalog.WorkspaceCopy or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch);

    public static ModelToolDefinition ForDefinition(AgentDefinition definition, ToolDescriptor descriptor)
    {
        if (WorkspaceSemantics.IsV2(definition))
        {
            var tool = descriptor.ModelDefinition;
            var description = descriptor.Name switch
            {
                ToolCatalog.WorkspaceRead => "Read a file in durable /home or temporary /working. Relative paths resolve from Session cwd (initially /home). Returns home revision/hash for safe edits. /agent and /attachments remain read-only.",
                ToolCatalog.WorkspaceList => "List the current directory, or an explicit /home or /working directory. Returns bounded metadata and the durable whole-tree token for structural changes. Relative paths resolve from Session cwd.",
                ToolCatalog.WorkspaceWrite => "Create a UTF-8 file directly in /home or /working. Relative paths use Session cwd. Existing /home files require current expectedRevision or expectedSha256; stale writes fail.",
                ToolCatalog.WorkspacePatch => "Apply exact-once UTF-8 edits in /home or /working. Requires current expectedSha256; stale or ambiguous edits fail. Relative paths use Session cwd.",
                ToolCatalog.WorkspaceSearch => "Search filenames and bounded UTF-8 text from Session cwd or an explicit /home or /working directory. Skips binary contents. /agent and /attachments remain read-only.",
                ToolCatalog.ArtifactsCreateFromWorkspace => "Publish a fresh Session-owned downloadable Artifact from /home or /working. Relative paths resolve from Session cwd. This copies exact file bytes; it does not change workspace persistence.",
                ToolCatalog.WorkspaceMove => "Move or rename a file or complete tree within /home or within /working. Relative paths use Session cwd. Home requires expectedTreeSha256; destination must not exist. Cross-root moves and moving cwd/ancestors are forbidden.",
                ToolCatalog.WorkspaceCopy => "Copy exact bytes or a whole tree with empty folders, within or across /home and /working. Creates parents; never merges or overwrites by default. Same-scope /home copy requires expectedTreeSha256. Cross-scope copy to an existing durable file requires current destination expectedRevision or expectedSha256; directory/scratch overwrites are forbidden.",
                ToolCatalog.SandboxRun => "Run a bounded offline sandbox command (echo, true, cat of /working files). Sandbox relative paths resolve from /working, independently of workspace.cwd. Copy /home sources to /working first.",
                _ => tool.Description
            };
            var schema = tool.ParametersJson;
            if (descriptor.Name == ToolCatalog.WorkspaceWrite)
                schema = """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string"},"content":{"type":"string"},"expectedRevision":{"type":"integer","minimum":1},"expectedSha256":{"type":"string"}},"required":["path","content"]}""";
            if (descriptor.Name == ToolCatalog.WorkspaceCopy)
                schema = """{"type":"object","additionalProperties":false,"properties":{"source":{"type":"string"},"destination":{"type":"string"},"expectedTreeSha256":{"type":"string"},"expectedRevision":{"type":"integer","minimum":1},"expectedSha256":{"type":"string"}},"required":["source","destination"]}""";
            return new(tool.Name, description, schema);
        }
        return descriptor.Name == ToolCatalog.WorkspaceMove && !AllowsStructure(definition)
            ? new ModelToolDefinition(ToolCatalog.WorkspaceMove,
                "Move a workspace file to another relative path. Does not overwrite an existing destination.",
                """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"]}""")
            : descriptor.ModelDefinition;
    }
}
