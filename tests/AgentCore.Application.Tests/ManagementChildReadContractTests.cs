using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;
public sealed class ManagementChildReadContractTests
{
    [Theory]
    [InlineData("skills.list", "{}", "skill", "instance:review")]
    [InlineData("skills.inspect", "{\"key\":\"instance:review\"}", "skillProcedure", "instance:review")]
    [InlineData("resources.list", "{}", "resource", "instance:10000000-0000-0000-0000-000000000001")]
    [InlineData("resources.inspect", "{\"key\":\"instance:10000000-0000-0000-0000-000000000001\"}", "resource", "instance:10000000-0000-0000-0000-000000000001")]
    public void Child_reads_retain_exact_source_kind_key_and_closed_existing_consent(string tool, string json, string kind, string key)
    {
        var args = TenantManagementReadContract.Parse(tool, json);
        var sources = JsonSerializer.Serialize(new[] { new TenantManagementChildRead(kind, key, new('a', 64), Guid.NewGuid().ToString("D") + ":" + key) });
        var value = new PreparedManagementRead(Guid.NewGuid(), Guid.NewGuid(), tool, args.Json, 1, new('b',64), new('c',64), "{}",
            TenantManagementReadRules.Hash(tool, args.Json, 1, new('b',64), new('c',64), "{}", sources), sources);
        TenantManagementReadRules.Validate(value);
        var operation = TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        var intent = TenantApprovalToolContract.Parse(new("consent", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "inspect_management", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(args.Json) } })));
        Assert.True(intent.Matches(operation));
        var reference = TenantManagementReadService.Reference(value, Guid.NewGuid());
        TenantManagementReadReferences.ValidateShape(reference);
        Assert.Equal(reference, JsonSerializer.Deserialize<TenantManagementReadReference>(JsonSerializer.Serialize(reference)));
        Assert.Equal(operation, TenantApprovalRules.Rebuild(JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(operation))!));
        var wrongSources = JsonSerializer.Serialize(new[] { new TenantManagementChildRead("credential", key, new('a',64), Guid.NewGuid().ToString("D")) });
        Assert.Throws<ArgumentException>(() => TenantManagementReadRules.Validate(value with { SourcesJson = wrongSources,
            ObservationHash = TenantManagementReadRules.Hash(tool, args.Json, 1, new('b',64), new('c',64), "{}", wrongSources) }));
    }

    [Theory]
    [InlineData("skills.list", "{\"page\":1}")]
    [InlineData("resources.list", "{\"tenantId\":\"foreign\"}")]
    [InlineData("skills.inspect", "{\"key\":\"review\"}")]
    [InlineData("skills.inspect", "{\"key\":\"instance:review\",\"key\":\"instance:other\"}")]
    [InlineData("resources.inspect", "{\"key\":\"instance:not-an-owned-resource\"}")]
    [InlineData("resources.inspect", "{\"key\":\"instance:10000000-0000-0000-0000-000000000001\",\"limit\":32000}")]
    [InlineData("skills.inspect", "[]")]
    [InlineData("skills.inspect", "{\"key\":null}")]
    public void Child_reads_reject_ambiguous_or_unqualified_selections(string tool, string json) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantManagementReadContract.Parse(tool, json));
}
