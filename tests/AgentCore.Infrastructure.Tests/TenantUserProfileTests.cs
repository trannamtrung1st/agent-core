using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantUserProfileTests
{
    private static TenantUserProfileService Service(Fixture f, ITenantUserProfileStore? store = null) => new(f.Iam, store ?? (ITenantUserProfileStore)f.Iam, f.Identity, f.Ids, f.Clock);
    private static async Task<(Guid Tenant, HumanSessionReference Human)> Start(Fixture f)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        return (owner.TenantId, new(owner.AccountId, login.SessionId, owner.OwnerPrincipalId));
    }
    private static async Task<HumanSessionReference> Member(Fixture f, Guid tenant, HumanSessionReference owner)
    {
        var member = await f.Identity.RegisterAsync("member@example.test", "Member", "strong-member-password");
        var role = await f.Access.SaveRoleAsync(owner.PrincipalId, tenant, null, "No Profile administration", ["agents.read"], (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.PrincipalId, tenant, member.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(member.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
        return new(member.AccountId, (await f.Identity.SignInAsync(member.LoginKey, "strong-member-password"))!.Session.SessionId, user.PrincipalId);
    }
    private static TenantUserProfileChange Change(TenantUserProfileView view, string value) => new(view.SecurityRevision, view.Profile.Revision, new Dictionary<string, string?> { ["preferredName"] = value });

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_user_Profile_has_actual_membership_ownership_independent_Account_tenants_and_no_implicit_seed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, human) = await Start(f); var service = Service(f);
        var empty = await service.ReadAsync(tenant, human); Assert.Equal(0, empty.Profile.Revision); Assert.Empty(empty.Profile.Preferences);
        var audits = (await f.Iam.ReadAuditAsync(tenant, 100)).Count;
        Assert.Equal(empty.SecurityRevision, (await service.ReadAsync(tenant, human)).SecurityRevision); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(tenant, 100)).Count);
        var saved = await service.UpdateAsync(tenant, human, new(empty.SecurityRevision, 0, new Dictionary<string, string?> {
            ["preferredName"] = "  friend  ", ["language"] = "vi", ["locale"] = "vi-VN", ["timeZone"] = "Asia/Ho_Chi_Minh" }));
        Assert.Equal("friend", saved.Profile.Preferences["preferredName"].Value); Assert.Equal(1, saved.Profile.Revision); Assert.Equal(empty.SecurityRevision + 1, saved.SecurityRevision);
        Assert.All(saved.Profile.Preferences.Values, p => Assert.Equal(UserProfileValueSource.UserSet, p.Source));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, UserProfileValue>)saved.Profile.Preferences)["preferredName"] = new("forged", UserProfileValueSource.UserSet, f.Clock.GetUtcNow()));
        var beta = await f.Access.CreateTenantAsync((await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == human.AccountId).PrincipalId, human.AccountId, "Beta", "beta");
        var betaHuman = human with { PrincipalId = beta.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantUserProfileStore)f.Iam).ReadOwnTenantUserProfileAsync(beta.Tenant.TenantId, human).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantUserProfileStore)f.Iam).ReadOwnTenantUserProfileAsync(tenant, betaHuman).AsTask());
        var betaEmpty = await service.ReadAsync(beta.Tenant.TenantId, betaHuman);
        Assert.Equal(0, betaEmpty.Profile.Revision); Assert.Empty(betaEmpty.Profile.Preferences); Assert.NotEqual(human.PrincipalId, betaHuman.PrincipalId);
        var other = await service.UpdateAsync(beta.Tenant.TenantId, betaHuman, Change(betaEmpty, "Beta private"));
        Assert.Equal("friend", (await service.ReadAsync(tenant, human)).Profile.Preferences["preferredName"].Value);
        var member = await Member(f, tenant, human);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantUserProfileStore)f.Iam).ReadOwnTenantUserProfileAsync(tenant, human with { PrincipalId = member.PrincipalId }).AsTask());
        var memberEmpty = await service.ReadAsync(tenant, member); Assert.Empty(memberEmpty.Profile.Preferences);
        await service.UpdateAsync(tenant, member, Change(memberEmpty, "Member private"));
        var current = await service.ReadAsync(tenant, human);
        var cleared = await service.UpdateAsync(tenant, human, new(current.SecurityRevision, current.Profile.Revision,
            new Dictionary<string, string?> { ["preferredName"] = null, ["locale"] = null, ["language"] = null, ["timeZone"] = null }));
        Assert.Empty(cleared.Profile.Preferences); Assert.Equal(2, cleared.Profile.Revision); Assert.Equal("Beta private", other.Profile.Preferences["preferredName"].Value);
        var profileAudits = (await f.Iam.ReadAuditAsync(tenant, 100)).Where(a => a.Action == "profile.update").ToArray(); Assert.Equal(3, profileAudits.Length);
        var auditText = System.Text.Json.JsonSerializer.Serialize(profileAudits); Assert.DoesNotContain("friend", auditText); Assert.DoesNotContain("Member private", auditText); Assert.DoesNotContain("timeZone", auditText);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); Assert.Equal(3, await db.Set<TenantUserProfileRecord>().CountAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            var reopened = Service(f, new SqliteIamStore(f.Contexts)); Assert.Empty((await reopened.ReadAsync(tenant, human)).Profile.Preferences);
            Assert.Equal("Member private", (await reopened.ReadAsync(tenant, member)).Profile.Preferences["preferredName"].Value);
        }
    }

    [Theory] [InlineData(false, "account")] [InlineData(true, "account")] [InlineData(false, "principal")] [InlineData(true, "principal")]
    [InlineData(false, "tenant")] [InlineData(true, "tenant")] [InlineData(false, "agent")] [InlineData(true, "agent")]
    public async Task Tenant_user_Profile_rejects_untrusted_identity_substitution_at_owning_store(bool sqlite, string field)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, human) = await Start(f); var store = (ITenantUserProfileStore)f.Iam;
        if (field == "account") human = human with { AccountId = f.Ids.NewId() };
        if (field == "principal") human = human with { PrincipalId = f.Ids.NewId() };
        if (field == "tenant") tenant = f.Ids.NewId();
        if (field == "agent") { var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision); human = human with { PrincipalId = agent.PrincipalId }; }
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ReadOwnTenantUserProfileAsync(tenant, human).AsTask());
        var now = DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds()); var profile = new TenantUserProfile(tenant, human.PrincipalId, human.AccountId, 1,
            new Dictionary<string, UserProfileValue> { ["preferredName"] = new("forged", UserProfileValueSource.UserSet, now) }, now);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TrySaveOwnTenantUserProfileAsync(profile, human, 1, 0, Audit(f, profile)).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_user_Profile_CAS_duplicate_audit_and_forged_source_cannot_mutate_owned_values(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, human) = await Start(f); var store = (ITenantUserProfileStore)f.Iam; var service = Service(f);
        var before = await service.ReadAsync(tenant, human); var saved = await service.UpdateAsync(tenant, human, Change(before, "Saved"));
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.UpdateAsync(tenant, human, Change(before, "stale both")).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.UpdateAsync(tenant, human, new(saved.SecurityRevision, 0, new Dictionary<string, string?> { ["preferredName"] = "stale Profile" })).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.UpdateAsync(tenant, human, new(before.SecurityRevision, 1, new Dictionary<string, string?> { ["preferredName"] = "stale security" })).AsTask());
        var next = saved.Profile with { Revision = 2 }; var previousAudit = Assert.Single(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Action == "profile.update");
        Assert.Null(await store.TrySaveOwnTenantUserProfileAsync(next, human, saved.SecurityRevision, 1, previousAudit));
        var forged = next with { Preferences = new Dictionary<string, UserProfileValue> { ["preferredName"] = new("Host forged", UserProfileValueSource.HostSet, next.UpdatedAt) } };
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveOwnTenantUserProfileAsync(forged, human, saved.SecurityRevision, 1, Audit(f, next)).AsTask());
        Assert.Equal(saved.Profile.Revision, (await service.ReadAsync(tenant, human)).Profile.Revision); Assert.Equal(saved.SecurityRevision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Action == "profile.update");
    }
    private static AuthorizationAudit Audit(Fixture f, TenantUserProfile p) => new(f.Ids.NewId(), p.TenantId, p.PrincipalId, "profile.update", p.PrincipalId.ToString("D"), "allowed", "profile_updated", p.UpdatedAt);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_user_Profile_security_writes_cannot_detach_or_reparent_its_actual_membership(bool sqlite, bool reparent)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, owner) = await Start(f); var human = await Member(f, tenant, owner); var service = Service(f);
        var saved = await service.UpdateAsync(tenant, human, Change(await service.ReadAsync(tenant, human), "Retained owner"));
        var before = (await f.Iam.ReadTenantAsync(tenant))!; var actual = before.Users.Single(u => u.PrincipalId == human.PrincipalId); var replacement = f.Ids.NewId();
        var proposed = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 },
            Users = reparent ? before.Users.Select(u => u.UserId == actual.UserId ? u with { UserId = replacement } : u).ToArray() : before.Users.Where(u => u.UserId != actual.UserId).ToArray(),
            Principals = reparent ? before.Principals.Select(p => p.PrincipalId == actual.PrincipalId ? p with { UserId = replacement } : p).ToArray() : before.Principals.Where(p => p.PrincipalId != actual.PrincipalId).ToArray(),
            Assignments = reparent ? before.Assignments : before.Assignments.Where(a => a.PrincipalId != actual.PrincipalId).ToArray() };
        var audit = new AuthorizationAudit(f.Ids.NewId(), tenant, owner.PrincipalId, "members.status", actual.UserId.ToString("D"), "allowed", "test", f.Clock.GetUtcNow());
        var committed = false;
        var failure = await Record.ExceptionAsync(async () => committed = await f.Iam.TrySaveTenantAsync(proposed, before.Tenant.Revision, audit));
        Assert.False(committed); Assert.True(failure is null or ArgumentException, failure?.ToString());
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var retained = await service.ReadAsync(tenant, human); Assert.Equal(saved.Profile.Revision, retained.Profile.Revision); Assert.Equal("Retained owner", retained.Profile.Preferences["preferredName"].Value);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(tenant, 100), a => a.AuditId == audit.AuditId);
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")] [InlineData(false, "password")] [InlineData(true, "password")]
    [InlineData(false, "membership")] [InlineData(true, "membership")] [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_user_Profile_owning_commit_rechecks_original_login_membership_CAS_and_cancellation(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, owner) = await Start(f); var human = await Member(f, tenant, owner); var service = Service(f);
        var before = await service.ReadAsync(tenant, human); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new ProfileHook((ITenantUserProfileStore)f.Iam, beforeSave: async () => { entered.TrySetResult(); await release.Task; });
        var pending = Service(f, hook).UpdateAsync(tenant, human, Change(before, "Must not commit"), cancel.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "logout") await f.Identity.SignOutAsync(human.AuthenticationSessionId);
            if (boundary == "password") await f.Identity.ChangePasswordAsync(human.AccountId, "strong-member-password", "replacement-member-password");
            if (boundary == "membership") await f.Access.SetMemberStatusAsync(owner.PrincipalId, tenant, (await f.Iam.ReadTenantAsync(tenant))!.Users.Single(u => u.PrincipalId == human.PrincipalId).UserId, IdentityStatus.Suspended, before.SecurityRevision);
            if (boundary == "compete") await service.UpdateAsync(tenant, human, Change(before, "Actual winner"));
            if (boundary == "cancel") cancel.Cancel();
            release.TrySetResult();
            if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else if (boundary == "compete") await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
            else await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
            if (boundary == "membership") await f.Access.SetMemberStatusAsync(owner.PrincipalId, tenant, (await f.Iam.ReadTenantAsync(tenant))!.Users.Single(u => u.PrincipalId == human.PrincipalId).UserId, IdentityStatus.Active, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            var login = (await f.Identity.SignInAsync("member@example.test", boundary == "password" ? "replacement-member-password" : "strong-member-password"))!.Session;
            var retained = await service.ReadAsync(tenant, human with { AuthenticationSessionId = login.SessionId });
            Assert.Equal(boundary == "compete" ? 1 : 0, retained.Profile.Revision);
            if (boundary == "compete") Assert.Equal("Actual winner", retained.Profile.Preferences["preferredName"].Value); else Assert.Empty(retained.Profile.Preferences);
            Assert.Equal(boundary == "compete" ? 1 : 0, (await f.Iam.ReadAuditAsync(tenant, 100)).Count(a => a.Action == "profile.update"));
        }
        finally { release.TrySetResult(); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_user_Profile_private_read_rechecks_authority_and_changed_profile_after_collection(bool sqlite, bool changed)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, human) = await Start(f); var service = Service(f);
        var saved = await service.UpdateAsync(tenant, human, Change(await service.ReadAsync(tenant, human), "Private original"));
        var count = 0; var hooked = new ProfileHook((ITenantUserProfileStore)f.Iam, afterRead: async () => {
            if (++count != 1) return;
            if (changed) await service.UpdateAsync(tenant, human, Change(saved, "New private value")); else await f.Identity.SignOutAsync(human.AuthenticationSessionId);
        });
        if (changed) await Assert.ThrowsAsync<SecurityConflictException>(() => Service(f, hooked).ReadAsync(tenant, human).AsTask());
        else await Assert.ThrowsAsync<AccessDeniedException>(() => Service(f, hooked).ReadAsync(tenant, human).AsTask());
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_user_Profile_commit_to_acknowledgement_gap_recovers_facts_without_automatic_write_replay(bool sqlite, bool logout)
    {
        await using var f = await Fixture.Create(sqlite); var (tenant, human) = await Start(f); var service = Service(f); var before = await service.ReadAsync(tenant, human);
        var hook = new ProfileHook((ITenantUserProfileStore)f.Iam, afterSave: async () => {
            if (logout) await f.Identity.SignOutAsync(human.AuthenticationSessionId); else throw new IOException("Injected acknowledgement loss.");
        });
        if (logout) await Assert.ThrowsAsync<AccessDeniedException>(() => Service(f, hook).UpdateAsync(tenant, human, Change(before, "Accepted before acknowledgement")).AsTask());
        else await Assert.ThrowsAsync<IOException>(() => Service(f, hook).UpdateAsync(tenant, human, Change(before, "Accepted before acknowledgement")).AsTask());
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var currentHuman = human with { AuthenticationSessionId = login.SessionId }; var recovered = await service.ReadAsync(tenant, currentHuman);
        Assert.Equal(1, recovered.Profile.Revision); Assert.Equal("Accepted before acknowledgement", recovered.Profile.Preferences["preferredName"].Value);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.UpdateAsync(tenant, currentHuman, Change(before, "Replay must fail")).AsTask());
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Action == "profile.update");
    }
    private sealed class ProfileHook(ITenantUserProfileStore inner, Func<Task>? beforeSave = null, Func<Task>? afterRead = null, Func<Task>? afterSave = null) : ITenantUserProfileStore
    {
        public async ValueTask<TenantUserProfileView> ReadOwnTenantUserProfileAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
        { var result = await inner.ReadOwnTenantUserProfileAsync(tenant, human, ct); if (afterRead is not null) await afterRead(); return result; }
        public async ValueTask<TenantUserProfileView?> TrySaveOwnTenantUserProfileAsync(TenantUserProfile profile, HumanSessionReference human, long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit, CancellationToken ct = default)
        { if (beforeSave is not null) await beforeSave(); var result = await inner.TrySaveOwnTenantUserProfileAsync(profile, human, expectedSecurityRevision, expectedProfileRevision, audit, ct); if (result is not null && afterSave is not null) await afterSave(); return result; }
    }
}
