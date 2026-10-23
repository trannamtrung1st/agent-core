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
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0", "mcr.microsoft.com/dotnet/sdk", "10.0")]
    [InlineData("ubuntu:24.04", "docker.io/library/ubuntu", "24.04")]
    [InlineData("my.image:v1", "docker.io/library/my.image", "v1")]
    [InlineData("my.image", "docker.io/library/my.image", "latest")]
    [InlineData("library/busybox", "docker.io/library/busybox", "latest")]
    [InlineData("acme/tool:v1", "docker.io/acme/tool", "v1")]
    [InlineData("docker.io/ubuntu", "docker.io/library/ubuntu", "latest")]
    [InlineData("localhost:5000/acme/tool:V1", "localhost:5000/acme/tool", "V1")]
    [InlineData(" ghcr.io/acme/tool:latest ", "ghcr.io/acme/tool", "latest")]
    public void Onboarding_names_normalize_without_weakening_immutable_runtime_references(string input, string path, string tag)
    {
        var source = OciImageSource.Parse(input);
        Assert.Equal(path + "@sha256:" + new string('0', 64), source.Image.Value);
        Assert.Equal(tag, source.Tag);
        Assert.Throws<ArgumentException>(() => OciImageReference.Parse(input));
    }

    [Theory]
    [InlineData("ubuntu:")] [InlineData("ubuntu:bad/tag")]
    [InlineData("https://mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("ghcr.io/acme/../tool:latest")] [InlineData("ghcr.io/acme/tool:tag?token=secret")]
    [InlineData("ghcr.io/acme/tool:tag\nsecret")] [InlineData("sha256:invalid")]
    public void Invalid_onboarding_sources_are_rejected(string input) => Assert.Throws<ArgumentException>(() => OciImageSource.Parse(input));

    [Fact]
    public void Onboarding_digests_and_local_ids_remain_pinned()
    {
        Assert.Equal("docker.io/library/ubuntu@sha256:" + Digest, OciImageSource.Parse("ubuntu@sha256:" + Digest).Image.Value);
        var local = OciImageSource.Parse("sha256:" + Digest);
        Assert.Null(local.Tag); Assert.True(local.Image.IsLocal);
    }

    [Theory]
    [InlineData("docker.io/library/ubuntu")]
    [InlineData("ghcr.io/astral-sh/uv")]
    [InlineData("mcr.microsoft.com/dotnet/sdk")]
    [InlineData("quay.io/prometheus/prometheus")]
    [InlineData("public.ecr.aws/docker/library/busybox")]
    public void Default_policy_allows_common_public_registries(string path)
    {
        var options = new OpenSandboxOptions(); options.ValidatePolicy();
        options.ValidateProfile(new("public", 1, "Public", "", true, path + "@sha256:" + Digest));
        Assert.Throws<ArgumentException>(() => options.ValidateProfile(new("foreign", 1, "Foreign", "", true, "mcr.microsoft.com.evil/tool@sha256:" + Digest)));
    }

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
    public async Task Unowned_registry_records_cannot_resolve_source_reveal_secrets_or_start_native_verification(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "retired-registry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "core.db")).Options);
            ICredentialStore store;
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); store = new SqliteCredentialStore(factory); }
            else store = new InMemoryCredentialStore(new InMemoryAgentInstanceStore());
            var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var protector = new LocalCredentialProtector(Path.Combine(root, "keys"));
            var credential = new Credential(id, "Retained unowned fixture", CredentialKind.Token, CredentialStatus.Active,
                CredentialRules.Metadata(null), [], protector.Protect(id, 1, "retained-private-oci-sentinel"), 1, 1, now, now,
                CredentialPurpose.ContainerRegistry, ContainerRegistry: new("ghcr.io", "owner", ["ghcr.io/acme"]));
            await store.SaveAsync(credential, 0);
            var profiles = new InMemorySandboxStore();
            var profile = new SandboxProfile("retired", 1, "Retired", "", true, "ghcr.io/acme/tool@sha256:" + Digest, RegistryCredentialId: id);
            await profiles.SaveProfileAsync(profile, 0);
            await profiles.SaveVerificationAsync(new(profile.Id, 1, now, true, "Retained observation",
                ContractVersion: SandboxProfileVerification.CurrentContractVersion, RegistryCredentialId: id,
                RegistryCredentialRevision: 1, RegistrySecretRevision: 0));
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = "http://127.0.0.1:1", ApiKey = "unused", AllowedImagePrefixes = ["ghcr.io/acme"] };
            var deniedProtector = new RetiredRegistryProtector();
            var authority = new SandboxImageAuthority(options, profiles, TimeProvider.System, deniedProtector);
            Assert.Equal("SourceUnavailable", (await authority.InspectAsync(profile)).Status);
            foreach (var image in new[] { profile.ImageReference, "ghcr.io/acme/tool:latest" })
                await Assert.ThrowsAsync<AgentCoreException>(() => authority.ResolveAsync(profile with { ImageReference = image }).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => authority.ResolveAuthAsync(profile, CancellationToken.None, false).AsTask());
            var adapter = new OpenSandboxService(options, profiles, null!, null!, TimeProvider.System, imageAuthority: authority);
            var result = await adapter.VerifyProfileAsync(profile, CancellationToken.None);
            Assert.False(result.Passed); Assert.Equal("sourceAuthority", result.FailureCode);
            Assert.DoesNotContain("sentinel", result.Message); Assert.Equal(0, deniedProtector.Calls);
            // No network service exists at Endpoint. Source refusal precedes all disposable host and registry I/O.
            if (sqlite) store = new SqliteCredentialStore(factory);
            var retained = Assert.IsType<Credential>(await store.GetAsync(id));
            Assert.Equal(credential.ProtectedPayload, retained.ProtectedPayload); Assert.Equal(1, retained.Revision); Assert.Equal(0, retained.SecretRevision);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class RetiredRegistryProtector : ICredentialProtector
    {
        public int Calls { get; private set; }
        public string Protect(Guid id, int version, string value) => throw new InvalidOperationException();
        public string Unprotect(Guid id, int version, string payload) { Calls++; throw new InvalidOperationException("Unowned source cannot release a secret."); }
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
