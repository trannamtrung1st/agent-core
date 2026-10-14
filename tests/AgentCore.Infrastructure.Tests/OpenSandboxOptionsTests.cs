using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxOptionsTests
{
    [Theory]
    [InlineData("http://opensandbox:8080",true,true)]
    [InlineData("http://opensandbox:8080",false,false)]
    [InlineData("http://opensandbox:8081",true,false)]
    [InlineData("http://private-host:8080",true,false)]
    [InlineData("http://172.17.0.1:8080",true,false)]
    [InlineData("http://opensandbox:8080/path",true,false)]
    [InlineData("http://user:password@opensandbox:8080",true,false)]
    [InlineData("http://127.0.0.1:18080",false,true)]
    public void Internal_compose_origin_is_explicit_narrow_and_authenticated(string endpoint,bool trust,bool allowed)
    {
        var options=new OpenSandboxOptions { Endpoint=endpoint,TrustLocalComposeEndpoint=trust,ApiKey="private-fixture" };
        if(allowed) Assert.Equal(endpoint.TrimEnd('/'),options.Origin().ToString().TrimEnd('/'));
        else Assert.Throws<InvalidOperationException>(()=>options.Origin());
        options.ApiKey=null;
        Assert.Throws<InvalidOperationException>(()=>options.Origin());
    }
    [Fact]
    public void Arbitrary_files_are_not_read_as_docker_secrets()
    {
        var options=new OpenSandboxOptions { ApiKeyFile="/etc/passwd" };
        Assert.Null(options.ApiKey);Assert.Throws<InvalidOperationException>(()=>options.Origin());
    }
}
