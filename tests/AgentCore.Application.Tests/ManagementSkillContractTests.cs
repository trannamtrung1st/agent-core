using System.Text.Json.Nodes;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;
public sealed class ManagementSkillContractTests
{
    private const string Input = """{"expectedInstanceRevision":4,"name":"Exact Skill","description":"Bounded description","procedure":"Compare the observed totals.","projection":"OnDemand","enabled":true,"requiredCapabilities":["chat.respond"],"id":"exact-skill"}""";
    [Theory]
    [InlineData("name", "null")] [InlineData("name", "1")] [InlineData("name", "\"\"")]
    [InlineData("description", "false")] [InlineData("procedure", "{}")] [InlineData("projection", "1")]
    [InlineData("enabled", "\"true\"")] [InlineData("requiredCapabilities", "null")] [InlineData("requiredCapabilities", "{}")] [InlineData("requiredCapabilities", "[null]")]
    [InlineData("requiredCapabilities", "[\"chat.respond\",\"chat.respond\"]")] [InlineData("expectedInstanceRevision", "\"4\"")] [InlineData("expectedInstanceRevision", "0")]
    [InlineData("id", "null")] [InlineData("tenantId", "\"arbitrary-authority\"")]
    public void Exact_Skill_contract_rejects_wrong_types_and_extra_authority_without_runtime_exceptions(string field, string json)
    {
        var body = JsonNode.Parse(Input)!.AsObject(); body[field] = JsonNode.Parse(json);
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("skills.create", body.ToJsonString()));
    }
    [Fact]
    public void Exact_Skill_contract_keeps_bounded_values_and_semantic_rejection_identity_independent_of_revision_witnesses()
    {
        var args = TenantManagementMutationContract.Parse("skills.create", Input);
        Assert.Equal(args.Json, TenantManagementMutationContract.Parse("skills.create", args.Json).Json);
        var first = """{"key":"instance:exact-skill","expectedRevision":1,"expectedInstanceRevision":4,"enabled":false}""";
        var second = """{"key":"instance:exact-skill","expectedRevision":2,"expectedInstanceRevision":8,"enabled":false}""";
        var a = TenantManagementMutationContract.Parse("skills.set_enabled", first); var b = TenantManagementMutationContract.Parse("skills.set_enabled", second);
        Assert.Equal(TenantManagementMutationContract.SemanticArguments(a.Json), TenantManagementMutationContract.SemanticArguments(b.Json));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("skills.create", Input.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false")));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("skills.set_enabled", first.Replace("\"instance:exact-skill\"", "\"arbitrary\"")));
    }
}
