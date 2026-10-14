using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class SandboxApiTests
{
    [Fact]
    public async Task Owner_profile_revisions_limits_and_safe_audit_work_without_a_running_backend()
    {
        await using var factory = new SandboxFactory(); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/v2/admin/sandboxes")).StatusCode);
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var initial = (await owner.GetFromJsonAsync<SandboxPlatformResponse>("/api/v2/admin/sandboxes"))!;
        Assert.Equal("Disabled",initial.State); Assert.Empty(initial.Profiles);
        var request = new SaveSandboxProfileRequest(0,"Local Workbench","Profile",true,"sha256:"+new string('a',64),"printf private-script-sentinel");
        var response = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request); response.EnsureSuccessStatusCode();
        Assert.Equal(1,(await response.Content.ReadFromJsonAsync<SandboxProfileResponse>())!.Revision);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request with { ExpectedRevision=1, ImageReference="busybox:latest" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request with { ExpectedRevision=1, Cpu=4 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request with { ExpectedRevision=1, Pids=128 })).StatusCode);
        (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request with { ExpectedRevision=1, Name="Changed" })).EnsureSuccessStatusCode();
        Assert.Equal("Local Workbench",(await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/workbench?revision=1"))!.Name);
        var audit = await owner.GetStringAsync("/api/v2/admin/events?targetType=sandbox.profile&targetId=workbench");
        Assert.Contains("SandboxProfileChanged",audit); Assert.DoesNotContain("private-script-sentinel",audit); Assert.DoesNotContain("sha256",audit);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench/verify",new VerifySandboxProfileRequest(2))).StatusCode);
        var verification = await owner.PostAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench/verify",new VerifySandboxProfileRequest(2)); verification.EnsureSuccessStatusCode();
        Assert.False((await verification.Content.ReadFromJsonAsync<SandboxVerificationResponse>())!.Passed);
    }

    private sealed class SandboxFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<OpenSandboxOptions>(); s.AddSingleton(new OpenSandboxOptions { AllowLocalImageIds=true }); });
        }
    }
}
