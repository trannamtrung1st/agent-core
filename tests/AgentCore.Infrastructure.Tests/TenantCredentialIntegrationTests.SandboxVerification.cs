using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Identity;
using AgentCore.Domain.Credentials;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private sealed class ScriptedSandboxVerifier(string scope, TimeProvider clock) : ITenantSandboxVerifier
    {
        public string HostScope => scope;
        public int Calls { get; private set; }
        public int CleanupCalls { get; private set; }
        public bool Clean { get; set; } = true;
        public Func<Task>? During { get; set; }
        public Func<SandboxVerificationOutcome, SandboxVerificationOutcome>? Transform { get; set; }
        public async ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct = default)
        {
            Calls++; if (During is { } work) await work();
            var result = new SandboxVerificationOutcome(new(profile.Id, profile.Revision, clock.GetUtcNow(), true, "Synthetic compatibility evidence", Stdout: "synthetic bounded initialization output",
                ContractVersion: SandboxProfileVerification.CurrentContractVersion, RegistryCredentialId: attempt.RegistryCredentialId,
                RegistryCredentialRevision: attempt.RegistryCredentialRevision, RegistrySecretRevision: attempt.RegistrySecretRevision,
                TenantId: attempt.TenantId, IncludesInitialization: !attempt.ImageOnly, HostScope: attempt.HostScope), Clean);
            return Transform?.Invoke(result) ?? result;
        }
        public ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default) { CleanupCalls++; return ValueTask.FromResult(Clean); }
    }
    private static (TenantSandboxProfileService Profiles, TenantSandboxVerificationService Service, SandboxImageAuthority Images, OpenSandboxOptions Options) VerificationServices(Fixture f, ITenantSandboxVerifier? verifier = null, OpenSandboxOptions? options = null)
    {
        options ??= new() { CanonicalWorkspaceRoot = Path.Combine(f.Root, "working"), WorkspaceDockerHostRoot = Path.Combine(f.Root, "working"), ApiKey = "synthetic-only-key" };
        var store = (ITenantSandboxVerificationStore)f.Iam; var profileStore = (ITenantSandboxProfileStore)f.Iam;
        var guard = new TenantSandboxImageAuthorization(f.Iam, profileStore, f.Identity, f.Authorizer); var host = new SandboxVerificationHost(options);
        var images = new SandboxImageAuthority(options, f.Sandboxes, f.Clock, f.Legacy, f.Protector, f.Credentials, guard, store, host);
        var profiles = new TenantSandboxProfileService(f.Iam, profileStore, f.Identity, f.Authorizer, images, f.Ids, f.Clock, store);
        return (profiles, new(f.Iam, store, profiles, guard, images, verifier ?? new ScriptedSandboxVerifier(host.Scope, f.Clock), f.Identity, f.Authorizer, f.Ids, f.Clock), images, options);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_full_evidence_is_owned_durable_and_distinct_from_image_only(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider());
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var v = VerificationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var image = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1, imageOnly: true);
        Assert.Equal(SandboxVerificationStatus.Succeeded, image.Attempt!.Status); Assert.True(image.Attempt.ImageOnly);
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        Assert.False((await v.Images.InspectAsync(p)).Eligible);
        var full = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(SandboxVerificationStatus.Succeeded, full.Attempt!.Status); Assert.False(full.Attempt.ImageOnly);
        Assert.True((await v.Images.InspectAsync(p)).Eligible); Assert.Equal(1, (await v.Profiles.ReadAsync(owner.TenantId, p.Id, human)).Profile.Revision);
        Assert.Null(await f.Sandboxes.GetVerificationAsync(p.Id, 1));
        var latest = await v.Service.ReadAsync(owner.TenantId, p.Id, human); Assert.Equal(full.Attempt.AttemptId, latest.Attempt!.AttemptId);
        await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1, imageOnly: true);
        Assert.Equal(full.Attempt.Result, (await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        Assert.True((await v.Images.InspectAsync(p)).Eligible);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            Assert.True((await reopened.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation!.Passed);
            Assert.NotNull(await reopened.FindSandboxVerificationAsync(owner.TenantId, p.Id, full.Attempt.AttemptId));
        }
        v.Options.WorkspaceDockerHostRoot += "-changed"; Assert.False((await v.Images.InspectAsync(p)).Eligible);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "synthetic bounded initialization output");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_retired_original_human_keeps_facts_but_cannot_promote_or_release_output(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var seed = VerificationServices(f);
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(seed.Options).Scope, f.Clock);
        var v = VerificationServices(f, runner, seed.Options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var captured = human;
        runner.During = async () => { await f.Identity.SignOutAsync(captured.AuthenticationSessionId); _ = await SandboxHuman(f, owner.OwnerPrincipalId); };
        var revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Service.VerifyAsync(owner.TenantId, p.Id, human, revision, 1).AsTask());
        var receipt = (await ((ITenantSandboxVerificationStore)f.Iam).FindSandboxVerificationAsync(owner.TenantId, p.Id))!;
        Assert.Equal(SandboxVerificationStatus.Retired, receipt.Status); Assert.True(receipt.Result!.Passed); Assert.False(receipt.Active);
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        runner.During = null; human = await SandboxHuman(f, owner.OwnerPrincipalId);
        Assert.Equal(receipt.AttemptId, (await v.Service.ReadAsync(owner.TenantId, p.Id, human)).Attempt!.AttemptId);
        Assert.Equal(SandboxVerificationStatus.Succeeded, (await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1)).Attempt!.Status); Assert.Equal(2, runner.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_active_claim_and_expired_cleanup_never_replay_scripts_or_accept_late_completion(bool sqlite)
    {
        var clock = new FakeTimeProvider(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var seed = VerificationServices(f);
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(seed.Options).Scope, f.Clock);
        var v = VerificationServices(f, runner, seed.Options); var store = (ITenantSandboxVerificationStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.During = () => { entered.TrySetResult(); return release.Task; };
        var first = v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reservation = await Revision();
        var busy = await Assert.ThrowsAsync<SecurityConflictException>(() => v.Service.VerifyAsync(owner.TenantId, p.Id, human, reservation, 1).AsTask()); Assert.Equal("verification_in_progress", busy.ReasonCode);
        Assert.Equal(1, runner.Calls); Assert.True((await store.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Pending);
        clock.Advance(TenantSandboxVerificationRules.MaximumLease + TimeSpan.FromSeconds(1));
        var claim = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(clock.GetUtcNow(), f.Ids.NewId()));
        release.TrySetResult(); Assert.Equal(SandboxVerificationStatus.CleanupPending, (await first).Attempt!.Status);
        Assert.Null((await store.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim with { ClaimId = f.Ids.NewId() }, true, clock.GetUtcNow()));
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim with { HostScope = new string('a', 64) }, true, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(46));
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim, true, clock.GetUtcNow()));
        var renewed = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(clock.GetUtcNow(), f.Ids.NewId()));
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim, true, clock.GetUtcNow()));
        claim = renewed;
        Assert.True(await store.FinishSandboxVerificationCleanupAsync(claim, true, clock.GetUtcNow()));
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim, true, clock.GetUtcNow()));
        Assert.Equal(1, runner.Calls); runner.During = null;
        Assert.Equal(SandboxVerificationStatus.Succeeded, (await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1)).Attempt!.Status); Assert.Equal(2, runner.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_expired_lease_cannot_promote_even_before_cleanup_claim(bool sqlite)
    {
        var clock = new FakeTimeProvider(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var seed = VerificationServices(f);
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(seed.Options).Scope, f.Clock)
        { During = () => { clock.Advance(TenantSandboxVerificationRules.MaximumLease + TimeSpan.FromSeconds(1)); return Task.CompletedTask; } };
        var v = VerificationServices(f, runner, seed.Options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var expired = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(SandboxVerificationStatus.Retired, expired.Attempt!.Status); Assert.True(expired.Attempt.Result!.Passed);
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        Assert.False((await v.Images.InspectAsync(p)).Eligible);
        runner.During = null;
        Assert.Equal(SandboxVerificationStatus.Succeeded, (await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1)).Attempt!.Status);
        Assert.Equal(2, runner.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_credential_epoch_change_retires_captured_evidence_and_fresh_verification_recovers(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var seed = VerificationServices(f);
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(seed.Options).Scope, f.Clock); var v = VerificationServices(f, runner, seed.Options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(human.PrincipalId, owner.TenantId, new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry,
            null, null, "verification-private-value", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"])), await Revision());
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput() with { RegistryCredentialId = credential.Credential.CredentialId }, await Revision(), 0)).Profile;
        runner.During = async () => { await f.Service.ReplaceAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, "verification-rotated-value", await Revision(), 1); };
        var retired = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(SandboxVerificationStatus.Retired, retired.Attempt!.Status); Assert.True(retired.Attempt.Result!.Passed);
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        runner.During = null; var recovered = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(SandboxVerificationStatus.Succeeded, recovered.Attempt!.Status); Assert.Equal(1, recovered.Attempt.Result!.RegistrySecretRevision);
        Assert.True((await v.Images.InspectAsync(p)).Eligible); Assert.Equal(2, runner.Calls);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "verification-private-value", "verification-rotated-value", "synthetic bounded initialization output");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_mismatched_evidence_is_not_persisted_and_cleanup_does_not_rerun_initialization(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var seed = VerificationServices(f);
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(seed.Options).Scope, f.Clock)
        { Transform = r => r with { Result = r.Result with { TenantId = Guid.NewGuid(), Stdout = "foreign-private-evidence" } } };
        var v = VerificationServices(f, runner, seed.Options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var pending = await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1);
        Assert.Equal(SandboxVerificationStatus.CleanupPending, pending.Attempt!.Status); Assert.DoesNotContain("foreign-private-evidence", System.Text.Json.JsonSerializer.Serialize(pending));
        Assert.Null((await ((ITenantSandboxVerificationStore)f.Iam).ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        await new TenantSandboxVerificationCleanup((ITenantSandboxVerificationStore)f.Iam, runner, f.Ids, f.Clock).RunPassAsync();
        Assert.Equal(1, runner.Calls); Assert.Equal(1, runner.CleanupCalls);
        Assert.Equal(SandboxVerificationStatus.Retired, (await v.Service.ReadAsync(owner.TenantId, p.Id, human)).Attempt!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_real_SDK_lost_create_cleans_exact_owner_after_original_session_retires(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var store = (ITenantSandboxVerificationStore)f.Iam;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
        var creates = 0; var lists = 0; string? profileId = null; Guid? dispatched = null;
        server.Run(async http =>
        {
            Assert.Equal("synthetic-only-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
            var attempt = (await store.FindSandboxVerificationAsync(owner.TenantId, profileId!))!;
            var metadataOwner = owner.TenantId.ToString("N") + "." + attempt.AttemptId.ToString("N");
            if (http.Request.Method == "POST")
            {
                creates++; dispatched = attempt.AttemptId;
                using var body = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
                Assert.Equal(metadataOwner, body.RootElement.GetProperty("metadata").GetProperty("agent-core-image-verification").GetString());
                await f.Identity.SignOutAsync(human.AuthenticationSessionId);
                http.Response.StatusCode = 500; await http.Response.WriteAsync("private-upstream-sentinel");
            }
            else
            {
                Assert.Equal("GET", http.Request.Method); lists++;
                Assert.Contains(metadataOwner, Uri.UnescapeDataString(http.Request.QueryString.Value!));
                await http.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), pagination = new { page = 1, pageSize = 100, totalItems = 0, totalPages = 0 } });
            }
        });
        await server.StartAsync();
        var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true, CanonicalWorkspaceRoot = Path.Combine(f.Root, "working"),
            WorkspaceDockerHostRoot = Path.Combine(f.Root, "working"), Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" };
        var seed = VerificationServices(f, options: options);
        var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer);
        var real = new OpenSandboxService(options, f.Sandboxes, null!, f.Memory, new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource()),
            f.Clock, imageAuthority: seed.Images, tenantVerifications: store, verificationAuthority: guard);
        var v = VerificationServices(f, real, options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput() with { ImageReference = "sha256:" + new string('a', 64) }, await Revision(), 0)).Profile;
        profileId = p.Id; var revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Service.VerifyAsync(owner.TenantId, p.Id, human, revision, 1).AsTask());
        var receipt = (await store.FindSandboxVerificationAsync(owner.TenantId, p.Id))!;
        Assert.Equal(dispatched, receipt.AttemptId); Assert.Equal(1, creates); Assert.Equal(1, lists);
        Assert.Equal(SandboxVerificationStatus.Retired, receipt.Status); Assert.False(receipt.Active); Assert.False(receipt.Result!.Passed);
        Assert.DoesNotContain("private-upstream-sentinel", System.Text.Json.JsonSerializer.Serialize(receipt));
        Assert.Null((await store.ReadSandboxVerificationAsync(owner.TenantId, p.Id, 1)).Observation);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(options.CanonicalWorkspaceRoot, ".verification")));
        human = await SandboxHuman(f, owner.OwnerPrincipalId);
        Assert.Equal(receipt.AttemptId, (await v.Service.ReadAsync(owner.TenantId, p.Id, human)).Attempt!.AttemptId);
        await server.StopAsync();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_verification_real_SDK_cleanup_rechecks_claim_and_purges_only_its_disposable_owner(bool sqlite)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0"); var calls = 0;
        server.Run(async http => { calls++; Assert.Equal("GET", http.Request.Method); Assert.Equal("synthetic-only-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
            await http.Response.WriteAsJsonAsync(new { items = Array.Empty<object>(), pagination = new { page = 1, pageSize = 100, totalItems = 0, totalPages = 0 } }); });
        await server.StartAsync(); var clock = new FakeTimeProvider(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha"); var human = await SandboxHuman(f, owner.OwnerPrincipalId);
        var options = new OpenSandboxOptions { CanonicalWorkspaceRoot = Path.Combine(f.Root, "working"), WorkspaceDockerHostRoot = Path.Combine(f.Root, "working"), Endpoint = server.Urls.Single(), ApiKey = "synthetic-only-key" };
        var runner = new ScriptedSandboxVerifier(new SandboxVerificationHost(options).Scope, f.Clock) { Clean = false }; var v = VerificationServices(f, runner, options);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var p = (await v.Profiles.SaveAsync(owner.TenantId, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var attempt = (await v.Service.VerifyAsync(owner.TenantId, p.Id, human, await Revision(), 1)).Attempt!; Assert.Equal(SandboxVerificationStatus.CleanupPending, attempt.Status);
        var mounts = new CanonicalSandboxMountResolver(options).Verification(attempt.AttemptId); var sibling = new CanonicalSandboxMountResolver(options).Verification(f.Ids.NewId());
        var store = (ITenantSandboxVerificationStore)f.Iam; var claim = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(clock.GetUtcNow(), f.Ids.NewId()));
        var files = new FileSessionWorkspace(options.CanonicalWorkspaceRoot, Path.Combine(f.Root, "templates"), sessions: f.Memory, instances: f.Instances, sandboxes: f.Sandboxes);
        var real = new OpenSandboxService(options, f.Sandboxes, files, f.Memory, new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource()), f.Clock, tenantVerifications: store);
        Assert.False(await real.CleanupAsync(claim with { TenantId = f.Ids.NewId() })); Assert.Equal(0, calls);
        Assert.False(await real.CleanupAsync(claim with { ClaimId = f.Ids.NewId() })); Assert.Equal(0, calls); Assert.True(Directory.Exists(mounts.VerificationRoot));
        Assert.False(await real.CleanupAsync(claim with { HostScope = new string('a', 64) })); Assert.Equal(0, calls);
        await f.Identity.SignOutAsync(human.AuthenticationSessionId);
        Assert.True(await real.CleanupAsync(claim)); Assert.Equal(1, calls); Assert.False(Directory.Exists(mounts.VerificationRoot)); Assert.True(Directory.Exists(sibling.VerificationRoot));
        Assert.True(await store.FinishSandboxVerificationCleanupAsync(claim, true, clock.GetUtcNow())); Assert.Equal(1, runner.Calls);
        await server.StopAsync();
    }
}
