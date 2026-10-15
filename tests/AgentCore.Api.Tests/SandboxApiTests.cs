using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class SandboxApiTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Explicit_empty_registry_policy_does_not_restore_public_defaults(string configured)
    {
        await using var factory = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"OpenSandbox\":{\"AllowedImagePrefixes\":" + configured + "}}")))));
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var platform = (await owner.GetFromJsonAsync<SandboxPlatformResponse>("/api/v2/admin/sandboxes"))!;
        Assert.Empty(platform.AllowedImagePrefixes);
        var request = new SaveSandboxProfileRequest(0, "Blocked", "", true, "docker.io/library/busybox@sha256:" + new string('a', 64));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/blocked", request)).StatusCode);
    }

    [Fact]
    public async Task Dotted_docker_hub_short_digest_is_saved_with_the_same_host_as_credential_selection()
    {
        await using var factory = new SandboxFactory(); using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var digest = "sha256:" + new string('a', 64);
        var request = new SaveSandboxProfileRequest(0, "Dotted image", "", true, "my.image@" + digest);
        var response = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotted-image", request);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<SandboxProfileResponse>())!;
        Assert.Equal("docker.io/library/my.image@" + digest, saved.ImageReference);
        Assert.Equal(saved, await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/dotted-image"));
    }

    [Fact]
    public async Task Configured_registry_allowlist_replaces_public_defaults_and_rejects_other_sources()
    {
        await using var factory = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenSandbox:AllowedImagePrefixes:0"] = "mcr.microsoft.com/dotnet" })));
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var platform = (await owner.GetFromJsonAsync<SandboxPlatformResponse>("/api/v2/admin/sandboxes"))!;
        Assert.Equal(["mcr.microsoft.com/dotnet"], platform.AllowedImagePrefixes);
        var request = new SaveSandboxProfileRequest(0, "Restricted", "", true, "docker.io/library/busybox@sha256:" + new string('a', 64));
        var forbidden = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/restricted", request);
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
        Assert.Contains("not allowed by host policy", await forbidden.Content.ReadAsStringAsync());
        (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/restricted", request with { ImageReference = "mcr.microsoft.com/dotnet/sdk@sha256:" + new string('a', 64) })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Tagged_images_resolve_before_save_and_failed_resolution_preserves_the_saved_revision()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        await using var registry = builder.Build(); registry.Urls.Add("http://127.0.0.1:0");
        var calls = 0; var digest = new string('a', 64);
        registry.MapMethods("/v2/dotnet/sdk/manifests/{tag}", ["HEAD"], (Microsoft.AspNetCore.Http.HttpContext context, string tag) =>
        {
            calls++;
            if (tag != "10.0") { context.Response.StatusCode = 404; return; }
            context.Response.Headers["Docker-Content-Digest"] = "sha256:" + digest;
        });
        await registry.StartAsync();
        var host = new Uri(registry.Urls.Single()).Authority;
        await using var factory = new SandboxFactory(new OpenSandboxOptions { AllowedImagePrefixes = [host + "/dotnet"], InsecureRegistryHosts = [host] });
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var request = new SaveSandboxProfileRequest(0, ".NET Workbench", ".NET SDK workbench", true, host + "/dotnet/sdk:10.0");
        var response = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotnet-workbench", request);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<SandboxProfileResponse>())!;
        Assert.Equal(host + "/dotnet/sdk@sha256:" + digest, saved.ImageReference);
        Assert.Equal("NeedsVerification", saved.Availability); Assert.Equal(1, calls);
        digest = new string('b', 64);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotnet-workbench", request)).StatusCode);
        Assert.Equal(1, calls);
        response = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotnet-workbench", request with { ExpectedRevision = 1 });
        response.EnsureSuccessStatusCode(); Assert.Equal(2, calls);
        var missing = await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotnet-workbench", request with { ExpectedRevision = 2, ImageReference = host + "/dotnet/sdk:missing" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("Image tag could not be resolved", await missing.Content.ReadAsStringAsync());
        var readback = (await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/dotnet-workbench"))!;
        Assert.Equal(2, readback.Revision); Assert.EndsWith(digest, readback.ImageReference);
        var history = (await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/dotnet-workbench?revision=1"))!;
        Assert.Equal(saved.ImageReference, history.ImageReference);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/dotnet-workbench", request with { ExpectedRevision = 2, ImageReference = host + "/forbidden/sdk:10.0" })).StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Registry_expiry_can_be_renewed_and_cleared_with_revision_guards_without_exposing_the_secret()
    {
        await using var factory = new SandboxFactory(); using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var expired = new ContainerRegistryProfileDto("docker.io", "owner", [], DateTimeOffset.UnixEpoch);
        var created = await owner.PostAsJsonAsync("/api/v2/admin/container-registry-credentials",
            new AdminCreateContainerRegistryRequest("Expiring Hub", "Token", expired, "expiry-secret-sentinel"));
        created.EnsureSuccessStatusCode();
        var credential = (await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal(expired.ExpiresAtUtc, credential.ContainerRegistry!.ExpiresAtUtc);
        var renewed = expired with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) };
        var patch = new { expectedRevision = credential.Revision, displayName = credential.DisplayName, status = "Active", profile = renewed };
        var saved = await owner.PatchAsJsonAsync($"/api/v2/admin/container-registry-credentials/{credential.CredentialId}", patch);
        saved.EnsureSuccessStatusCode();
        Assert.DoesNotContain("expiry-secret-sentinel", await saved.Content.ReadAsStringAsync());
        var current = (await saved.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal(credential.Revision + 1, current.Revision);
        Assert.Equal(renewed.ExpiresAtUtc, current.ContainerRegistry!.ExpiresAtUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync($"/api/v2/admin/container-registry-credentials/{credential.CredentialId}", patch)).StatusCode);
        var cleared = await owner.PatchAsJsonAsync($"/api/v2/admin/container-registry-credentials/{credential.CredentialId}",
            new { expectedRevision = current.Revision, displayName = current.DisplayName, status = "Active", profile = renewed with { ExpiresAtUtc = null } });
        cleared.EnsureSuccessStatusCode();
        var readback = (await owner.GetFromJsonAsync<AdminCredentialResponse>($"/api/v2/admin/credentials/{credential.CredentialId}"))!;
        Assert.Null(readback.ContainerRegistry!.ExpiresAtUtc);
        Assert.Equal(current.Revision + 1, readback.Revision);
    }

    [Fact]
    public async Task Storage_edits_append_exact_byte_revisions_and_rejected_limits_preserve_the_saved_profile()
    {
        await using var factory = new SandboxFactory(); using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var request = new SaveSandboxProfileRequest(0, "Storage fixture", "", true, "sha256:" + new string('a',64), StorageBytes: 128 * 1024 * 1024 + 1);
        (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/storage", request)).EnsureSuccessStatusCode();
        var update = request with { ExpectedRevision = 1, StorageBytes = 64 * 1024 * 1024 };
        (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/storage", update)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/storage", update)).StatusCode);
        foreach (var invalid in new[] {
            update with { ExpectedRevision = 2, StorageBytes = 0 },
            update with { ExpectedRevision = 2, StorageBytes = 250L * 1024 * 1024 + 1 },
            update with { ExpectedRevision = 2, MaxLifetimeSeconds = 60 },
            update with { ExpectedRevision = 2, InitializationScript = new string('界', 5500) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/storage", invalid)).StatusCode);
        var readback = (await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/storage"))!;
        Assert.Equal(2, readback.Revision); Assert.Equal(update.StorageBytes, readback.StorageBytes);
        Assert.Equal(request.StorageBytes, (await owner.GetFromJsonAsync<SandboxProfileResponse>("/api/v2/admin/sandbox-profiles/storage?revision=1"))!.StorageBytes);
    }

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
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PutAsJsonAsync("/api/v2/admin/sandbox-profiles/workbench",request with { ExpectedRevision=1, ImageReference="busybox:invalid/tag" })).StatusCode);
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

    private sealed class SandboxFactory(OpenSandboxOptions? options = null) : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<OpenSandboxOptions>(); s.AddSingleton(options ?? new OpenSandboxOptions { AllowLocalImageIds=true }); });
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
