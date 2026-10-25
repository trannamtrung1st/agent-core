using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceMutationApprovalContractTests
{
    private static readonly AgentCore.Domain.Spaces.RunSpaceWitness Context = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, 1, 1, null, 0, 1);
    private static PreparedTenantOperation Prepare(Guid id, Guid instance, PreparedWorkspaceMutation value, long revision, long persona) =>
        TenantApprovalRules.Rebuild(TenantApprovalRules.PrepareWorkspaceMutation(id, instance, value, revision, persona) with { SpaceContext = Context });

    [Theory]
    [InlineData("workspace.write", """{"path":"/working/a","content":"literal"}""", "workspaces.write", true)]
    [InlineData("workspace.patch", """{"path":"/home/a","edits":[{"oldText":"a","newText":"b"}]}""", "workspaces.write", false)]
    [InlineData("workspace.mkdir", """{"path":"/home/a"}""", "workspaces.write", false)]
    [InlineData("workspace.copy", """{"source":"/home/a","destination":"/working/b"}""", "workspaces.write", true)]
    [InlineData("workspace.move", """{"source":"/home/a","destination":"/home/b"}""", "workspaces.delete", false)]
    [InlineData("workspace.delete", """{"path":"/working/a","recursive":true}""", "workspaces.delete", true)]
    [InlineData("workspace.batch", """{"operations":[{"op":"mkdir","path":"/working/b"},{"op":"delete","path":"/working/a"}]}""", "workspaces.delete", true)]
    public void Exact_mutation_derives_primary_and_required_scopes_and_rebuilds_roundtrip(string tool, string json, string permission, bool working)
    {
        var session = Guid.NewGuid(); var instance = Guid.NewGuid(); var args = TenantWorkspaceMutationContract.Parse(session, tool, json);
        var value = TenantApprovalRules.MutationParameters(session, args, new('a', 64));
        var op = Prepare(Guid.NewGuid(), instance, value, 2, 3);
        Assert.Equal(TenantOperationKind.WorkspaceMutation, op.Kind); Assert.False(op.IsLocalConfiguration);
        Assert.Equal(permission, op.Permission); Assert.Equal("workspace", op.ResourceType); Assert.Equal(working ? "working:" + session.ToString("D") : "home:" + Context.SpaceId.ToString("D"), op.ResourceId);
        Assert.Equal(op, TenantApprovalRules.Rebuild(op));
        Assert.Equal(op, JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(op)));
        Assert.Equal(working, value.Working);
        if (tool == ToolCatalog.WorkspaceCopy)
        {
            var scopes = JsonSerializer.Deserialize<WorkspaceMutationScopeTarget[]>(value.TargetsJson)!;
            Assert.Contains(new WorkspaceMutationScopeTarget("workspaces.read", "/home"), scopes); Assert.Contains(new WorkspaceMutationScopeTarget("workspaces.write", "/working"), scopes);
        }
        if (tool == ToolCatalog.WorkspaceBatch) Assert.Contains("workspaces.write", value.TargetsJson);
    }

    [Fact]
    public void Forged_primary_scope_permissions_coverage_and_source_witness_are_rejected()
    {
        var session = Guid.NewGuid(); var instance = Guid.NewGuid();
        var value = TenantApprovalRules.MutationParameters(session, TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceCopy,
            """{"source":"/home/a","destination":"/working/b"}"""), new('a', 64));
        foreach (var forged in new[] { value with { Working = false }, value with { Permission = "workspaces.read" }, value with { TargetsJson = "[]" },
            value with { ObservationHash = new('z', 64) }, value with { ArgumentsJson = """{"source":"/working/a","destination":"/home/b"}""" } })
            Assert.Throws<ArgumentException>(() => Prepare(Guid.NewGuid(), instance, forged, 1, 1));
    }

    [Fact]
    public void Refusal_fingerprint_excludes_mutable_witnesses_but_exact_action_hash_does_not()
    {
        var session = Guid.NewGuid(); var instance = Guid.NewGuid();
        var value = TenantApprovalRules.MutationParameters(session, TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceWrite,
            """{"path":"/working/a","content":"saved"}"""), new('a', 64));
        var first = Prepare(Guid.NewGuid(), instance, value, 1, 1);
        var changed = Prepare(Guid.NewGuid(), instance, value with { ObservationHash = new('b', 64) }, 2, 2);
        Assert.Equal(first.OperationFingerprint, changed.OperationFingerprint); Assert.NotEqual(first.ActionHash, changed.ActionHash);
        var args = TenantWorkspaceMutationContract.Parse(session, ToolCatalog.WorkspaceWrite, """{"path":"/working/a","content":"different"}""");
        var different = Prepare(Guid.NewGuid(), instance, TenantApprovalRules.MutationParameters(session, args, value.ObservationHash), 1, 1);
        Assert.NotEqual(first.OperationFingerprint, different.OperationFingerprint);
        Assert.Throws<ArgumentException>(() => (first with { Preferences = new("balanced", null, null) }).Kind);
    }
}
