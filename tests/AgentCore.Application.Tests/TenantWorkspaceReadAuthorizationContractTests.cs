using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceReadAuthorizationContractTests
{
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
