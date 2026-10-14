using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class OciSandboxAuthorityTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [Theory]
    [InlineData("ghcr.io/acme/tool", true)]
    [InlineData("ghcr.io/acme", true)]
    [InlineData("ghcr.io/acme-evil/tool", false)]
    [InlineData("ghcr.io.evil/acme/tool", false)]
    [InlineData("ghcr.io:443/acme/tool", false)]
    public void Repository_policy_matches_complete_host_and_path_segments(string source, bool allowed)
    {
        var options = new OpenSandboxOptions { AllowedImagePrefixes = ["ghcr.io/acme/"] };
        var profile = new SandboxProfile("fixture", 1, "Fixture", "", true, source + "@sha256:" + Digest);
        if (allowed) options.ValidateProfile(profile); else Assert.Throws<ArgumentException>(() => options.ValidateProfile(profile));
    }
    [Theory]
    [InlineData("ghcr.io/acme/tool:latest")]
    [InlineData("acme/tool@sha256:")]
    [InlineData("ghcr.io@sha256:")]
    [InlineData("https://user:password@ghcr.io/acme/tool@sha256:")]
    [InlineData("ghcr.io:00080/acme/tool@sha256:")]
    [InlineData("ghcr.io:65536/acme/tool@sha256:")]
    [InlineData("ghcr.io/acme/../tool@sha256:")]
    [InlineData("ghcr.io/acme//tool@sha256:")]
    [InlineData("ghcr.io/acme/tool#fragment@sha256:")]
    [InlineData("ghcr.io/acme/tool?token=secret@sha256:")]
    [InlineData("ghcr.io/acme/тool@sha256:")]
    [InlineData("ghcr.io/acme/tool\n@sha256:")]
    public void Hostile_or_mutable_sources_are_rejected(string source) =>
        Assert.Throws<ArgumentException>(() => OciImageReference.Parse(source + (source.EndsWith(':') ? Digest : "")));

    [Fact]
    public void Invalid_host_policy_fails_configuration_and_local_ids_require_explicit_host_gate()
    {
        Assert.Throws<ArgumentException>(() => new OpenSandboxOptions { AllowedImagePrefixes = ["ghcr.io/acme-*/"] }.ValidatePolicy());
        var profile = new SandboxProfile("fixture", 1, "Fixture", "", true, "sha256:" + Digest);
        Assert.Throws<ArgumentException>(() => new OpenSandboxOptions().ValidateProfile(profile));
        new OpenSandboxOptions { AllowLocalImageIds = true }.ValidateProfile(profile);
        Assert.True(OciImageReference.Parse(profile.ImageReference).IsLocal);
        Assert.False(OciImageReference.Parse("docker.io/acme/tool@sha256:" + Digest).IsLocal);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Protected_registry_scope_rotation_disable_delete_and_verification_epoch_survive_store_reopen(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "oci-authority-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var owners = new InMemoryAgentInstanceStore();
            var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source="+Path.Combine(root,"core.db")).Options);
            ICredentialStore store; IAgentCredentialBindingStore bindings;
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); var adapter = new SqliteCredentialStore(factory); store=adapter; bindings=adapter; }
            else { var adapter = new InMemoryCredentialStore(owners); store=adapter; bindings=adapter; }
            var protector = new LocalCredentialProtector(Path.Combine(root,"keys"));
            var service = new CredentialService(store,bindings,protector,owners,new SystemIdGenerator(TimeProvider.System),TimeProvider.System);
            var saved = await service.CreateContainerRegistryAsync("Private OCI","Token",new("ghcr.io","owner",["ghcr.io/acme"]),"private-oci-sentinel");
            Assert.DoesNotContain("private-oci-sentinel",(await store.GetAsync(saved.CredentialId))!.ProtectedPayload);
            var profiles = new InMemorySandboxStore();
            var profile = new SandboxProfile("fixture",1,"Fixture","",true,"ghcr.io/acme/tool@sha256:"+Digest,RegistryCredentialId:saved.CredentialId);
            await profiles.SaveProfileAsync(profile,0);
            var options = new OpenSandboxOptions { AllowedImagePrefixes=["ghcr.io/acme", "ghcr.io/acme-evil"] };
            var authority = new SandboxImageAuthority(options,profiles,TimeProvider.System,store,protector);
            Assert.Equal("SourceUnavailable",(await authority.InspectAsync(profile with {ImageReference="ghcr.io/acme-evil/tool@sha256:"+Digest})).Status);
            Assert.Equal("NeedsVerification",(await authority.InspectAsync(profile)).Status);
            await profiles.SaveVerificationAsync(new(profile.Id,1,DateTimeOffset.UtcNow,true,"Historical observation"));
            Assert.False((await authority.InspectAsync(profile)).Eligible);
            await profiles.SaveVerificationAsync(new(profile.Id,1,DateTimeOffset.UtcNow,true,"Current observation",ContractVersion:1,RegistryCredentialId:saved.CredentialId,RegistryCredentialRevision:saved.Revision,RegistrySecretRevision:0));
            Assert.True((await authority.InspectAsync(profile)).Eligible);
            var pinned = profile with {RegistryCredentialRevision=1,RegistrySecretRevision=0};
            Assert.Equal("private-oci-sentinel",(await authority.ResolveAuthAsync(pinned,CancellationToken.None,true))!.Password);
            saved = await service.ReplaceAsync(saved.CredentialId,saved.Revision,"rotated-oci-sentinel");
            Assert.Equal(1,(await store.GetAsync(saved.CredentialId))!.SecretRevision);
            Assert.Equal("NeedsVerification",(await authority.InspectAsync(profile)).Status);
            await Assert.ThrowsAsync<AgentCoreException>(()=>authority.ResolveAuthAsync(pinned,CancellationToken.None,true).AsTask());
            saved = await service.UpdateContainerRegistryAsync(saved.CredentialId,saved.Revision,saved.DisplayName,"Active",new("ghcr.io","owner",["ghcr.io/acme/other"]));
            Assert.Equal("SourceUnavailable",(await authority.InspectAsync(profile)).Status);
            saved = await service.UpdateContainerRegistryAsync(saved.CredentialId,saved.Revision,saved.DisplayName,"Active",new("ghcr.io","owner",["ghcr.io/acme"],DateTimeOffset.UnixEpoch));
            Assert.Equal("SourceUnavailable",(await authority.InspectAsync(profile)).Status);
            saved = await service.UpdateAsync(saved.CredentialId,saved.Revision,saved.DisplayName,"Disabled",saved.Metadata,[]);
            Assert.Equal("SourceUnavailable",(await authority.InspectAsync(profile)).Status);
            if (sqlite) Assert.Equal(saved.ContainerRegistry!.Registry,(await new SqliteCredentialStore(factory).GetAsync(saved.CredentialId))!.ContainerRegistry!.Registry);
            await service.DeleteAsync(saved.CredentialId,saved.Revision);
            Assert.Equal("SourceUnavailable",(await authority.InspectAsync(profile)).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
