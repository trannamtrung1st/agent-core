using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class TenantSkillLoadContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[\"instance:a\"],\"tenantId\":\"other\"}")]
    [InlineData("{\"ids\":[\"instance:a\",\"instance:a\"]}")]
    [InlineData("{\"ids\":[null]}")]
    [InlineData("{\"ids\":[\"arbitrary\"]}")]
    [InlineData("{\"ids\":[\"instance:a\"],\"ids\":[\"instance:b\"]}")]
    [InlineData("{\"ids\":[\"instance:a\",\"instance:b\",\"instance:c\",\"instance:d\",\"instance:e\"]}")]
    public void Private_procedure_selector_rejects_wider_duplicate_malformed_or_excessive_reads(string json)
        => Assert.Throws<ArgumentException>(() => TenantManagementReadContract.Parse("skills.load", json));
    [Fact]
    public void Private_read_selector_is_exact_and_never_turns_public_Core_loading_into_private_consent()
    {
        var args = TenantManagementReadContract.Parse("skills.load", "{\"ids\":[\"instance:first\",\"definition:second\"]}");
        Assert.Equal(args.Json, TenantManagementReadContract.Parse("skills.load", args.Json).Json);
        Assert.Equal(new[] { "definition:second", "instance:first" }, TenantSkillLoadContract.Parse(args.Json));
        Assert.Equal(args.Json, TenantManagementReadContract.Parse("skills.load", "{\"ids\":[\"definition:second\",\"instance:first\"]}").Json);
        Assert.Throws<ArgumentException>(() => TenantManagementReadContract.Parse("skills.load", "{\"ids\":[\"builtin:harness-authoring.v1\"]}"));
    }
}
