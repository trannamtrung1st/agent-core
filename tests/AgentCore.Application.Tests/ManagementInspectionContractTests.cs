using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Tests;
public sealed class ManagementInspectionContractTests
{
    [Theory] [InlineData("harness.inspect","{}")] [InlineData("configuration.inspect","{}")] [InlineData("configuration.inspect","{\"target\":\"browser.navigate\",\"goal\":\"Look up information\"}")]
    public void Exact_compound_management_read_roundtrip_preserves_sources_hash_and_closed_existing_intent(string tool,string arguments)
    {
        var args=TenantManagementReadContract.Parse(tool,arguments);var session=Guid.NewGuid();var root=Guid.NewGuid();
        var source=JsonSerializer.Serialize(new[] { new TenantManagementChildRead("skill","instance:review",new('a',64),Guid.NewGuid().ToString("D")+":instance:review") });
        var hash=TenantManagementReadRules.Hash(tool,args.Json,3,new('b',64),new('c',64),"{}",source);
        var value=new PreparedManagementRead(session,root,tool,args.Json,3,new('b',64),new('c',64),"{}",hash,source);TenantManagementReadRules.Validate(value);
        Assert.Equal(value,JsonSerializer.Deserialize<PreparedManagementRead>(JsonSerializer.Serialize(value)));
        var operation=TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(),Guid.NewGuid(),value,3,1);
        Assert.Equal(operation,TenantApprovalRules.Rebuild(JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(operation))!));
        var intent=TenantApprovalToolContract.Parse(new("consent",ToolCatalog.RequestAuthorization,JsonSerializer.Serialize(new { operation="inspect_management",read=new { tool,arguments=JsonSerializer.Deserialize<JsonElement>(args.Json) } })));
        Assert.True(intent.Matches(operation));var reference=TenantManagementReadService.Reference(value,Guid.NewGuid());
        Assert.Equal(source,TenantManagementReadReferences.Shape(reference,session,root).SourcesJson);TenantManagementReadReferences.ValidateShape(reference);
        Assert.Throws<ArgumentException>(()=>TenantManagementReadRules.Validate(value with { SourcesJson="[]" }));
        Assert.NotEqual(hash,TenantManagementReadRules.Hash(tool,args.Json,3,new('b',64),new('c',64),"{}"));
    }
    [Theory]
    [InlineData("harness.inspect","{\"instanceId\":\"foreign\"}")]
    [InlineData("harness.inspect","{\"goal\":\"private\"}")]
    [InlineData("configuration.inspect","{\"tenantId\":\"foreign\"}")]
    [InlineData("configuration.inspect","{\"target\":\"skills.load\",\"target\":\"email.send\"}")]
    [InlineData("configuration.inspect","{\"goal\":\"\"}")]
    [InlineData("configuration.inspect","[]")]
    public void Wider_duplicate_and_malformed_inspection_selectors_fail_closed(string tool,string arguments)=>Assert.ThrowsAny<ArgumentException>(()=>TenantManagementReadContract.Parse(tool,arguments));
    [Fact]
    public void Incumbent_simple_read_serialization_and_hash_remain_unchanged_without_child_witnesses()
    {
        var old=new PreparedManagementRead(Guid.NewGuid(),Guid.NewGuid(),"settings.list","{}",2,new('a',64),new('b',64),"{}",
            TenantManagementReadRules.Hash("settings.list","{}",2,new('a',64),new('b',64),"{}"));
        Assert.DoesNotContain("SourcesJson",JsonSerializer.Serialize(old));TenantManagementReadRules.Validate(old);
        Assert.DoesNotContain("SourcesJson",JsonSerializer.Serialize(TenantManagementReadService.Reference(old,Guid.NewGuid())));
    }
    [Theory] [InlineData(" [] ")] [InlineData("null")] [InlineData("[null]")]
    public void Child_witnesses_require_the_exact_bounded_canonical_shape(string source)=>Assert.Throws<AccessDeniedException>(()=>TenantManagementInspectionSources.Read(source));
    [Theory]
    [InlineData("Use concise responses","Keep the next responses brief")]
    [InlineData("Look up the actual source","Use the browser")]
    [InlineData("An unsupported goal","Another unsupported request")]
    public void Equivalent_diagnosis_wording_has_one_semantic_read_root_while_the_saved_action_stays_exact(string first,string second)
    {
        PreparedTenantOperation Operation(string goal)
        {
            var args=TenantManagementReadContract.Parse("configuration.inspect",JsonSerializer.Serialize(new { goal }));
            var value=new PreparedManagementRead(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("10000000-0000-0000-0000-000000000002"),args.Tool,args.Json,3,new('a',64),new('b',64),"{}",
                TenantManagementReadRules.Hash(args.Tool,args.Json,3,new('a',64),new('b',64),"{}","[]"),"[]");
            return TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(),Guid.Parse("10000000-0000-0000-0000-000000000003"),value,3,1);
        }
        var a=Operation(first);var b=Operation(second);Assert.Equal(a.OperationFingerprint,b.OperationFingerprint);Assert.Equal(a.SemanticActionJson,b.SemanticActionJson);Assert.NotEqual(a.ActionHash,b.ActionHash);
        Assert.NotEqual(TenantManagementReadSelection.SemanticArguments("configuration.inspect","{}"),TenantManagementReadSelection.SemanticArguments("configuration.inspect",JsonSerializer.Serialize(new { goal=first })));
    }

}
