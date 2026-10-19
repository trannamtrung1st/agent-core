using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantKnowledgeReadContractTests
{
    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("{\"identity\":3}")]
    [InlineData("{\"identity\":\"\"}")] [InlineData("{\"identity\":\"   \"}")]
    [InlineData("{\"identity\":\"source\",\"identity\":\"other\"}")]
    [InlineData("{\"identity\":\"source\",\"path\":\"/home/private\"}")]
    [InlineData("{\"identity\":\"source\",\"tenantId\":\"other\"}")]
    [InlineData("{\"identity\":\"source\",\"citation\":\"forged\"}")]
    public void Knowledge_arguments_never_accept_caller_authority_or_source_metadata(string json) =>
        Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.KnowledgeRetrieve, json));
    [Fact]
    public void Knowledge_exact_action_binds_metadata_but_root_refusal_retains_identity()
    {
        var instance = Guid.NewGuid(); var session = Guid.NewGuid(); var key = "instance:" + Guid.NewGuid().ToString("D");
        var read = new PreparedWorkspaceRead(session, false, ToolCatalog.KnowledgeRetrieve, "{\"identity\":\"" + key + "\"}", new string('a', 64), key, 1,
            Knowledge: new(key, "Policy", "Policy citation", "/agent/instance/resources/policy.md"));
        var first = TenantApprovalRules.PrepareWorkspaceRead(Guid.NewGuid(), instance, read, 1, 1);
        var changed = TenantApprovalRules.PrepareWorkspaceRead(Guid.NewGuid(), instance, read with { ObservationHash = new string('b', 64), Knowledge = read.Knowledge! with { Citation = "Changed citation" } }, 2, 1);
        Assert.NotEqual(first.ActionHash, changed.ActionHash); Assert.Equal(first.OperationFingerprint, changed.OperationFingerprint);
        Assert.Equal("resources.read", first.Permission); Assert.Equal(instance.ToString("D") + ":" + key, first.ResourceId);
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareWorkspaceRead(Guid.NewGuid(), instance, read with { Knowledge = null }, 1, 1));
    }
}
