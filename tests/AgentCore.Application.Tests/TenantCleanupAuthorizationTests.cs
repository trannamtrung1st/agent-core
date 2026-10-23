using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;
public sealed class TenantCleanupAuthorizationTests
{
    [Fact]
    public void Cleanup_keeps_exact_authorization_only_for_previously_admitted_browser_cleanup()
    {
        var offered=TenantApprovalToolContract.Descriptor(settings:true,credentials:true,browserTools:[ToolCatalog.BrowserNavigate,ToolCatalog.BrowserClose,ToolCatalog.BrowserHover,ToolCatalog.BrowserTabs],httpRequest:true).ModelDefinition;
        var result=Assert.Single(RunFinalization.CleanupTools([offered]));
        Assert.Contains("browser.close",result.ParametersJson);Assert.Contains("browser.hover",result.ParametersJson);Assert.DoesNotContain("browser.navigate",result.ParametersJson);
        Assert.DoesNotContain("send_http_request",result.ParametersJson);Assert.DoesNotContain("update_persona",result.ParametersJson);Assert.DoesNotContain("check_credential_connection",result.ParametersJson);
        Assert.Contains("\"const\":false",result.ParametersJson);
        using var json=JsonDocument.Parse(result.ParametersJson);var tabs=json.RootElement.GetProperty("oneOf")[0].GetProperty("properties").GetProperty("command").GetProperty("oneOf").EnumerateArray().Single(x=>x.GetProperty("properties").GetProperty("tool").GetProperty("enum")[0].GetString()==ToolCatalog.BrowserTabs);
        Assert.DoesNotContain(tabs.GetProperty("properties").GetProperty("arguments").GetProperty("properties").GetProperty("operation").GetProperty("enum").EnumerateArray(),x=>x.GetString()=="new");
    }
    [Theory]
    [InlineData("browser.close","{}",false,true)]
    [InlineData("browser.hover","{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"Account\"}}",false,true)]
    [InlineData("browser.navigate","{\"url\":\"https://public.example/\"}",false,false)]
    [InlineData("browser.tabs","{\"operation\":\"new\",\"url\":\"https://public.example/\"}",false,false)]
    [InlineData("browser.close","{}",true,false)]
    public void Cleanup_admission_rechecks_the_exact_typed_intent(string tool,string arguments,bool downloads,bool allowed)
    {
        var call=new ModelToolCall("cleanup",ToolCatalog.RequestAuthorization,JsonSerializer.Serialize(new { operation="run_browser_command",command=new { tool,arguments=JsonSerializer.Deserialize<JsonElement>(arguments),downloadArtifacts=downloads } }));
        Assert.Equal(allowed,RunFinalization.CleanupCall(call));
    }
    [Fact]
    public void A_generic_authorization_name_cannot_admit_other_work_in_cleanup()
    {
        Assert.False(RunFinalization.CleanupCall(new("http",ToolCatalog.RequestAuthorization,"{\"operation\":\"send_http_request\",\"request\":{\"method\":\"POST\",\"url\":\"https://public.example/\"}}")));
        Assert.Empty(RunFinalization.CleanupTools([TenantApprovalToolContract.Descriptor(settings:true,credentials:false).ModelDefinition]));
    }
    [Theory]
    [InlineData("[]")] [InlineData("{\"operation\":1}")]
    public void Malformed_direct_tab_intent_is_refused_during_cleanup(string arguments) =>
        Assert.False(RunFinalization.CleanupCall(new("tabs",ToolCatalog.BrowserTabs,arguments)));
}
