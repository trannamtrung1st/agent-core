using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Providers;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private sealed class SetupReadiness : ISandboxReadiness
    {
        public string State { get; set; } = "Ready";
        public bool IsAvailable => State == "Ready";
        public string SafeMessage => "Synthetic readiness";
    }
    private sealed class SetupVerifier(string scope) : ITenantSandboxVerifier
    {
        public string HostScope => scope;
        public int Calls { get; private set; }
        public SandboxProfile? LastProfile { get; private set; }
        public Func<Task>? During { get; set; }
        public bool Clean { get; set; } = true;
        public int CleanupCalls { get; private set; }
        public Func<SandboxVerificationOutcome, SandboxVerificationOutcome>? Transform { get; set; }
        public async ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct = default)
        {
            Calls++; LastProfile = profile; if (During is { } action) await action();
            var result = new SandboxVerificationOutcome(new(profile.Id, profile.Revision, DateTimeOffset.UtcNow, true, "Synthetic setup prerequisite", Stdout: attempt.InstanceId is null ? "" : "bounded instance output",
                ContractVersion: SandboxProfileVerification.CurrentContractVersion, TenantId: attempt.TenantId, IncludesInitialization: !attempt.ImageOnly, HostScope: scope,
                InstanceId: attempt.InstanceId, InstanceRevision: attempt.InstanceRevision, RegistryCredentialId: attempt.RegistryCredentialId,
                RegistryCredentialRevision: attempt.RegistryCredentialRevision, RegistrySecretRevision: attempt.RegistrySecretRevision), Clean);
            return Transform?.Invoke(result) ?? result;
        }
        public ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default) { CleanupCalls++; return ValueTask.FromResult(true); }
    }
    private static async Task<HumanSessionReference> SetupHuman(Fixture f, Guid account, Guid principal, string login = "owner@example.test", string password = "strong-owner-password") =>
        new(account, (await f.Identity.SignInAsync(login, password))!.Session.SessionId, principal);
    private static (TenantSandboxProfileService Profiles, TenantSandboxVerificationService Verify, SandboxImageAuthority Images, SetupReadiness Ready, SetupVerifier Runner, SandboxVerificationHost Host, OpenSandboxOptions Options) SetupProfiles(Fixture f, OpenSandboxOptions? options = null)
    {
        options ??= new OpenSandboxOptions(); var store = (ITenantSandboxProfileStore)f.Iam; var verification = (ITenantSandboxVerificationStore)f.Iam;
        var guard = new TenantSandboxImageAuthorization(f.Iam, store, f.Identity, f.Authorizer, SetupResolver(f)); var host = new SandboxVerificationHost(options);
        var images = new SandboxImageAuthority(options, new InMemorySandboxStore(), TimeProvider.System, tenantCredentials: (ITenantCredentialStore)f.Iam,
            tenantAuthorization: guard, tenantVerifications: verification, verificationHost: host);
        var profiles = new TenantSandboxProfileService(f.Iam, store, f.Identity, f.Authorizer, images, f.Ids, TimeProvider.System, verification);
        var runner = new SetupVerifier(host.Scope);
        return (profiles, new(f.Iam, verification, profiles, guard, images, runner, f.Identity, f.Authorizer, f.Ids, TimeProvider.System), images, new(), runner, host, options);
    }
    private static TenantSandboxSetupResolver SetupResolver(Fixture f)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        return new((ITenantAgentStore)f.Iam, (ITenantDefinitionStore)f.Iam,
            new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default), (ITenantSandboxProfileStore)f.Iam);
    }
    private static TenantSandboxProfileInput SetupInput(string name, double cpu = 1) => new(name, name + " private description", true, "ghcr.io/acme/tool@sha256:" + new string('a', 64), Cpu: cpu);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_selection_narrows_owned_profiles_and_preserves_sparse_defaults_across_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("First"), await Revision(), 0)).Profile;
        var b = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Second", .5), await Revision(), 0)).Profile;
        await p.Verify.VerifyAsync(owner.TenantId, a.Id, human, await Revision(), 1); await p.Verify.VerifyAsync(owner.TenantId, b.Id, human, await Revision(), 1);
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Sandbox settings", await Revision());
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with { SandboxPolicy = new(a.Id, [a.Id, b.Id]) }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var services = ConfigurationServices(f, sandboxImages: p.Images, sandboxReadiness: p.Ready);
        var agent = await services.Agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision()); var id = agent.Instance.InstanceId;
        var reads = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, services.Configuration, f.Identity, f.Authorizer, services.Agents);
        var view = await reads.ReadSandboxAsync(owner.TenantId, id, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId));
        Assert.True(view.CompleteCatalog); Assert.True(view.CanManage); Assert.Equal(2, view.Profiles.Count); Assert.Equal(1d, view.Section!.Effective["cpu"]);
        var changed = await services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), 1,
            Fields(new { allowedProfileIds = new[] { b.Id }, profileId = b.Id, cpu = .4, initializationScript = "echo instance-setup" }), [], initiator: human);
        view = await reads.ReadSandboxAsync(owner.TenantId, id, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId));
        Assert.Equal(b.Id, view.Section!.Effective["profileId"]); Assert.Equal(.4d, view.Section.Effective["cpu"]); Assert.Equal(.5d, view.Section.DefinitionDefaults["cpu"]);
        Assert.Equal("instance", view.Section.Sources["cpu"]); Assert.Equal("echo instance-setup", view.Section.Effective["initializationScript"]);
        var before = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<AgentCoreException>(() => services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, changed.Instance.Revision, Fields(new { cpu = .75 }), [], initiator: human).AsTask());
        Assert.Equal(before, await Revision()); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        if (sqlite)
        {
            var reopened = ConfigurationServices(f, new SqliteIamStore(f.Contexts!, f.Content), sandboxImages: p.Images, sandboxReadiness: p.Ready);
            var persisted = await reopened.Configuration.ReadSandboxAsync(human.PrincipalId, owner.TenantId, id);
            Assert.Equal(b.Id, persisted.Section!.Effective["profileId"]); Assert.Equal(.4d, persisted.Section.Effective["cpu"]);
        }
        changed = await services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), changed.Instance.Revision,
            Fields(new { }), ["allowedProfileIds", "profileId", "cpu", "initializationScript"], initiator: human);
        view = await reads.ReadSandboxAsync(owner.TenantId, id, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId));
        Assert.Empty(view.Section!.Overrides); Assert.Equal(a.Id, view.Section.Effective["profileId"]); Assert.Equal(1d, view.Section.Effective["cpu"]);
        before = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, changed.Instance.Revision,
            Fields(new { cpu = .75 }), [], initiator: human).AsTask()); // The non-default .5 CPU profile also caps overrides.
        Assert.Equal(before, await Revision());
        changed = await services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), changed.Instance.Revision, Fields(new { profileId = b.Id }), [], initiator: human);
        await p.Profiles.SaveAsync(owner.TenantId, a.Id, human, SetupInput("First") with { Enabled = false }, await Revision(), 1);
        before = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, changed.Instance.Revision,
            Fields(new { }), ["profileId"], initiator: human).AsTask()); // Reset is also a new default selection; it cannot silently use disabled A.
        Assert.Equal(before, await Revision());
        changed = await services.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, changed.Instance.Revision, Fields(new { allowedProfileIds = new[] { b.Id } }), [], initiator: human);
        Assert.Equal(b.Id, changed.Instance.SettingsOverrides!.SandboxProfileId!.Value);
    }

    private static async Task<(TenantAgent Agent, TenantAgentConfigurationService Configuration, TenantAgentService Directory)> SetupAgent(
        Fixture f, Guid tenant, Guid actor, SandboxPolicy policy, ISandboxImageAuthority images, ISandboxReadiness ready)
    {
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Scoped Sandbox setup", await Revision());
        draft = await f.Service.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with { SandboxPolicy = policy }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(actor, tenant, draft.DraftId, await Revision(), draft.Revision);
        var services = ConfigurationServices(f, sandboxImages: images, sandboxReadiness: ready);
        return (await services.Agents.CreateFromPublicationAsync(actor, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision()), services.Configuration, services.Agents);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_selection_partial_profile_reader_can_narrow_but_cannot_read_hidden_defaults_or_execute(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Visible"), await Revision(), 0)).Profile;
        var b = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Hidden"), await Revision(), 0)).Profile;
        await p.Verify.VerifyAsync(owner.TenantId, a.Id, human, await Revision(), 1);
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(b.Id, [a.Id, b.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var minimal = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Minimal member", ["agents.chat"], await Revision());
        var account = await f.Identity.RegisterAsync("setup-reader@example.test", "Setup reader", "strong-reader-password");
        var invite = await f.Access.InviteAsync(human.PrincipalId, owner.TenantId, account.LoginKey, minimal.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invite.Invitation.InvitationId, invite.Token);
        var reader = await SetupHuman(f, account.AccountId, member.PrincipalId, "setup-reader@example.test", "strong-reader-password");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Manage one setup", ["agents.read", "agents.update", "sandbox.read", "sandbox.manage"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, role.RoleId, AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), await Revision());
        var profileRole = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read one source", ["sandbox.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, member.PrincipalId, profileRole.RoleId, TenantSandboxProfileRules.Scope(owner.TenantId, a.Id), await Revision());
        var reads = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, s.Configuration, f.Identity, f.Authorizer, s.Directory);
        var view = await reads.ReadSandboxAsync(owner.TenantId, id, new(reader.AccountId, reader.AuthenticationSessionId, reader.PrincipalId));
        Assert.False(view.CompleteCatalog); Assert.Null(view.Section); Assert.Equal(a.Id, Assert.Single(view.Profiles).Id);
        Assert.DoesNotContain(b.Id, System.Text.Json.JsonSerializer.Serialize(view)); Assert.DoesNotContain("Hidden private description", System.Text.Json.JsonSerializer.Serialize(view));
        var changed = await s.Configuration.PatchSettingsAsync(reader.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), 1,
            Fields(new { allowedProfileIds = new[] { a.Id }, profileId = a.Id }), [], initiator: reader);
        Assert.Equal(a.Id, changed.Instance.SettingsOverrides!.SandboxProfileId!.Value);
        view = await reads.ReadSandboxAsync(owner.TenantId, id, new(reader.AccountId, reader.AuthenticationSessionId, reader.PrincipalId));
        Assert.NotNull(view.Section); Assert.Equal(a.Id, view.Section.Effective["profileId"]); Assert.False(view.CompleteCatalog);
        Assert.DoesNotContain(b.Id, System.Text.Json.JsonSerializer.Serialize(view));
        Assert.False((await f.Authorizer.CheckAsync(reader.PrincipalId, "sandbox.execute", AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")))).Allowed);
        await f.Access.RevokeAssignmentAsync(human.PrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
        var before = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Configuration.PatchSettingsAsync(reader.PrincipalId, owner.TenantId, id, "sandboxPolicy", 0, 0, Fields(new { profileId = a.Id }), [], initiator: reader).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadSandboxAsync(owner.TenantId, id, new(reader.AccountId, reader.AuthenticationSessionId, reader.PrincipalId)).AsTask());
        Assert.Equal(before, await Revision());
    }

    private sealed class SetupImageHook(ISandboxImageAuthority inner) : ISandboxImageAuthority
    {
        public Func<Task>? After { get; set; }
        public ValueTask<SandboxProfile> ResolveAsync(SandboxProfile p, CancellationToken ct = default) => inner.ResolveAsync(p, ct);
        public async ValueTask<SandboxImageAvailability> InspectAsync(SandboxProfile p, CancellationToken ct = default, bool requireVerification = true)
        {
            var result = await inner.InspectAsync(p, ct, requireVerification);
            if (After is { } action) { After = null; await action(); }
            return result;
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_selection_fences_retired_human_reads_writes_and_profile_changes_before_commit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Fenced"), await Revision(), 0)).Profile;
        await p.Verify.VerifyAsync(owner.TenantId, a.Id, human, await Revision(), 1);
        var hook = new SetupImageHook(p.Images); var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(a.Id, [a.Id]), hook, p.Ready); var id = s.Agent.Instance.InstanceId;
        var reads = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, s.Configuration, f.Identity, f.Authorizer, s.Directory);
        hook.After = () => f.Identity.SignOutAsync(human.AuthenticationSessionId).AsTask();
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadSandboxAsync(owner.TenantId, id, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId)).AsTask());
        human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var before = await Revision(); var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        hook.After = () => f.Identity.SignOutAsync(human.AuthenticationSessionId).AsTask();
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { profileId = a.Id }), [], initiator: human).AsTask());
        Assert.Equal(before, await Revision()); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(1, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
        human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId);
        hook.After = async () => { _ = await p.Profiles.SaveAsync(owner.TenantId, a.Id, human, SetupInput("Fenced changed"), await Revision(), 1); };
        await Assert.ThrowsAsync<SecurityConflictException>(() => s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { profileId = a.Id }), [], initiator: human).AsTask());
        Assert.Equal(before + 1, await Revision()); Assert.Equal(1, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
        await p.Verify.VerifyAsync(owner.TenantId, a.Id, human, await Revision(), 2);
        var recovered = await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", await Revision(), 1, Fields(new { profileId = a.Id }), [], initiator: human);
        Assert.Equal(2, recovered.Instance.Revision);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_instance_sandbox_selection_rejects_unverified_unavailable_and_foreign_sources_without_global_fallback(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var human = await SetupHuman(f, owner.AccountId, owner.OwnerPrincipalId); var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = (await p.Profiles.SaveAsync(owner.TenantId, null, human, SetupInput("Owned"), await Revision(), 0)).Profile;
        var s = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(a.Id, [a.Id]), p.Images, p.Ready); var id = s.Agent.Instance.InstanceId;
        var before = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { profileId = a.Id }), [], initiator: human).AsTask());
        Assert.Equal(before, await Revision());
        await p.Verify.VerifyAsync(owner.TenantId, a.Id, human, await Revision(), 1); p.Ready.State = "Unavailable"; before = await Revision();
        await Assert.ThrowsAsync<AgentCoreException>(() => s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { profileId = a.Id }), [], initiator: human).AsTask());
        Assert.Equal("ServerUnavailable", Assert.Single((await s.Configuration.ReadSandboxAsync(human.PrincipalId, owner.TenantId, id)).Profiles).Availability);
        var changed = await s.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, id, "sandboxPolicy", before, 1, Fields(new { cpu = .5 }), [], initiator: human);
        Assert.Equal(.5, changed.Instance.SettingsOverrides!.SandboxCpu!.Value); p.Ready.State = "Ready";
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(x => x.AccountId == owner.AccountId).PrincipalId;
        var beta = await f.Access.CreateTenantAsync(system, owner.AccountId, "Beta", "beta"); var betaUser = beta.Users.Single();
        var betaHuman = await SetupHuman(f, owner.AccountId, betaUser.PrincipalId);
        var foreign = (await p.Profiles.SaveAsync(beta.Tenant.TenantId, null, betaHuman, SetupInput("Foreign"), beta.Tenant.Revision, 0)).Profile;
        var mixed = await SetupAgent(f, owner.TenantId, human.PrincipalId, new(a.Id, [a.Id, foreign.Id]), p.Images, p.Ready); var mixedId = mixed.Agent.Instance.InstanceId;
        var view = await mixed.Configuration.ReadSandboxAsync(human.PrincipalId, owner.TenantId, mixedId);
        Assert.False(view.CompleteCatalog); Assert.Null(view.Section); Assert.Equal(a.Id, Assert.Single(view.Profiles).Id);
        before = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => mixed.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, mixedId, "sandboxPolicy", before, 1, Fields(new { initializationScript = "echo no" }), [], initiator: human).AsTask());
        Assert.Equal(before, await Revision());
        var recovered = await mixed.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, mixedId, "sandboxPolicy", before, 1, Fields(new { allowedProfileIds = new[] { a.Id }, profileId = a.Id }), [], initiator: human);
        Assert.Equal(2, recovered.Instance.Revision);
        before = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => mixed.Configuration.PatchSettingsAsync(human.PrincipalId, owner.TenantId, mixedId, "sandboxPolicy", before, 2, Fields(new { profileId = "global-profile" }), [], initiator: human).AsTask());
        Assert.Equal(before, await Revision());
    }
}
