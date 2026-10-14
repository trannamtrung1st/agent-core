using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Tests;

public sealed class RegistryAuthorizationProbeTests
{
    [Fact]
    public async Task Remote_private_source_declared_public_cannot_reuse_cached_bytes_without_authorization()
    {
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");var creates=0;
        app.MapMethods("/v2/acme/tool/manifests/{digest}",["HEAD"],(HttpContext context)=>{Assert.False(context.Request.Headers.ContainsKey("Authorization"));context.Response.Headers.WWWAuthenticate="Basic realm=\"fixture\"";return Results.StatusCode(401);});
        app.MapPost("/v1/sandboxes",()=>{Interlocked.Increment(ref creates);return Results.StatusCode(500);});
        await app.StartAsync();
        try
        {
            var host=new Uri(app.Urls.Single()).Authority;
            var profiles=new AgentCore.Infrastructure.Persistence.InMemorySandboxStore();
            var profile=new SandboxProfile("anonymous",1,"Anonymous","",true,host+"/acme/tool@sha256:"+new string('a',64));
            await profiles.SaveProfileAsync(profile,0);
            var options=new OpenSandboxOptions {Enabled=true,Endpoint=app.Urls.Single(),ApiKey="fixture-key",InsecureRegistryHosts=[host],AllowedImagePrefixes=[host+"/acme"]};
            var adapter=new OpenSandboxService(options,profiles,null!,null!,null!,TimeProvider.System);
            Assert.False((await adapter.VerifyProfileAsync(profile,CancellationToken.None)).Passed);Assert.Equal(0,creates);
        }
        finally {await app.StopAsync();}
    }
    [Fact]
    public async Task Credential_rotated_during_manifest_probe_fences_pending_disposable_provisioning()
    {
        var root=Path.Combine(Path.GetTempPath(),"oci-probe-race-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");var creates=0;
        var owners=new AgentCore.Infrastructure.Persistence.InMemoryAgentInstanceStore();
        var credentials=new AgentCore.Infrastructure.Persistence.InMemoryCredentialStore(owners);
        var protector=new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(root);
        var service=new AgentCore.Application.Credentials.CredentialService(credentials,credentials,protector,owners,new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System),TimeProvider.System);
        AgentCore.Application.Credentials.CredentialView? credential=null;
        app.MapMethods("/v2/acme/tool/manifests/{digest}",["HEAD"],async()=>{await service.ReplaceAsync(credential!.CredentialId,credential.Revision,"rotated-probe-sentinel");return Results.Ok();});
        app.MapPost("/sandboxes",()=>{Interlocked.Increment(ref creates);return Results.StatusCode(500);});
        await app.StartAsync();
        try
        {
            var host=new Uri(app.Urls.Single()).Authority;
            credential=await service.CreateContainerRegistryAsync("Race fixture","Password",new(host,"owner",[host+"/acme"]),"private-probe-sentinel");
            var profiles=new AgentCore.Infrastructure.Persistence.InMemorySandboxStore();
            var profile=new AgentCore.Domain.Definitions.SandboxProfile("race",1,"Race","",true,host+"/acme/tool@sha256:"+new string('a',64),RegistryCredentialId:credential.CredentialId);
            await profiles.SaveProfileAsync(profile,0);
            var options=new OpenSandboxOptions {Enabled=true,Endpoint=app.Urls.Single(),ApiKey="fixture-key",InsecureRegistryHosts=[host],AllowedImagePrefixes=[host+"/acme"]};
            var authority=new SandboxImageAuthority(options,profiles,TimeProvider.System,credentials,protector);
            var adapter=new OpenSandboxService(options,profiles,null!,null!,null!,TimeProvider.System,imageAuthority:authority);
            var result=await adapter.VerifyProfileAsync(profile,CancellationToken.None);
            Assert.False(result.Passed);Assert.Equal(0,creates);Assert.DoesNotContain("probe-sentinel",result.Message);
            Assert.Equal(2,(await credentials.GetAsync(credential.CredentialId))!.Revision);
        }
        finally {await app.StopAsync();Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("redirect")] [InlineData("foreignRealm")] [InlineData("reflectedError")]
    public async Task Private_manifest_checks_never_forward_secrets_to_redirects_foreign_realms_or_error_projections(string failure)
    {
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");var calls=0;
        app.MapMethods("/v2/acme/tool/manifests/{digest}",["HEAD"],(HttpContext context)=>{
            Interlocked.Increment(ref calls);
            if(failure=="redirect") {context.Response.Headers.Location="https://unapproved.example/steal";return Results.StatusCode(302);}
            if(failure=="foreignRealm") {context.Response.Headers.WWWAuthenticate="Bearer realm=\"https://unapproved.example/token\",service=\"fixture\",scope=\"repository:other:pull,push\"";return Results.StatusCode(401);}
            context.Response.Headers.WWWAuthenticate="Basic realm=\"private-probe-sentinel\"";return Results.StatusCode(403);
        });
        await app.StartAsync();
        try
        {
            var host=new Uri(app.Urls.Single()).Authority;
            var options=new OpenSandboxOptions {InsecureRegistryHosts=[host],AllowedImagePrefixes=[host+"/acme"]};options.ValidatePolicy();
            var image=OciImageReference.Parse(host+"/acme/tool@sha256:"+new string('a',64));
            var error=await Assert.ThrowsAsync<AgentCoreException>(()=>RegistryAuthorizationProbe.CheckAsync(options,image,new ImageAuth {Username="owner",Password="private-probe-sentinel"},CancellationToken.None));
            Assert.DoesNotContain("private-probe-sentinel",error.Message);Assert.DoesNotContain("unapproved.example",error.Message);Assert.Equal(1,calls);
        }
        finally {await app.StopAsync();}
    }
}
