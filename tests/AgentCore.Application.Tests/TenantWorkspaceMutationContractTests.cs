using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceMutationContractTests
{
    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("null")]
    [InlineData("{\"path\":\"/working/a\",\"content\":\"x\",\"tenantId\":\"other\"}")]
    [InlineData("{\"path\":\"/working/a\",\"path\":\"/home/a\",\"content\":\"x\"}")]
    [InlineData("{\"path\":\"relative.txt\",\"content\":\"x\"}")]
    [InlineData("{\"path\":\"/working\",\"content\":\"x\"}")]
    [InlineData("{\"path\":\"/working/../escape\",\"content\":\"x\"}")]
    public void Write_rejects_ambiguous_paths_and_caller_authority(string json) =>
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(Guid.NewGuid(), ToolCatalog.WorkspaceWrite, json));

    [Fact]
    public void Copy_derives_independent_source_and_destination_scopes_and_stable_parameters()
    {
        var session = Guid.NewGuid();
        var args = TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceCopy, """{"destination":"/working/copy.txt","source":"/home/source.txt"}""");
        Assert.Equal(new[] { ("/home/source.txt", "workspaces.read"), ("/working/copy.txt", "workspaces.write") }, args.Targets);
        Assert.Equal(args.ParametersHash, TenantWorkspaceMutationContract.Parse(session, args.Tool, args.CanonicalArguments).ParametersHash);
        Assert.NotEqual(TenantWorkspaceMutationContract.ActionHash(session, args.ParametersHash), TenantWorkspaceMutationContract.ActionHash(Guid.NewGuid(), args.ParametersHash));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceMove, """{"source":"/home/source.txt","destination":"/working/copy.txt"}"""));
    }

    [Fact]
    public void Batch_canonical_roundtrip_preserves_order_and_delete_requirements_without_synthetic_false_fields()
    {
        var session = Guid.NewGuid();
        var args = TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceBatch,
            """{"operations":[{"op":"mkdir","path":"/working/folder"},{"op":"move","source":"/working/a","destination":"/working/folder/a"},{"op":"delete","path":"/working/folder","recursive":true}]}""");
        var copy = TenantWorkspaceMutationContract.Parse(session, args.Tool, args.CanonicalArguments);
        Assert.Equal(args.ParametersHash, copy.ParametersHash); Assert.Equal(args.Operations, copy.Operations);
        Assert.Contains(("/working/a", "workspaces.delete"), copy.Targets); Assert.Contains(("/working/folder", "workspaces.delete"), copy.Targets);
        Assert.DoesNotContain("recursive\":false", args.CanonicalArguments);
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceBatch,
            """{"operations":[{"op":"mkdir","path":"/home/a"},{"op":"mkdir","path":"/working/b"}]}"""));
    }

    [Fact]
    public void Literal_content_and_edits_obey_UTF8_and_collection_bounds()
    {
        var session = Guid.NewGuid();
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/a", content = new string('é', 16384) }, json));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/a", content = new string('é', 16385) }, json)));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspacePatch, """{"path":"/working/a","edits":[]}"""));
        Assert.Throws<ArgumentException>(() => TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspacePatch, """{"path":"/working/a","edits":[{"oldText":"","newText":"x"}]}"""));
    }

    [Fact]
    public async Task Tenant_projection_closes_nested_schemas_and_keeps_capability_origin_and_batch_bounds()
    {
        var names = new[] { ToolCatalog.WorkspaceWrite, ToolCatalog.WorkspacePatch, ToolCatalog.WorkspaceMkdir, ToolCatalog.WorkspaceCopy, ToolCatalog.WorkspaceMove, ToolCatalog.WorkspaceDelete, ToolCatalog.WorkspaceBatch };
        var d = await CapabilityProjectionTests.Definition(names); var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(d) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var tools = ToolProjectionService.Project(d, context, ToolConfigurationGates.AllowAll);
        foreach (var name in names)
        {
            var tool = Assert.Single(tools, t => t.Name == name); using var schema = JsonDocument.Parse(tool.ParametersJson);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Contains("sole call", tool.Description); Assert.Contains("no dispatch", tool.Description);
            if (name == ToolCatalog.WorkspaceBatch) Assert.Equal(16, schema.RootElement.GetProperty("properties").GetProperty("operations").GetProperty("maxItems").GetInt32());
            if (name == ToolCatalog.WorkspacePatch) Assert.False(schema.RootElement.GetProperty("properties").GetProperty("edits").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        }
        Assert.DoesNotContain(ToolProjectionService.Project(d, context with { ExecutionContext = new(context.ExecutionContext!.TenantId, context.ExecutionContext.AgentPrincipalId, null, session, context.ExecutionContext.RunId, activation, activation, null, null, ExecutionOrigin.ScheduledOccurrence) }, ToolConfigurationGates.AllowAll), t => TenantWorkspaceMutationContract.IsMutation(t.Name));
        Assert.Empty(ToolProjectionService.Project(d, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll));
    }
}
