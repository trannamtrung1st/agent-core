using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class SandboxApiTests
{
    [Fact]
    public async Task Historical_drafts_require_explicit_owner_repair_and_never_restore_retired_execution()
    {
        await using var factory = new SandboxFactory();
        var lifecycle = factory.Services.GetRequiredService<AgentCore.Application.Admin.AgentDefinitionLifecycleService>();
        var source = (await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
        Assert.Contains("sandbox.run", RoleEnvironments.Of(source).ToolList);
        var draft = await lifecycle.ForkDraftAsync(source.Id, source.Version, DefinitionDraftSourceKind.ForkBuiltIn);
        var rejected = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(()=>lifecycle.UpdateDraftAsync(
            draft.DraftId,draft.Revision,draft.Candidate with {SystemInstructions="Explicit owner edit."}).AsTask());
        Assert.Equal(400,rejected.StatusCode);
        var repaired = await lifecycle.UpdateDraftAsync(draft.DraftId,draft.Revision,
            HarnessLegacyFixture.WithoutRetiredSandboxTool(draft.Candidate) with {SystemInstructions="Explicit owner edit."});
        Assert.Equal(draft.Revision+1,repaired.Revision);
        Assert.DoesNotContain("sandbox.run",repaired.Candidate.Environment!.ToolList);
        Assert.DoesNotContain("sandbox.exec",repaired.Candidate.Environment.ToolList);
        Assert.Null(repaired.Candidate.SandboxPolicy);
        Assert.Contains("sandbox.run",RoleEnvironments.Of((await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(source.Id,source.Version))!).ToolList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_requests_without_a_profile_are_validation_errors_and_preserve_credentials(bool explicitNull)
    {
        await using var factory = new SandboxFactory(); using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var body = new Dictionary<string, object?> { ["displayName"] = "Invalid registry", ["kind"] = "Password", ["protectedValue"] = "invalid-registry-sentinel" };
        if (explicitNull) body["profile"] = null;
        var rejected = await owner.PostAsJsonAsync("/api/v2/admin/container-registry-credentials", body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.DoesNotContain("invalid-registry-sentinel", await rejected.Content.ReadAsStringAsync());
        var created = await owner.PostAsJsonAsync("/api/v2/admin/container-registry-credentials",
            new AdminCreateContainerRegistryRequest("Valid registry", "Password", new("docker.io", "owner", []), "fixture-value"));
        created.EnsureSuccessStatusCode();
        var credential = (await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        body.Remove("kind"); body.Remove("protectedValue"); body["expectedRevision"] = credential.Revision; body["status"] = "Active";
        rejected = await owner.PatchAsJsonAsync($"/api/v2/admin/container-registry-credentials/{credential.CredentialId}", body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var unchanged = (await owner.GetFromJsonAsync<AdminCredentialResponse>($"/api/v2/admin/credentials/{credential.CredentialId}"))!;
        Assert.Equal(credential.Revision, unchanged.Revision);
        Assert.Equal(credential.DisplayName, unchanged.DisplayName);
        Assert.Equal(credential.ContainerRegistry!.Registry, unchanged.ContainerRegistry!.Registry);
    }

    [Fact]
    public async Task Verified_configuration_remains_inspectable_but_disabled_server_blocks_selection_and_run_pins()
    {
        await using var factory=new SandboxFactory();using var owner=TestOwnerCapability.CreateOwnerClient(factory);
        var store=factory.Services.GetRequiredService<ISandboxStore>();
        var profile=new SandboxProfile("general-workbench",1,"Verified fixture","",true,"sha256:"+new string('a',64));
        await store.SaveProfileAsync(profile,0);
        await store.SaveVerificationAsync(new(profile.Id,1,DateTimeOffset.UtcNow,true,"Policy fixture observation",ContractVersion:1));
        var created=await owner.PostAsJsonAsync("/api/v2/admin/agent-instances",new{definitionId="general-assistant",version=30});created.EnsureSuccessStatusCode();
        var instance=(await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>());var id=instance.GetProperty("instanceId").GetGuid();
        var saved=(await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/general-workbench"))!;
        Assert.Equal("ServerUnavailable",saved.Availability);Assert.True(saved.Verification!.Passed);
        var resolved=await factory.Services.GetRequiredService<AgentCore.Application.Execution.AgentRunConfigurationResolver>().ResolveAsync(id);
        Assert.Null(resolved.Configuration.Definition.ExecutionSandboxProfile);
        var patch=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",new{expectedInstanceRevision=instance.GetProperty("revision").GetInt64(),set=new{profileId=profile.Id},clear=Array.Empty<string>()});
        Assert.Equal(HttpStatusCode.BadRequest,patch.StatusCode);
    }
    [Fact]
    public async Task Registry_credentials_are_safe_host_authority_and_source_epoch_changes_invalidate_profiles()
    {
        await using var factory = new SandboxFactory(); using var owner=TestOwnerCapability.CreateOwnerClient(factory);
        var created = await owner.PostAsJsonAsync("/api/v2/admin/container-registry-credentials",new AdminCreateContainerRegistryRequest("OCI fixture","Token",new("docker.io","owner",["docker.io/agent-core"]),"oci-secret-sentinel"));
        created.EnsureSuccessStatusCode();
        var credential=(await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal("ContainerRegistry",credential.Purpose);Assert.DoesNotContain("oci-secret-sentinel",await owner.GetStringAsync("/api/v2/admin/credentials"));
        var image="docker.io/agent-core/tool@sha256:"+new string('a',64);
        var request=new SaveSandboxProfileRequest(0,"Private OCI","",true,image,RegistryCredentialId:credential.CredentialId);
        var response=await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/private",request);response.EnsureSuccessStatusCode();
        Assert.Equal("NeedsVerification",(await response.Content.ReadFromJsonAsync<SandboxProfileResponse>())!.Availability);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/wrong-host",request with {ImageReference="ghcr.io/trannamtrung1st/tool@sha256:"+new string('a',64)})).StatusCode);
        var instanceResponse=await owner.PostAsJsonAsync("/api/v2/admin/agent-instances",new{definitionId="general-assistant",version=30});instanceResponse.EnsureSuccessStatusCode();
        var instance=(await instanceResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>());
        var binding=await owner.PostAsJsonAsync($"/api/v2/admin/agent-instances/{instance.GetProperty("instanceId").GetGuid()}/credential-bindings",new AdminBindCredentialRequest(credential.CredentialId,"oci",instance.GetProperty("revision").GetInt64()));
        Assert.Equal(HttpStatusCode.Forbidden,binding.StatusCode);
        (await owner.PatchAsJsonAsync($"/api/v2/admin/credentials/{credential.CredentialId}",new AdminUpdateCredentialRequest(credential.Revision,credential.DisplayName,"Disabled",credential.Metadata,[]))).EnsureSuccessStatusCode();
        var unavailable=(await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/private"))!;
        Assert.Equal("SourceUnavailable",unavailable.Availability);
        // Revoked source can still be explicitly disabled; the owner cannot be trapped by its credential.
        (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/private",request with {ExpectedRevision=1,Enabled=false})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/sensitive",new{expectedRevision=0,name="Unsafe",description="",enabled=true,imageReference=image,password="oci-secret-sentinel"})).StatusCode);
    }
    [Fact]
    public async Task Instance_sandbox_sparse_settings_preserve_authorization_CAS_and_effective_inspection()
    {
        await using var factory = new SandboxFactory();using var owner=TestOwnerCapability.CreateOwnerClient(factory);
        await factory.Services.GetRequiredService<ISandboxStore>().SaveProfileAsync(new("general-workbench",1,"Workbench","",true,"sha256:"+new string('a',64),"echo Profile"),0);
        var created=await owner.PostAsJsonAsync("/api/v2/admin/agent-instances",new{definitionId="general-assistant",version=30});created.EnsureSuccessStatusCode();
        var instance=await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();var id=instance.GetProperty("instanceId").GetGuid();
        var section=await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy");
        var revision=section.GetProperty("instanceRevision").GetInt64();Assert.Equal(1800,section.GetProperty("effective").GetProperty("idleTtlSeconds").GetInt32());
        var patch=new{expectedInstanceRevision=revision,set=new{cpu=.5,memoryMiB=512,initializationScript="echo Instance"},clear=Array.Empty<string>()};
        var saved=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",patch);saved.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",patch)).StatusCode);
        var effective=await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v2/admin/agent-instances/{id}/sandbox");
        Assert.Equal(.5,effective.GetProperty("profile").GetProperty("cpu").GetDouble());Assert.Equal("echo Instance",effective.GetProperty("instanceInitializationScript").GetString());
        var current=effective.GetProperty("instanceRevision").GetInt64();
        var refused=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",new{expectedInstanceRevision=current,set=new{cpu=2},clear=Array.Empty<string>()});Assert.Equal(HttpStatusCode.BadRequest,refused.StatusCode);
        var verification=await owner.PostAsJsonAsync($"/api/v2/admin/agent-instances/{id}/sandbox/verify",new{expectedInstanceRevision=current});verification.EnsureSuccessStatusCode();
        var observation=await verification.Content.ReadFromJsonAsync<SandboxSetupVerificationResponse>();Assert.False(observation!.Passed);Assert.Equal(current,observation.InstanceRevision);
        using var anonymous=factory.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync($"/api/v2/admin/agent-instances/{id}/sandbox/verify",new{expectedInstanceRevision=current})).StatusCode);
        var cleared=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",new{expectedInstanceRevision=current,set=new{},clear=new[]{"cpu","memoryMiB","initializationScript"}});cleared.EnsureSuccessStatusCode();
        effective=await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v2/admin/agent-instances/{id}/sandbox");Assert.Equal(1,effective.GetProperty("profile").GetProperty("cpu").GetDouble());
        current=effective.GetProperty("instanceRevision").GetInt64();
        var reduced=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/sandboxPolicy",new{expectedInstanceRevision=current,set=new{cpu=.5},clear=Array.Empty<string>()});reduced.EnsureSuccessStatusCode();
        current=(await reduced.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("instanceRevision").GetInt64();
        var store=factory.Services.GetRequiredService<ISandboxStore>();var profile=(await store.GetProfileAsync("general-workbench"))!;
        await store.SaveProfileAsync(profile with {Revision=2,Cpu=.25},1);
        var independent=await owner.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{id}/settings/instructions",new{expectedInstanceRevision=current,set=new{systemInstructions="Independent instructions remain writable after a Global ceiling change."},clear=Array.Empty<string>()});independent.EnsureSuccessStatusCode();
        effective=await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v2/admin/agent-instances/{id}/sandbox");Assert.Equal(.25,effective.GetProperty("profile").GetProperty("cpu").GetDouble());
    }

    [Fact]
    public async Task Shipped_host_enables_sandbox_but_missing_private_configuration_remains_unavailable()
    {
        await using var factory = new UnconfiguredSandboxFactory();
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.True(factory.Services.GetRequiredService<OpenSandboxOptions>().Enabled);
        var platform = (await owner.GetFromJsonAsync<SandboxPlatformResponse>("/api/v2/admin/sandboxes"))!;
        Assert.Equal("Unavailable", platform.State);
        Assert.Empty(platform.Profiles);
    }

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
        var observation = (await verification.Content.ReadFromJsonAsync<SandboxVerificationResponse>())!;
        Assert.False(observation.Passed); Assert.Equal(2, observation.Profile.Revision);
        Assert.Contains("disabled", observation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(observation.Profile, await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/workbench"));
        Assert.Equal(2, (await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/workbench?revision=2"))!.Revision);
    }

    private sealed class SandboxFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<OpenSandboxOptions>(); s.AddSingleton(new OpenSandboxOptions { AllowLocalImageIds=true }); });
        }
    }

    private sealed class UnconfiguredSandboxFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
            new Dictionary<string, string?>
            {
                ["OpenSandbox:ApiKey"] = ""
            };
    }
}
