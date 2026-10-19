using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private sealed class ProfileProbe : IBrowser, ITenantBrowserProfileControl
    {
        public int Reads, Resets, Deletes;
        public Func<Task>? BeforeDelete;
        public TenantBrowserProfileObservation Observation = new(Guid.NewGuid(), new('a', 64), true, 0, false, null, null);
        public BrowserProviderDescriptor Provider => new("controlled", "Controlled profile", new HashSet<BrowserFeature>());
        public bool IsAvailable => true;
        public BrowserHostPolicy HostPolicy => new(true, true, BrowserInteractionMode.InteractiveDemo, []);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => throw new InvalidOperationException();
        public ValueTask<TenantBrowserProfileObservation> ReadTenantProfileAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
        { Reads++; return ValueTask.FromResult(Observation); }
        public async ValueTask<TenantBrowserProfileResult> ResetTenantProfileAsync(TenantBrowserAgentOwner owner, Guid operation, string witness,
            Func<Guid, CancellationToken, ValueTask<bool>> begin, Func<CancellationToken, ValueTask<bool>> authorizeDelete, CancellationToken ct = default)
        {
            Resets++; Assert.Equal(Observation.Witness, witness);
            if (!await begin(Observation.ProviderEpoch, ct)) return new(operation, "not_started", "browser_profile_authority_changed");
            if (BeforeDelete is { } before) await before();
            var allowed = await authorizeDelete(ct); if (allowed) Deletes++;
            var result = new TenantBrowserProfileResult(operation, allowed ? "reset" : "unknown", allowed ? "browser_profile_reset_completed" : "browser_profile_reset_unknown");
            Observation = Observation with { LastResult = result }; return result;
        }
    }
    private static TenantBrowserProfileService ProfileService(Fixture f, IBrowser browser, IIamStore? store = null)
    { store ??= f.Iam; return new(store, (ITenantAgentStore)store, (ITenantBrowserProfileStore)store, f.Identity, new(store), browser, f.Clock, f.Ids); }
    private static TenantBrowserAgentOwner ProfileOwner(Setup s) => new(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId);
    private static async Task<TenantBrowserProfileCommit> BeginProfile(Fixture f, Setup s, ProfileProbe probe)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var reset = new AgentBrowserProfileReset(f.Ids.NewId(), s.Human.PrincipalId, probe.Observation.ProviderEpoch, probe.Observation.Witness, "pending", "browser_profile_reset_started", at);
        return (await ((ITenantBrowserProfileStore)f.Iam).TryBeginTenantProfileResetAsync(ProfileOwner(s), s.Human,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision, reset,
            new(reset.OperationId, s.Tenant, s.Human.PrincipalId, "browser.profile.reset", s.Agent.Instance.InstanceId.ToString("D"), "allowed", reset.ReasonCode, at)))!;
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_profile_reset_settles_facts_after_logout_or_security_change_without_deleting(bool sqlite, bool logout)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new ProfileProbe(); var service = ProfileService(f, probe);
        var view = await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human); var operation = f.Ids.NewId();
        probe.BeforeDelete = async () => {
            if (logout) await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            else await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Concurrent role change", ["browser.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        };
        var result = await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, operation, view.SecurityRevision, view.InstanceRevision, view.Witness);
        Assert.Equal("unknown", result.Status); Assert.Equal(0, probe.Deletes); Assert.Equal(1, probe.Resets);
        var saved = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
        Assert.Equal("unknown", saved.Instance.BrowserProfileReset!.Status); Assert.Equal(3, saved.Instance.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "browser.profile.reset.result" && a.ReasonCode == "browser_profile_reset_unknown");
        if (!logout) { Assert.Equal(result, await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, operation, view.SecurityRevision, view.InstanceRevision, view.Witness)); Assert.Equal(1, probe.Resets); }
    }

    [Theory] [InlineData(false, "cached")] [InlineData(true, "cached")] [InlineData(false, "restart")] [InlineData(true, "restart")]
    [InlineData(false, "busy")] [InlineData(true, "busy")]
    public async Task Tenant_Browser_profile_reset_recovery_records_cached_or_unknown_facts_without_native_replay(bool sqlite, string outcome)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new ProfileProbe();
        var started = await BeginProfile(f, s, probe); Assert.NotNull(started); var operation = started.Agent.Instance.BrowserProfileReset!.OperationId;
        if (outcome == "cached") probe.Observation = probe.Observation with { LastResult = new(operation, "reset", "browser_profile_reset_completed") };
        if (outcome == "restart") probe.Observation = probe.Observation with { ProviderEpoch = Guid.NewGuid() };
        if (outcome == "busy") probe.Observation = probe.Observation with { Busy = true }; // A pending begin can precede the native in-flight flag.
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var service = ProfileService(f, probe, reopened); var view = await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human);
        Assert.Equal(outcome == "cached" ? "reset" : outcome == "busy" ? "pending" : "unknown", view.LastReset!.Status);
        Assert.Equal(0, probe.Resets); Assert.Equal(0, probe.Deletes);
        if (outcome == "busy") { Assert.False(view.CanReset); return; }
        Assert.Equal(view.LastReset.OperationId, (await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human)).LastReset!.OperationId);
        // The same operation is a factual readback even with its obsolete submitted revisions.
        Assert.Equal(view.LastReset.Status, (await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, operation, 1, 1, probe.Observation.Witness)).Status);
        Assert.Equal(0, probe.Resets);
        var recovered = await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, f.Ids.NewId(), view.SecurityRevision, view.InstanceRevision, view.Witness);
        Assert.Equal("reset", recovered.Status); Assert.Equal(1, probe.Resets);
        // An older operation cannot be reused after a later deliberate reset.
        view = await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, operation, view.SecurityRevision, view.InstanceRevision, view.Witness).AsTask());
        Assert.Equal(1, probe.Deletes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_profile_pending_fences_actual_persistent_model_target_before_provider_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); await BeginProfile(f, s, new ProfileProbe());
        var target = Probe(s); target.Mode = BrowserProfileMode.PersistentAgent; var browser = new ReadProbe(target, BrowserProfileMode.PersistentAgent);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, browser).InspectAsync(Frame(s), ReadCall(), 32768).AsTask());
        Assert.Equal("browser_profile_reset_unconfirmed", denied.ReasonCode); Assert.Equal(0, target.Reads); Assert.Equal(0, browser.Reads);
    }

    [Theory] [InlineData(false, "agents.read")] [InlineData(true, "agents.read")] [InlineData(false, "browser.read")] [InlineData(true, "browser.read")]
    [InlineData(false, "browser.manage")] [InlineData(true, "browser.manage")]
    public async Task Tenant_Browser_profile_reset_requires_independent_current_human_permissions_before_provider_access(bool sqlite, string missing)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new ProfileProbe();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("profile-manager@example.test", "Profile manager", "strong-manager-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, (await f.Iam.ReadTenantAsync(s.Tenant))!.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        foreach (var assignment in (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Where(a => a.PrincipalId == member.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Partial profile manager", new[] { "agents.read", "browser.read", "browser.manage" }.Where(p => p != missing).ToArray(), await Revision());
        await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, member.PrincipalId, role.RoleId, TenantBrowserProfileRules.Scope(ProfileOwner(s)), await Revision());
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-manager-password"))!.Session;
        var human = new HumanSessionReference(account.AccountId, login.SessionId, member.PrincipalId); var service = ProfileService(f, probe); var revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, human, f.Ids.NewId(), revision, 1, probe.Observation.Witness).AsTask());
        Assert.Equal(0, probe.Resets); Assert.Equal(0, probe.Reads);
        if (missing == "browser.manage") Assert.False((await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, human)).CanReset);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, human).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human with { PrincipalId = s.Agent.PrincipalId }).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(Guid.NewGuid(), s.Agent.Instance.InstanceId, s.Human).AsTask());
        Assert.Null((await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!.Instance.BrowserProfileReset);
    }

    [Fact]
    public async Task Tenant_Browser_profile_SQLite_native_host_recreation_keeps_abandoned_bytes_unknown_until_a_new_human_reset()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var s = await Prepare(f); var root = Path.Combine(f.Root, "browser");
        var directory = Path.Combine(root, "tenants", s.Tenant.ToString("D"), "agents", s.Agent.Instance.InstanceId.ToString("D"));
        Directory.CreateDirectory(directory); var savedFile = Path.Combine(directory, "saved"); File.WriteAllText(savedFile, "private saved state");
        var first = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = root }, null);
        var observed = await first.ReadTenantProfileAsync(ProfileOwner(s)); var staged = await BeginProfile(f, s, new ProfileProbe { Observation = observed });
        await first.StopAsync(CancellationToken.None);
        var second = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = root }, null);
        try
        {
            var deletes = 0; second.TenantProfileDeleteProbe = path => { deletes++; Directory.Delete(path, true); };
            IIamStore reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock); var service = ProfileService(f, second, reopened);
            var view = await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human);
            Assert.Equal("unknown", view.LastReset!.Status); Assert.True(view.ProfilePresent); Assert.Equal(0, deletes);
            Assert.Equal("private saved state", File.ReadAllText(savedFile));
            Assert.NotEqual(observed.Witness, view.Witness);
            Assert.Equal("unknown", (await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, staged.Agent.Instance.BrowserProfileReset!.OperationId, 1, 1, observed.Witness)).Status);
            Assert.Equal(0, deletes);
            var result = await service.ResetAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, f.Ids.NewId(), view.SecurityRevision, view.InstanceRevision, view.Witness);
            Assert.Equal("reset", result.Status); Assert.Equal(1, deletes); Assert.False(Directory.Exists(directory));
        }
        finally { await second.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Tenant_Browser_profile_SQLite_guards_reset_shape_transition_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var s = await Prepare(f); var started = await BeginProfile(f, s, new ProfileProbe()); Assert.NotNull(started);
        await using var db = await f.Contexts!.CreateDbContextAsync(); var id = s.Agent.Instance.InstanceId.ToString("D");
        foreach (var patch in new[] { "NULL", "'{}'", "json_set(BrowserProfileResetJson, '$.witness','other')", "json_set(BrowserProfileResetJson, '$.status','reset')",
            "json_set(BrowserProfileResetJson, '$.principalId','00000000-0000-0000-0000-000000000000')", "json_set(BrowserProfileResetJson, '$.extra','forged')" })
        {
            // Each SQL fragment is a fixed adversarial fixture above; the actual Instance ID remains a bound parameter.
            var sql = "UPDATE AgentInstances SET BrowserProfileResetJson = " + patch.Replace("{", "{{").Replace("}", "}}") + ", Revision=Revision+1 WHERE InstanceId={0}";
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, id));
        }
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020040000_TenantBrowserArtifactDrops"));
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("pending", (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Agent.Instance.InstanceId))!.Instance.BrowserProfileReset!.Status);
    }

    [Fact]
    public async Task Tenant_Browser_profile_SQLite_empty_migration_roundtrip_preserves_model()
    {
        await using var f = await Fixture.Create(true); await using var db = await f.Contexts!.CreateDbContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261020040000_TenantBrowserArtifactDrops");
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("TR_AgentInstances_ProfileReset_Update", await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger'").ToListAsync());
    }
}
