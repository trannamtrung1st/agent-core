using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;
public sealed class TenantHttpRequestContractTests
{
    [Theory] [InlineData("GET")] [InlineData("HEAD")] [InlineData("POST")] [InlineData("PUT")] [InlineData("PATCH")] [InlineData("DELETE")]
    public void Exact_public_request_roundtrips_normalization_hash_and_intent(string method)
    {
        var body=method is "GET" or "HEAD"?null:"<literal>試";
        var args=JsonSerializer.Serialize(new { method=method.ToLowerInvariant(),url="https://public.example/path#fragment",headers=new Dictionary<string,string> { ["accept"]="text/plain" },body });
        // Null body is omitted by the public schema, rather than accepted as an alternate input.
        if(body is null)args=JsonSerializer.Serialize(new { method=method.ToLowerInvariant(),url="https://public.example/path#fragment",headers=new Dictionary<string,string> { ["accept"]="text/plain" } });
        var normalized=TenantHttpRequestContract.Parse(args);Assert.Equal(method,normalized.Method);Assert.Equal("https://public.example/path",normalized.Url);
        var value=TenantHttpRequestContract.Parameters(Guid.NewGuid(),normalized);Assert.Equal(value,TenantHttpRequestContract.Parameters(value.SessionId,TenantHttpRequestContract.Arguments(value)));
        var prepared=TenantApprovalRules.PrepareHttpRequest(Guid.NewGuid(),Guid.NewGuid(),value,3,4);
        Assert.Equal(TenantOperationKind.HttpRequest,prepared.Kind);Assert.Equal("http.request",prepared.Permission);Assert.Equal(64,prepared.OperationFingerprint.Length);Assert.Equal(prepared,TenantApprovalRules.Rebuild(prepared));
        var intent=TenantApprovalToolContract.Parse(new("exact",ToolCatalog.RequestAuthorization,JsonSerializer.Serialize(new { operation="send_http_request",request=JsonSerializer.Deserialize<JsonElement>(value.ArgumentsJson) })));
        Assert.True(intent.Matches(prepared));Assert.False((intent with { Persona=new("Name","Role","Description","Tone") }).Matches(prepared));
        Assert.Throws<ArgumentException>(()=>TenantHttpRequestContract.Arguments(value with { RequestHash=new string('0',64) }));
        Assert.Throws<ArgumentException>(()=>_=(prepared with { Persona=new("Name","Role","Description","Tone") }).Kind);
    }
    [Theory]
    [InlineData("{\"method\":\"POST\",\"url\":\"https://user:password@public.example/\"}")]
    [InlineData("{\"method\":\"POST\",\"url\":\"http://127.0.0.1/\"}")]
    [InlineData("{\"method\":\"POST\",\"url\":\"http://192.168.1.2/\"}")]
    [InlineData("{\"method\":\"CONNECT\",\"url\":\"https://public.example/\"}")]
    [InlineData("{\"method\":\"GET\",\"url\":\"https://public.example/\",\"body\":\"payload\"}")]
    [InlineData("{\"method\":\"POST\",\"url\":\"https://public.example/\",\"headers\":{\"Authorization\":\"secret\"}}")]
    [InlineData("{\"method\":\"POST\",\"url\":\"https://public.example/\",\"headers\":{\"Cookie\":\"secret\"}}")]
    [InlineData("{\"method\":\"POST\",\"url\":\"https://public.example/\",\"requestId\":\"untrusted\"}")]
    [InlineData("{\"method\":\"POST\",\"method\":\"GET\",\"url\":\"https://public.example/\"}")]
    public void Invalid_or_credential_bearing_requests_are_refused(string json)=>Assert.Throws<ArgumentException>(()=>TenantHttpRequestContract.Parse(json));
    [Fact]
    public void Body_is_bounded_by_UTF8_bytes_and_response_metadata_hashes_only_projected_bytes()
    {
        Assert.Throws<ArgumentException>(()=>TenantHttpRequestContract.Parse(JsonSerializer.Serialize(new { method="POST",url="https://public.example/",body=new string('試',21846) })));
        var result=TenantHttpEffectRules.Normalize(new(409,"https://public.example/final","text/plain",new string('試',30000),false,true,null,null,null));
        Assert.True(result.Truncated);Assert.Equal(65535,TenantHttpEffectRules.Metadata(result).ProjectedBodyBytes);TenantHttpEffectRules.Validate(TenantHttpEffectRules.Metadata(result));
        Assert.Throws<ArgumentException>(()=>TenantHttpEffectRules.Normalize(new(0,"https://public.example/",null,"",false,true,null,"transport_error",null)));
    }
    [Fact]
    public void Adding_the_variant_preserves_existing_prepared_and_receipt_serialized_bytes()
    {
        var op=TenantApprovalRules.Prepare(Guid.NewGuid(),Guid.NewGuid(),new("Name","Role","Description","Tone"),1,1);
        var json=JsonSerializer.Serialize(op);Assert.DoesNotContain("HttpRequest",json);Assert.Equal(json,JsonSerializer.Serialize(JsonSerializer.Deserialize<PreparedTenantOperation>(json)));
        Assert.Equal(op.ActionHash,JsonSerializer.Deserialize<PreparedTenantOperation>(json)!.ActionHash);
    }
}
