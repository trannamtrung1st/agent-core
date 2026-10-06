using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class WorkspaceStructureToolContractTests
{
    [Fact]
    public void Effects_replay_and_scope_are_explicit()
    {
        foreach(var name in new[] {ToolCatalog.WorkspaceMkdir,ToolCatalog.WorkspaceCopy,ToolCatalog.WorkspaceMove,ToolCatalog.WorkspaceDelete,ToolCatalog.WorkspaceBatch})
        {
            Assert.Equal(ToolResourceScope.Session,ToolRegistry.Get(name).Scope);
            Assert.Equal(name==ToolCatalog.WorkspaceMkdir ? ToolReplaySafety.ReplaySafe : ToolReplaySafety.NonReplayable,ToolCatalog.ReplaySafetyOf(name));
            Assert.Equal(name is ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch ? ToolEffect.Destructive : ToolEffect.Write,ToolCatalog.EffectOf(name));
        }
    }
    [Theory]
    [InlineData("{\"operations\":[{\"op\":\"write\",\"path\":\"a\"}]}")]
    [InlineData("{\"operations\":[{\"op\":\"delete\",\"path\":\"a\",\"recursive\":\"true\"}]}")]
    [InlineData("{\"operations\":[{\"op\":\"mkdir\",\"path\":\"a\",\"owner\":\"other\"}]}")]
    [InlineData("{\"operations\":[{\"op\":\"mkdir\",\"path\":\"a\",\"recursive\":false}]}")]
    [InlineData("{\"operations\":[{\"op\":\"copy\",\"source\":\"a\",\"destination\":\"b\",\"path\":\"c\"}]}")]
    [InlineData("{\"operations\":[{\"op\":\"mkdir\",\"path\":\"a\",\"path\":\"b\"}]}")]
    [InlineData("{\"operations\":[]}")]
    public void Malformed_batch_is_rejected(string value)
    {using var json=JsonDocument.Parse(value);Assert.Throws<AgentCoreException>(()=>WorkspaceStructureArguments.Parse(ToolCatalog.WorkspaceBatch,json.RootElement));}
    [Fact]
    public void Approval_preview_contains_every_exact_operation_and_recursion_without_truncation()
    {
        var operations=Enumerable.Range(0,16).Select(i=>new {op="delete",path="/home/"+new string('x',100)+i,recursive=true}).ToArray();
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new {operations}));
        var preview=ToolApprovalPreview.Build(ToolCatalog.WorkspaceBatch,json.RootElement);
        Assert.Contains("recursive",preview.Details["Exact operations"]);Assert.Equal(json.RootElement.GetRawText(),preview.Details["Exact operations"]);
        Assert.Contains("Earlier changes remain",preview.Details["Failure behavior"]);
    }
    [Fact]
    public async Task Compatibility_approval_keeps_original_relative_operations_and_scratch_scope()
    {
        const string arguments = "{\"path\":\"notes.md\"}";
        using var json = JsonDocument.Parse(arguments);
        var prepared = await ToolActionPreparation.PrepareApprovalAsync(new SessionToolExecutor(),
            new("legacy", ToolCatalog.WorkspaceDelete, arguments), json.RootElement, sessionId: Guid.NewGuid());
        Assert.Null(prepared.ErrorJson);
        Assert.Equal(arguments, prepared.Preparation!.Details!["Exact operations"]);
        Assert.Contains("/workspace/working", prepared.Preparation.Details["Path scope"]);
    }
    [Fact]
    public void Home_uuid_names_do_not_select_an_owner_and_scratch_foreign_ids_remain_forbidden()
    {var session=Guid.NewGuid();var name=Guid.NewGuid().ToString("N");Assert.True(RolePermissions.AllowsLogicalPath("/home/"+name+"/work",session));Assert.False(RolePermissions.AllowsLogicalPath("/workspace/working/"+name+"/work",session));}
    [Theory] [InlineData("*.txt")] [InlineData("[ab].txt")] [InlineData("../work")] [InlineData("dir/../../work")]
    public void Mutation_paths_reject_globs_and_traversal(string path)
    {Assert.Throws<AgentCoreException>(()=>WorkspaceStructuralPaths.Normalize(Guid.NewGuid(),[new("delete",Path:path)]));}
}
