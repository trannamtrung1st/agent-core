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
    [Theory]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("sha256:short", false)] [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData(null, false)]
    public async Task Tag_resolution_requires_a_canonical_manifest_digest(string? digest, bool valid)
    {
        using var client = new HttpClient(new ProbeHandler(request =>
        {
            Assert.Equal("https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0", request.RequestUri!.AbsoluteUri);
            var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            if (digest is not null) result.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
            return result;
        }));
        var image = OciImageSource.Parse("mcr.microsoft.com/dotnet/sdk:10.0").Image;
        if (valid) Assert.Equal("mcr.microsoft.com/dotnet/sdk@" + digest, await RegistryAuthorizationProbe.ResolveTagAsync(new(), image, "10.0", null, client, CancellationToken.None));
        else Assert.Equal(400, (await Assert.ThrowsAsync<AgentCoreException>(() => RegistryAuthorizationProbe.ResolveTagAsync(new(), image, "10.0", null, client, CancellationToken.None))).StatusCode);
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("busybox:latest")]
    [InlineData("ghcr.io/astral-sh/uv:latest")]
    [InlineData("quay.io/prometheus/prometheus:latest")]
    [InlineData("public.ecr.aws/docker/library/busybox:latest")]
    public async Task Live_public_tag_resolution_uses_production_transport(string input)
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_REGISTRY_TESTS") != "1") return;
        var source = OciImageSource.Parse(input);
        var resolved = await RegistryAuthorizationProbe.ResolveTagAsync(new(), source.Image, source.Tag!, null, CancellationToken.None);
        var image = OciImageReference.Parse(resolved);
        Assert.Equal(source.Image.Registry, image.Registry); Assert.Equal(source.Image.Repository, image.Repository);
        Assert.NotEqual(source.Image.Digest, image.Digest);
        await RegistryAuthorizationProbe.CheckAsync(new(), image, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Docker_hub_maps_only_transport_and_scopes_anonymous_or_private_token_challenges(bool privateImage, bool tagged)
    {
        var image = OciImageReference.Parse("docker.io/acme/tool@sha256:" + new string('a', 64));
        var calls = 0;
        using var client = new HttpClient(new ProbeHandler(request =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Equal("https://auth.docker.io/token?scope=repository%3Aacme%2Ftool%3Apull&service=registry.docker.io", request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(privateImage ? "Basic" : null, request.Headers.Authorization?.Scheme);
                if (privateImage) Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("owner:hub-secret-sentinel")), request.Headers.Authorization!.Parameter);
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"hub-bearer-sentinel\"}") };
            }
            Assert.Equal("https://registry-1.docker.io/v2/acme/tool/manifests/" + (tagged ? "v1" : "sha256:" + image.Digest), request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal(calls == 1 ? null : "Bearer", request.Headers.Authorization?.Scheme);
            if (calls == 3)
            {
                Assert.Equal("hub-bearer-sentinel", request.Headers.Authorization!.Parameter);
                var result = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                result.Headers.Add("Docker-Content-Digest", "sha256:" + image.Digest);
                return result;
            }
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd("Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:other:pull,push\"");
            return response;
        }));
        var auth = privateImage ? new ImageAuth { Username = "owner", Password = "hub-secret-sentinel" } : null;
        if (tagged) Assert.Equal(image.Value, await RegistryAuthorizationProbe.ResolveTagAsync(new(), image, "v1", auth, client, CancellationToken.None));
        else await RegistryAuthorizationProbe.CheckAsync(new(), image, auth, client, CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.StartsWith("docker.io/", image.Value);
        Assert.Equal("docker.io", image.Registry);
    }

    [Theory]
    [InlineData("manifestRedirect")] [InlineData("tokenRedirect")]
    [InlineData("foreignRegistry")] [InlineData("foreignRealm")]
    [InlineData("wrongService")] [InlineData("wrongPath")]
    [InlineData("wrongPort")] [InlineData("insecureRealm")]
    public async Task Docker_hub_trust_never_extends_to_other_origins_paths_services_or_redirects(string failure)
    {
        var calls = 0;
        using var client = new HttpClient(new ProbeHandler(request =>
        {
            calls++;
            if (calls == 2 || failure == "manifestRedirect")
            {
                var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new("https://unapproved.example/steal");
                return redirect;
            }
            var realm = failure switch {
                "foreignRealm" => "https://auth.docker.io.evil/token", "wrongPath" => "https://auth.docker.io/other",
                "wrongPort" => "https://auth.docker.io:444/token", "insecureRealm" => "http://auth.docker.io/token",
                _ => "https://auth.docker.io/token" };
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"{realm}\",service=\"{(failure == "wrongService" ? "other" : "registry.docker.io")}\"");
            return response;
        }));
        var registry = failure == "foreignRegistry" ? "docker.io.evil" : "docker.io";
        var image = OciImageReference.Parse(registry + "/acme/tool@sha256:" + new string('a', 64));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => RegistryAuthorizationProbe.CheckAsync(new(), image,
            new ImageAuth { Username = "owner", Password = "hub-secret-sentinel" }, client, CancellationToken.None));
        Assert.Equal(failure == "tokenRedirect" ? 2 : 1, calls);
        Assert.DoesNotContain("sentinel", error.Message);
        Assert.DoesNotContain("unapproved.example", error.Message);
    }

    // Explicit opt-in; ordinary tests never contact Docker Hub or require credentials.
    [Fact]
    public async Task Live_public_docker_hub_manifest_check_uses_production_transport()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_REGISTRY_TESTS") != "1") return;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var tokenResponse = await client.GetAsync("https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/busybox:pull");
        tokenResponse.EnsureSuccessStatusCode();
        using var token = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://registry-1.docker.io/v2/library/busybox/manifests/latest");
        request.Headers.Authorization = new("Bearer", token.RootElement.GetProperty("token").GetString());
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json");
        using var manifest = await client.SendAsync(request);
        manifest.EnsureSuccessStatusCode();
        var digest = manifest.Headers.GetValues("Docker-Content-Digest").Single();
        await RegistryAuthorizationProbe.CheckAsync(new(), OciImageReference.Parse("docker.io/library/busybox@" + digest), null, CancellationToken.None);
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_bearer_transport_uses_only_requested_pull_scope_and_never_follows_token_redirects(bool redirect)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0");
        var heads = 0; var tokens = 0; var stolen = 0;
        app.MapMethods("/v2/acme/tool/manifests/{digest}", ["HEAD"], (HttpContext context) => {
            heads++;
            if (context.Request.Headers.Authorization == "Bearer fixture-bearer") return Results.Ok();
            context.Response.Headers.WWWAuthenticate = $"Bearer realm=\"{app.Urls.Single()}/token\",service=\"fixture\",scope=\"repository:other:pull,push\"";
            return Results.StatusCode(401);
        });
        app.MapGet("/token", (HttpContext context) => {
            tokens++;
            Assert.Equal("repository:acme/tool:pull", context.Request.Query["scope"]);
            Assert.StartsWith("Basic ", context.Request.Headers.Authorization.ToString());
            return redirect ? Results.Redirect(app.Urls.Single() + "/steal") : Results.Json(new { token = "fixture-bearer" });
        });
        app.MapGet("/steal", () => { stolen++; return Results.Ok(); });
        await app.StartAsync();
        try
        {
            var host = new Uri(app.Urls.Single()).Authority;
            var task = RegistryAuthorizationProbe.CheckAsync(new() { InsecureRegistryHosts = [host] },
                OciImageReference.Parse(host + "/acme/tool@sha256:" + new string('a',64)),
                new ImageAuth { Username = "owner", Password = "transport-secret-sentinel" }, CancellationToken.None);
            if (redirect) await Assert.ThrowsAsync<AgentCoreException>(() => task); else await task;
            Assert.Equal(redirect ? 1 : 2, heads); Assert.Equal(1, tokens); Assert.Equal(0, stolen);
        }
        finally { await app.StopAsync(); }
    }

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
            var adapter=new OpenSandboxService(options,profiles,null!,null!,TimeProvider.System);
            Assert.False((await adapter.VerifyProfileAsync(profile,CancellationToken.None)).Passed);Assert.Equal(0,creates);
        }
        finally {await app.StopAsync();}
    }
    [Fact]
    public async Task Credential_rotated_during_manifest_probe_fences_pending_disposable_provisioning()
    {
        var root=Path.Combine(Path.GetTempPath(),"oci-probe-race-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
        await using var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");var creates=0;var probes=0;
        var owners=new AgentCore.Infrastructure.Persistence.InMemoryAgentInstanceStore();
        var credentials=new AgentCore.Infrastructure.Persistence.InMemoryCredentialStore(owners);
        var protector=new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(root);
        var service=new AgentCore.Application.Credentials.CredentialService(credentials,credentials,protector,owners,new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System),TimeProvider.System);
        AgentCore.Application.Credentials.CredentialView? credential=null;
        app.MapMethods("/v2/acme/tool/manifests/{digest}",["HEAD"],async()=>{Interlocked.Increment(ref probes);await service.ReplaceAsync(credential!.CredentialId,credential.Revision,"rotated-probe-sentinel");return Results.Ok();});
        app.MapPost("/sandboxes",()=>{Interlocked.Increment(ref creates);return Results.StatusCode(500);});
        await app.StartAsync();
        try
        {
            var host=new Uri(app.Urls.Single()).Authority;
            credential=await service.CreateContainerRegistryAsync("Race fixture","Password",new(host,"owner",[host+"/acme"]),"private-probe-sentinel");
            var profiles=new AgentCore.Infrastructure.Persistence.InMemorySandboxStore();
            var profile=new AgentCore.Domain.Definitions.SandboxProfile("race",1,"Race","",true,host+"/acme/tool@sha256:"+new string('a',64),RegistryCredentialId:credential.CredentialId);
            await profiles.SaveProfileAsync(profile,0);
            var mounts=Path.Combine(root,"mounts");
            var options=new OpenSandboxOptions {Enabled=true,Endpoint=app.Urls.Single(),ApiKey="fixture-key",InsecureRegistryHosts=[host],AllowedImagePrefixes=[host+"/acme"],CanonicalWorkspaceRoot=mounts,WorkspaceDockerHostRoot=mounts};
            var authority=new SandboxImageAuthority(options,profiles,TimeProvider.System,credentials,protector);
            var adapter=new OpenSandboxService(options,profiles,null!,null!,TimeProvider.System,imageAuthority:authority);
            var result=await adapter.VerifyProfileAsync(profile,CancellationToken.None);
            Assert.False(result.Passed);Assert.Equal(1,probes);Assert.Equal(0,creates);Assert.DoesNotContain("probe-sentinel",result.Message);
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
