using System.Text.Json.Nodes;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;
public sealed class ManagementResourceContractTests
{
    private const string Input = """{"expectedInstanceRevision":4,"logicalPath":"reviewed.txt","kind":"Reference","mediaType":"text/plain","content":"Exact literal draft","source":"conversation:user"}""";
    [Theory]
    [InlineData("content", "null")] [InlineData("content", "{}")]
    [InlineData("source", "false")] [InlineData("source", "null")] [InlineData("source", "\"\"")]
    [InlineData("logicalPath", "false")] [InlineData("kind", "1")] [InlineData("kind", "\"EvalFixture\"")]
    [InlineData("mediaType", "\"image/png\"")] [InlineData("expectedInstanceRevision", "0")]
    [InlineData("tenantId", "\"foreign\"")] [InlineData("key", "\"definition:10000000-0000-0000-0000-000000000001\"")]
    public void Resource_contract_withholds_malformed_literal_and_extra_authority(string field, string json)
    {
        var input = JsonNode.Parse(Input)!.AsObject(); input[field] = JsonNode.Parse(json);
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("resources.upsert", input.ToJsonString()));
    }
    [Fact]
    public void Resource_contract_preserves_literal_and_correlates_the_same_operation_across_revisions()
    {
        var parsed = TenantManagementMutationContract.Parse("resources.upsert", Input);
        Assert.Equal(parsed.Json, TenantManagementMutationContract.Parse("resources.upsert", parsed.Json).Json);
        var a = """{"key":"instance:10000000-0000-0000-0000-000000000001","expectedInstanceRevision":4,"expectedResourceRevision":1,"enabled":false}""";
        var b = a.Replace("\"expectedInstanceRevision\":4", "\"expectedInstanceRevision\":8").Replace("\"expectedResourceRevision\":1", "\"expectedResourceRevision\":3");
        var first = TenantManagementMutationContract.Parse("resources.set_enabled", a);
        var second = TenantManagementMutationContract.Parse("resources.set_enabled", b);
        Assert.Equal(TenantManagementMutationContract.SemanticArguments(first.Json), TenantManagementMutationContract.SemanticArguments(second.Json));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("resources.upsert", Input.Replace("\"source\":", "\"source\":\"conversation:user\",\"source\":")));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("resources.copy", a));
    }
}
