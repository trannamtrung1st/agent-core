using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

// Unowned execution journeys were mapped to current tenant workspace/read/effect
// and Artifact publication/verification owners. Keep the pure registry contract.
public sealed class WorkspaceArtifactToolTests
{
    [Fact]
    public void Canonical_move_schema_uses_current_paths_without_a_tree_token()
    {
        using var schema = JsonDocument.Parse(ToolRegistry.Get(ToolCatalog.WorkspaceMove).ModelDefinition.ParametersJson);
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("expectedTreeSha256", out _));
    }
}
