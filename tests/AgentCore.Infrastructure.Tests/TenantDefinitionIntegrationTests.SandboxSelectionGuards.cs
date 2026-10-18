using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_profile_switch_checks_independent_scopes_redacts_target_and_reset_clears_selection(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var p = SetupProfiles(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Membership", ["members.read"], await Revision());
        var account = await f.Identity.RegisterAsync("selection-manager@example.test", "Selection manager", "strong-manager-password");
        var invitation = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, role.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invitation.Invitation.InvitationId, invitation.Token);
        var human = await SetupHuman(f, account.AccountId, member.PrincipalId, account.LoginKey, "strong-manager-password");
        async Task<Guid> Grant(string name, string[] permissions, AccessScope scope)
        {
            var r = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, human.PrincipalId, r.RoleId, scope, await Revision())).AssignmentId;
        }
        var agentScope = AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D"));
        await Grant("Read environment", ["sandbox.read"], agentScope);
        await Grant("Read Session", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await Grant("Read old source", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host);
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var change = new TenantSandboxProfileSwitch(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, null, s.Alternate!.Id, 1, 1);
        async Task Act() => await controls.RequestProfileSwitchAsync(s.Tenant, human, change, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(Act);
        await Grant("Manage Session", ["sessions.manage"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId)); await Assert.ThrowsAsync<AccessDeniedException>(Act);
        await Grant("Manage environment", ["sandbox.manage"], agentScope); await Assert.ThrowsAsync<AccessDeniedException>(Act);
        var targetRead = await Grant("Read selected source", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate.Id));
        var setup = await SetupResolver(f).ResolveAsync(s.Tenant, s.Session.AgentInstanceId, s.Alternate.Id);
        var before = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        AuthorizationAudit Audit() => new(f.Ids.NewId(), s.Tenant, human.PrincipalId, "sandbox.profile.switch", change.SessionId.ToString("D"), "allowed", "human_profile_switch", DateTimeOffset.UtcNow);
        var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        // The commit owner repeats source/setup and eligibility checks, even for a trusted preparation.
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryRequestTenantSandboxProfileSwitchAsync(next, before.Tenant.Revision,
            new(change, setup.Profile with { Cpu = setup.Profile.Cpu / 2 }, p.Host.Scope), human, Audit()).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryRequestTenantSandboxProfileSwitchAsync(next, before.Tenant.Revision,
            new(change, setup.Profile, new string('f', 64)), human, Audit()).AsTask());
        Assert.Equal(saved.Lease, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        await Act(); var selected = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, selected with { Revision = selected.Revision + 1, SelectionWitness = null }, selected.Revision).AsTask());
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, targetRead, await Revision());
        var read = await new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer).ReadAsync(s.Tenant, s.Session.SessionId, human);
        Assert.True(read.Environment!.SelectionRestricted); Assert.Null(read.Environment.Selection); Assert.Null(read.Environment.SelectedProfileId); Assert.Equal(s.Lease.Profile.Id, read.Environment.ProfileId);
        // Explicit reset needs the old environment's scopes, not revoked read authority over the pending target.
        await controls.RequestResetAsync(s.Tenant, human, new(s.Session.SessionId, saved.SessionRevision, selected.Generation, selected.Revision, null), await Revision());
        var reset = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.Null(reset.SelectionWitness); Assert.Null(reset.TransitionProfileId); Assert.Null(reset.SelectedProfileId); Assert.Equal("reset", reset.CleanupReason);
        if (sqlite) Assert.Equal(reset, (await new SqliteIamStore(f.Contexts!, f.Content).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")] [InlineData(false, "relogin")] [InlineData(true, "relogin")]
    [InlineData(false, "password")] [InlineData(true, "password")] [InlineData(false, "wrong-principal")] [InlineData(true, "wrong-principal")]
    public async Task Tenant_Sandbox_profile_switch_transaction_requires_original_human(bool sqlite, string retirement)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var p = SetupProfiles(f);
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var change = new TenantSandboxProfileSwitch(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, null, s.Alternate!.Id, 1, 1);
        var setup = await SetupResolver(f).ResolveAsync(s.Tenant, s.Session.AgentInstanceId, s.Alternate.Id);
        var before = (await f.Iam.ReadTenantAsync(s.Tenant))!; var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var audits = (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count; var human = s.Human;
        if (retirement == "password") await f.Identity.ChangePasswordAsync(human.AccountId, "strong-owner-password", "replacement-owner-password");
        else if (retirement == "wrong-principal") human = human with { PrincipalId = f.Ids.NewId() };
        else { await f.Identity.SignOutAsync(human.AuthenticationSessionId); if (retirement == "relogin") Assert.NotNull(await f.Identity.SignInAsync("owner@example.test", "strong-owner-password")); }
        AuthorizationAudit Audit() => new(f.Ids.NewId(), s.Tenant, human.PrincipalId, "sandbox.profile.switch", change.SessionId.ToString("D"), "allowed", "human_profile_switch", DateTimeOffset.UtcNow);
        Assert.Equal("initiator_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryRequestTenantSandboxProfileSwitchAsync(next, before.Tenant.Revision, new(change, setup.Profile, p.Host.Scope), human, Audit()).AsTask())).ReasonCode);
        Assert.Equal(saved.Lease, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_profile_switch_requires_explicit_reset_after_failed_initialization_and_preserves_consent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true); var store = (ITenantSandboxRuntimeStore)f.Iam; var p = SetupProfiles(f);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var failed = s.Lease with { Revision = 2, State = SandboxLeaseState.Terminated, CleanupReason = "initializationFailed", Initialization = new("Failed", s.Lease.Profile.Id, 1, 1) };
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, failed, 1));
        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host);
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var change = new TenantSandboxProfileSwitch(s.Session.SessionId, saved.SessionRevision, failed.Generation, failed.Revision, null, s.Alternate!.Id, 1, 1);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var revision = await Revision();
        Assert.Equal("sandbox_recovery_required", (await Assert.ThrowsAsync<SecurityConflictException>(() => controls.RequestProfileSwitchAsync(s.Tenant, s.Human, change, revision).AsTask())).ReasonCode);
        await controls.RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, failed.Generation, failed.Revision, null), await Revision());
        var receipt = await controls.RequestProfileSwitchAsync(s.Tenant, s.Human, change with { ExpectedLeaseRevision = 3 }, await Revision()); Assert.True(receipt.RetirementConfirmed);
        var selected = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.Equal("reset", selected.CleanupReason); Assert.Equal(failed.Initialization, selected.Initialization); Assert.Equal("awaitingReview", selected.TransitionPhase);
        var services = SandboxApprovalServices(f, s);
        var review = await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Alternate.Id, new("printf fresh", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation);
        Assert.True(review.Request!.Snapshot!.Sandbox!.WillInitialize);
    }
    [Theory] [InlineData(false, "instance")] [InlineData(true, "instance")] [InlineData(false, "profile")] [InlineData(true, "profile")]
    public async Task Tenant_Sandbox_profile_switch_rechecks_pending_target_drift_at_Review_and_atomic_reservation(bool sqlite, string drift)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var p = SetupProfiles(f);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Terminated }, 1));
        var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await controls.RequestProfileSwitchAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, s.Lease.Generation, 2, null, s.Alternate!.Id, 1, 1), await Revision());
        var services = SandboxApprovalServices(f, s);
        var review = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Alternate.Id, new("printf reviewed", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await services.Approvals.ReadViewAsync(s.Tenant, review.RequestId, SandboxHuman(s));
        await services.Approvals.DecideAsync(s.Tenant, review.RequestId, SandboxHuman(s), true, review.Operation.ActionHash, view.SecurityRevision, review.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var selected = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        var pinned = Assert.Single(s.Run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!, v => v.Id == s.Alternate.Id);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var fresh = s.Lease with { Generation = f.Ids.NewId(), Revision = selected.Revision + 1, Profile = pinned, AdmissionToolCallId = "fresh-target" };
        var write = new TenantSandboxRuntimeWrite(frame, await Revision(), run.Revision);
        // A new provider host cannot consume the saved selection even with a valid admitted profile.
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxLeaseAsync(write, fresh with { ProviderHostScope = new string('f', 64) }, selected.Revision, DateTimeOffset.UtcNow).AsTask());
        if (drift == "instance")
            await ConfigurationServices(f).Configuration.PatchSettingsAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, "conversationPolicy", await Revision(), 1, Fields(new { language = "fr-FR" }), [], initiator: s.Human);
        else
            await p.Profiles.SaveAsync(s.Tenant, s.Alternate.Id, s.Human, SetupInput("Changed target") with { InitializationScript = "printf changed-init" }, await Revision(), 1);
        Assert.Equal("sandbox_selection_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => services.Execution.ReserveAsync(frame, review.RequestId).AsTask())).ReasonCode);
        write = write with { SecurityRevision = await Revision() };
        Assert.Equal("sandbox_selection_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryReserveTenantSandboxLeaseAsync(write, fresh, selected.Revision, DateTimeOffset.UtcNow).AsTask())).ReasonCode);
        Assert.Equal("sandbox_selection_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => services.Approvals.PrepareSandboxAsync(new(s.Tenant, frame.RunId), s.Alternate.Id, new("printf new-review", TimeoutSeconds: 30), run.Revision, frame.Generation).AsTask())).ReasonCode);
        Assert.Equal(selected, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease); Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
        // Explicit reset clears obsolete pins so a caller can prepare a new, independently authorized selection.
        await controls.RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, selected.Generation, selected.Revision, null), await Revision());
        Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!.SelectionWitness);
    }

}
