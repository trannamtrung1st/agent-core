using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static TenantSandboxInstanceVerificationService InstanceVerification(Fixture f,
        (TenantSandboxProfileService Profiles, TenantSandboxVerificationService Verify, AgentCore.Infrastructure.Sandbox.SandboxImageAuthority Images, SetupReadiness Ready, SetupVerifier Runner,
            AgentCore.Infrastructure.Sandbox.SandboxVerificationHost Host, AgentCore.Infrastructure.Sandbox.OpenSandboxOptions Options) p, ITenantSandboxVerifier? verifier = null, ISandboxImageAuthority? images = null)
    {
        var guard = new TenantSandboxImageAuthorization(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer, SetupResolver(f));
        var dispatch = verifier is null ? p.Verify : new TenantSandboxVerificationService(f.Iam, (ITenantSandboxVerificationStore)f.Iam, p.Profiles, guard,
            p.Images, verifier, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
        return new(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, (ITenantSandboxVerificationStore)f.Iam, SetupResolver(f), guard,
            images ?? p.Images, verifier ?? p.Runner, p.Host, p.Ready, dispatch, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_pins_saved_alternate_setup_and_never_replaces_reusable_evidence(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Default"), await Revision(), 0)).Profile;
        var b = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Alternate", .5) with { InitializationScript = "echo profile-b" }, await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(a.Id, [a.Id, b.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var changed = await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), 1,
            Fields(new { cpu = .4, initializationScript = "echo instance" }), [], initiator: human);
        var service = InstanceVerification(f, p); var result = await service.VerifyAsync(owner.TenantId, id, b.Id, human, await Revision(), 2, 1);
        Assert.Equal("Verified", result.Status); Assert.Equal(SandboxVerificationStatus.Succeeded, result.Attempt!.Status);
        Assert.Equal(id, result.Attempt.InstanceId); Assert.Equal(2, result.Attempt.InstanceRevision); Assert.True(result.Observation!.Passed);
        Assert.Equal(b.Id, p.Runner.LastProfile!.Id); Assert.Equal("echo profile-b", p.Runner.LastProfile.InitializationScript);
        Assert.Equal("echo instance", p.Runner.LastProfile.InstanceInitializationScript); Assert.Equal(.4, p.Runner.LastProfile.Cpu); Assert.Equal(2, p.Runner.LastProfile.InstanceRevision);
        Assert.Equal(1, p.Runner.Calls);
        var store = (ITenantSandboxVerificationStore)f.Iam;
        Assert.Null((await store.ReadSandboxVerificationAsync(owner.TenantId, b.Id, 1)).Observation); Assert.False((await p.Images.InspectAsync(b)).Eligible);
        Assert.Null((await p.Verify.ReadAsync(owner.TenantId, b.Id, human)).Attempt);
        await Assert.ThrowsAsync<AccessDeniedException>(() => p.Verify.ReadAsync(owner.TenantId, b.Id, human, result.Attempt.AttemptId).AsTask());
        var other = await ConfigurationServices(f).Agents.CreateFromPublicationAsync(human.PrincipalId, owner.TenantId, Guid.Parse(changed.Instance.DefinitionId), 1, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(owner.TenantId, other.Instance.InstanceId, b.Id, human, result.Attempt.AttemptId).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(id, (await reopened.FindSandboxVerificationAsync(owner.TenantId, b.Id, result.Attempt.AttemptId, instanceId: id))!.InstanceId);
            Assert.True((await reopened.ReadSandboxInstanceVerificationAsync(owner.TenantId, id, 2, b.Id, 1)).Observation!.Passed);
            Assert.Null(await reopened.FindSandboxVerificationAsync(owner.TenantId, b.Id, result.Attempt.AttemptId));
        }
        changed = await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), 2, Fields(new { cpu = .3 }), [], initiator: human);
        var stale = await service.ReadAsync(owner.TenantId, id, b.Id, human, result.Attempt.AttemptId);
        Assert.Equal("NeedsVerification", stale.Status); Assert.Null(stale.Observation); Assert.Equal(SandboxVerificationStatus.Succeeded, stale.Attempt!.Status);
        await p.Verify.VerifyAsync(owner.TenantId, b.Id, human, await Revision(), 1);
        Assert.True((await p.Images.InspectAsync(b)).Eligible); Assert.Equal("NeedsVerification", (await service.ReadAsync(owner.TenantId, id, b.Id, human)).Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_uses_agent_manage_and_profile_read_with_independent_credential_authority(bool sqlite)
    {
        var keyRoot = Path.Combine(Path.GetTempPath(), "instance-verification-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
            var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
            async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
            var credentials = new TenantCredentialService(f.Iam, (ITenantCredentialStore)f.Iam, f.Authorizer, new LocalCredentialProtector(keyRoot), f.Ids, TimeProvider.System);
            var credential = await credentials.CreateAsync(human.PrincipalId, owner.TenantId, new("Registry", CredentialKind.Token, CredentialPurpose.ContainerRegistry,
                null, null, "instance-verification-private", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"])), await Revision());
            var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Protected") with { RegistryCredentialId = credential.Credential.CredentialId }, await Revision(), 0)).Profile;
            var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(profile.Id, [profile.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
            var minimal = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Minimal", ["agents.chat"], await Revision());
            var account = await f.Identity.RegisterAsync("verifier@example.test", "Verifier", "strong-verifier-password");
            var invite = await f.Access.InviteAsync(human.PrincipalId, owner.TenantId, account.LoginKey, minimal.RoleId, await Revision());
            var member = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invite.Invitation.InvitationId, invite.Token);
            var actor = await SetupHuman(f, account.AccountId, member.PrincipalId, "verifier@example.test", "strong-verifier-password");
            var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Verify one Instance", ["agents.read", "sandbox.read", "sandbox.manage"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, role.RoleId, AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), await Revision());
            var read = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read one profile", ["sandbox.read"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, read.RoleId, TenantSandboxProfileRules.Scope(owner.TenantId, profile.Id), await Revision());
            var source = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Inspect and bind source", ["credentials.read", "credentials.bind"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, source.RoleId, TenantCredentialRules.Scope(owner.TenantId, credential.Credential.CredentialId), await Revision());
            var service = InstanceVerification(f, p); var before = await Revision();
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.VerifyAsync(owner.TenantId, id, profile.Id, actor, before, 1, 1).AsTask());
            Assert.Equal(before, await Revision()); Assert.Equal(0, p.Runner.Calls);
            Assert.False((await service.ReadAsync(owner.TenantId, id, profile.Id, actor)).CanVerify);
            var useRole = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Use selected source", ["credentials.use"], await Revision());
            var use = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, useRole.RoleId, TenantCredentialRules.Scope(owner.TenantId, credential.Credential.CredentialId), await Revision());
            Assert.False((await f.Authorizer.CheckAsync(actor.PrincipalId, "sandbox.manage", TenantSandboxProfileRules.Scope(owner.TenantId, profile.Id))).Allowed);
            Assert.Equal("Verified", (await service.VerifyAsync(owner.TenantId, id, profile.Id, actor, await Revision(), 1, 1)).Status);
            Assert.True((await service.ReadAsync(owner.TenantId, id, profile.Id, actor)).CanVerify);
            p.Runner.During = async () => { await credentials.ReplaceAsync(human.PrincipalId, owner.TenantId, credential.Credential.CredentialId, "instance-verification-rotated", await Revision(), 1); };
            var retired = await service.VerifyAsync(owner.TenantId, id, profile.Id, actor, await Revision(), 1, 1);
            Assert.Equal(SandboxVerificationStatus.Retired, retired.Attempt!.Status); Assert.Equal("NeedsVerification", retired.Status); Assert.Null(retired.Observation);
            p.Runner.During = null;
            var current = await service.VerifyAsync(owner.TenantId, id, profile.Id, actor, await Revision(), 1, 1);
            Assert.Equal("Verified", current.Status); Assert.Equal(1, current.Observation!.RegistrySecretRevision);
            await f.Access.RevokeAssignmentAsync(human.PrincipalId, owner.TenantId, use.AssignmentId, await Revision()); before = await Revision();
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.VerifyAsync(owner.TenantId, id, profile.Id, actor, 0, 0, 0).AsTask());
            Assert.Equal(before, await Revision()); Assert.Equal(3, p.Runner.Calls);
            var audit = System.Text.Json.JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(owner.TenantId, 200));
            foreach (var secret in new[] { "instance-verification-private", "instance-verification-rotated", "bounded instance output" }) Assert.DoesNotContain(secret, audit);
        }
        finally { if (Directory.Exists(keyRoot)) Directory.Delete(keyRoot, recursive: true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_active_owner_spans_profiles_and_revisions_and_late_result_cannot_promote(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("First"), await Revision(), 0)).Profile;
        var b = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Second"), await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(a.Id, [a.Id, b.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var service = InstanceVerification(f, p); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Runner.During = () => { entered.TrySetResult(); return release.Task; };
        var first = service.VerifyAsync(owner.TenantId, id, a.Id, human, await Revision(), 1, 1).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal("VerificationPending", (await service.ReadAsync(owner.TenantId, id, b.Id, human)).Status);
            if (sqlite)
            {
                await using var db = f.Contexts!.CreateDbContext();
                var active = (await ((ITenantSandboxVerificationStore)f.Iam).FindSandboxVerificationAsync(owner.TenantId, a.Id, instanceId: id))!;
                db.Add(TenantSandboxVerificationRecord.Row(active with { AttemptId = Guid.NewGuid(), ProfileId = b.Id }));
                var unique = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Contains("InstanceId", unique.InnerException!.Message);
            }
            var before = await Revision();
            var busy = await Assert.ThrowsAsync<SecurityConflictException>(() => service.VerifyAsync(owner.TenantId, id, b.Id, human, before, 1, 1).AsTask());
            Assert.Equal("verification_in_progress", busy.ReasonCode); Assert.Equal(1, p.Runner.Calls);
            await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { cpu = .5 }), [], initiator: human);
            before = await Revision();
            Assert.Equal("verification_in_progress", (await Assert.ThrowsAsync<SecurityConflictException>(() => service.VerifyAsync(owner.TenantId, id, b.Id, human, before, 2, 1).AsTask())).ReasonCode);
        }
        finally { release.TrySetResult(); }
        var late = await first; Assert.Equal(SandboxVerificationStatus.Retired, late.Attempt!.Status); Assert.Null(late.Observation);
        p.Runner.During = null;
        Assert.Equal("Verified", (await service.VerifyAsync(owner.TenantId, id, b.Id, human, await Revision(), 2, 1)).Status); Assert.Equal(.5, p.Runner.LastProfile!.Cpu); Assert.Equal(2, p.Runner.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_retired_human_and_hostile_evidence_keep_only_owned_cleanup_facts(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Retired"), await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(profile.Id, [profile.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var service = InstanceVerification(f, p); var store = (ITenantSandboxVerificationStore)f.Iam;
        p.Runner.During = async () => { await f.Identity.SignOutAsync(human.AuthenticationSessionId); _ = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); };
        var before = await Revision(); await Assert.ThrowsAsync<AccessDeniedException>(() => service.VerifyAsync(owner.TenantId, id, profile.Id, human, before, 1, 1).AsTask());
        var retired = await store.FindSandboxVerificationAsync(owner.TenantId, profile.Id, instanceId: id); Assert.Equal(SandboxVerificationStatus.Retired, retired!.Status);
        Assert.Null((await store.ReadSandboxInstanceVerificationAsync(owner.TenantId, id, 1, profile.Id, 1)).Observation);
        p.Runner.During = null; human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId);
        Assert.Equal(retired.AttemptId, (await service.ReadAsync(owner.TenantId, id, profile.Id, human)).Attempt!.AttemptId);
        p.Runner.Transform = r => r with { Result = r.Result with { InstanceId = Guid.NewGuid(), Stdout = "foreign-instance-output" } };
        var pending = await service.VerifyAsync(owner.TenantId, id, profile.Id, human, await Revision(), 1, 1);
        Assert.Equal(SandboxVerificationStatus.CleanupPending, pending.Attempt!.Status); Assert.DoesNotContain("foreign-instance-output", System.Text.Json.JsonSerializer.Serialize(pending));
        var claim = Assert.Single(await store.ClaimSandboxVerificationCleanupAsync(DateTimeOffset.UtcNow, f.Ids.NewId())); Assert.Equal(id, claim.InstanceId);
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim with { InstanceId = Guid.NewGuid() }, true, DateTimeOffset.UtcNow));
        Assert.False(await store.FinishSandboxVerificationCleanupAsync(claim with { InstanceId = null }, true, DateTimeOffset.UtcNow));
        await f.Identity.SignOutAsync(human.AuthenticationSessionId);
        Assert.True(await store.FinishSandboxVerificationCleanupAsync(claim, true, DateTimeOffset.UtcNow)); Assert.Equal(2, p.Runner.Calls);
        Assert.Null((await store.ReadSandboxVerificationAsync(owner.TenantId, profile.Id, 1)).Observation);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_verification_reads_fence_original_human_and_security_after_source_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var profile = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Read fence"), await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(profile.Id, [profile.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var service = InstanceVerification(f, p); var saved = await service.VerifyAsync(owner.TenantId, id, profile.Id, human, await Revision(), 1, 1);
        var hook = new SetupImageHook(p.Images) { After = async () => { await f.Identity.SignOutAsync(human.AuthenticationSessionId); _ = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); } };
        await Assert.ThrowsAsync<AccessDeniedException>(() => InstanceVerification(f, p, images: hook).ReadAsync(owner.TenantId, id, profile.Id, human, saved.Attempt!.AttemptId).AsTask());
        human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId);
        Assert.Equal(saved.Attempt!.AttemptId, (await service.ReadAsync(owner.TenantId, id, profile.Id, human)).Attempt!.AttemptId);
        hook.After = async () => { _ = await p.Profiles.SaveAsync(owner.TenantId, profile.Id, human, SetupInput("Read fence changed"), await Revision(), 1); };
        await Assert.ThrowsAsync<SecurityConflictException>(() => InstanceVerification(f, p, images: hook).ReadAsync(owner.TenantId, id, profile.Id, human).AsTask());
        var current = await service.ReadAsync(owner.TenantId, id, profile.Id, human);
        Assert.Equal("NeedsVerification", current.Status); Assert.Null(current.Observation); Assert.Equal(1, p.Runner.Calls);
    }
}
