using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceReadAuthorizationContractTests
{
    [Theory]
    [InlineData("""{"path":"/home"}""")]
    [InlineData("""{"path":"/home","query":"","maxResults":1}""")]
    [InlineData("""{"path":"/home","query":"note","glob":"../**"}""")]
    [InlineData("""{"path":"/home","query":"note","maxResults":51}""")]
    [InlineData("""{"path":"/home","query":"note","cursor":"other"}""")]
    [InlineData("""{"path":"/home","query":"note","query":"other"}""")]
    public void Workspace_search_arguments_are_closed_and_bounded(string json) => Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceSearch, json));

    [Fact]
    public void Workspace_search_refusal_identity_binds_query_glob_and_result_bound_but_excludes_mutable_source()
    {
        var session = Guid.NewGuid(); var instance = Guid.NewGuid(); var id = Guid.NewGuid();
        var args = TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceSearch, """{"path":"/working","query":"報告 report","glob":"**/*.txt","maxResults":10}""");
        var read = new PreparedWorkspaceRead(session, true, ToolCatalog.WorkspaceSearch, TenantWorkspaceReadContract.Canonical(ToolCatalog.WorkspaceSearch, args), new string('a', 64));
        var exact = TenantApprovalRules.PrepareWorkspaceRead(id, instance, read, 1, 1);
        Assert.Equal(exact.OperationFingerprint, TenantApprovalRules.PrepareWorkspaceRead(id, instance, read with { ObservationHash = new string('b', 64) }, 2, 1).OperationFingerprint);
        foreach (var changed in new[] { args with { Query = "different" }, args with { Glob = "*.csv" }, args with { MaxResults = 1 } })
            Assert.NotEqual(exact.OperationFingerprint, TenantApprovalRules.PrepareWorkspaceRead(id, instance, read with { ArgumentsJson = TenantWorkspaceReadContract.Canonical(ToolCatalog.WorkspaceSearch, changed) }, 1, 1).OperationFingerprint);
        var json = System.Text.Json.JsonSerializer.Serialize(new { operation = "inspect_workspace", read = new { tool = ToolCatalog.WorkspaceSearch, arguments = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(read.ArgumentsJson) } });
        Assert.True(TenantApprovalToolContract.Parse(new("request", ToolCatalog.RequestAuthorization, json)).Matches(exact));
    }

    [Theory]
    [InlineData("{\"operation\":\"inspect_workspace\",\"read\":{\"tool\":\"workspace.delete\",\"arguments\":{\"path\":\"/home/a\"}}}")]
    [InlineData("{\"operation\":\"inspect_workspace\",\"tenantId\":\"other\",\"read\":{\"tool\":\"workspace.read\",\"arguments\":{\"path\":\"/home/a\"}}}")]
    [InlineData("{\"operation\":\"inspect_workspace\",\"read\":{\"tool\":\"workspace.read\",\"tool\":\"workspace.list\",\"arguments\":{\"path\":\"/home/a\"}}}")]
    [InlineData("{\"operation\":\"inspect_workspace\",\"read\":{\"tool\":\"workspace.read\",\"arguments\":{\"path\":\"/home/a\",\"principalId\":\"owner\"}}}")]
    [InlineData("{\"operation\":\"inspect_workspace\",\"read\":{\"tool\":\"workspace.list\",\"arguments\":{\"path\":\"/working\",\"limit\":257}}}")]
    public void Read_consent_accepts_only_one_closed_read_intent_without_caller_authority(string json) => Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("request", ToolCatalog.RequestAuthorization, json)));

    [Fact]
    public void Exact_source_and_instance_witnesses_change_the_action_but_not_the_same_root_refusal_identity()
    {
        var instance = Guid.NewGuid(); var session = Guid.NewGuid(); var id = Guid.NewGuid();
        var value = new PreparedWorkspaceRead(session, false, ToolCatalog.WorkspaceRead, """{"path":"/home/private.txt"}""", new string('a', 64));
        var first = TenantApprovalRules.PrepareWorkspaceRead(id, instance, value, 1, 1);
        var changed = TenantApprovalRules.PrepareWorkspaceRead(id, instance, value with { ObservationHash = new string('b', 64) }, 2, 1);
        Assert.NotEqual(first.ActionHash, changed.ActionHash); Assert.Equal(first.OperationFingerprint, changed.OperationFingerprint);
        var other = TenantApprovalRules.PrepareWorkspaceRead(id, instance, value with { ArgumentsJson = """{"path":"/home/another.txt"}""" }, 1, 1);
        Assert.NotEqual(first.OperationFingerprint, other.OperationFingerprint);
        var intent = TenantApprovalToolContract.Parse(new("request", ToolCatalog.RequestAuthorization, """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/home/private.txt"}}}"""));
        Assert.True(intent.Matches(first)); Assert.False(intent.Matches(other));
        Assert.Equal(TenantOperationKind.WorkspaceRead, first.Kind); Assert.Equal("home:" + instance.ToString("D"), first.ResourceId);
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.Rebuild(first with { ArtifactPublication = new(session, "{}") }));
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => TenantApprovalRules.PrepareWorkspaceRead(id, instance, value with { Working = true }, 1, 1));
    }
}
