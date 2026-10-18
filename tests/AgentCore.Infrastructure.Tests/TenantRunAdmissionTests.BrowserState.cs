using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private sealed class UnscopedBrowserStateProbe : IBrowser, IBrowserStateReader
    {
        public BrowserProviderDescriptor Provider => new("unscoped", "Unscoped state", new HashSet<BrowserFeature>());
        public bool IsAvailable => true;
        public BrowserHostPolicy HostPolicy => new(true, true, BrowserInteractionMode.ReadNavigation, []);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => throw new InvalidOperationException("No execution fallback.");
        public ValueTask<ResourceStateObservation> ObserveAsync(Guid instance, CancellationToken ct = default) => throw new InvalidOperationException("No unscoped state fallback.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_state_does_not_fall_back_to_an_unscoped_reader(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var service = new TenantBrowserStateService(f.Iam, (ITenantSessionStore)f.Iam, f.Identity, f.Authorizer, new UnscopedBrowserStateProbe(), f.Clock);
        var view = await service.ReadAsync(setup.Tenant, setup.Session, setup.Owner);
        Assert.Equal("unavailable", view.Observation.Availability); Assert.Equal("unknown", view.Observation.Sharing);
        Assert.Null(view.Observation.LiveContexts); Assert.Null(view.Observation.OpenPages);
    }

    private sealed class BrowserStateProbe : IBrowser, ITenantBrowserStateReader
    {
        public Func<Task>? DuringRead;
        public List<TenantBrowserOwner> Seen { get; } = [];
        public BrowserProviderDescriptor Provider => new("controlled", "Controlled state", new HashSet<BrowserFeature>());
        public bool IsAvailable => true;
        public BrowserHostPolicy HostPolicy => new(true, true, BrowserInteractionMode.ReadNavigation, []);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => throw new InvalidOperationException("State read must not execute Browser work.");
        public async ValueTask<ResourceStateObservation> ObserveTenantSessionAsync(TenantBrowserOwner owner, CancellationToken ct = default)
        {
            Seen.Add(owner); if (DuringRead is { } change) await change(); ct.ThrowIfCancellationRequested();
            return new("browser", "available", "isolated", DateTimeOffset.UtcNow, LiveContexts: 1, OpenPages: 1);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_state_requires_independent_exact_Session_and_Browser_reads_without_agent_admin_or_Chat(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        async Task<RoleAssignment> Grant(string name, string key, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, name, [key], await Revision());
            return await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, scope, await Revision());
        }
        var probe = new BrowserStateProbe(); var service = new TenantBrowserStateService(f.Iam, (ITenantSessionStore)f.Iam, f.Identity, f.Authorizer, probe, f.Clock);
        var agentScope = AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        var browserRead = await Grant("Browser state", "browser.read", agentScope);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Session, member).AsTask()); Assert.Empty(probe.Seen);
        await Grant("Read this Session", "sessions.read", TenantSessionRules.Scope(setup.Tenant, setup.Session));
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.read", agentScope)).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.chat", agentScope)).Allowed);
        var revision = await Revision(); var audit = (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Count;
        var view = await service.ReadAsync(setup.Tenant, setup.Session, member);
        Assert.Equal(setup.Session, view.SessionId); Assert.Equal(revision, view.SecurityRevision); Assert.Equal(1, view.SessionRevision);
        Assert.Equal(1, view.Observation.OpenPages);
        Assert.Equal(new(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Agent.PrincipalId, setup.Session), Assert.Single(probe.Seen));
        Assert.Equal(revision, await Revision()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Count);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, f.Ids.NewId(), member).AsTask()); Assert.Single(probe.Seen);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, browserRead.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Session, member).AsTask()); Assert.Single(probe.Seen);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        var betaHuman = setup.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Tenant.TenantId, setup.Session, betaHuman).AsTask()); Assert.Single(probe.Seen);
    }

    [Theory]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "account")] [InlineData(true, "account")]
    [InlineData(false, "security")] [InlineData(true, "security")]
    [InlineData(false, "session")] [InlineData(true, "session")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_Browser_state_discards_observation_after_current_human_authority_or_scope_changes(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read Browser state", ["browser.read", "sessions.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        using var cancellation = new CancellationTokenSource();
        var probe = new BrowserStateProbe { DuringRead = async () => {
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
            if (boundary == "account")
            {
                var account = (await f.Iam.ReadAccountAsync(member.AccountId))!; var revision = await Revision();
                Assert.True(await f.Iam.TryUpdateAccountAsync(account with { Status = IdentityStatus.Suspended, SecurityStamp = "suspended" }, account.SecurityStamp,
                    new(f.Ids.NewId(), null, setup.Owner.PrincipalId, "system.accounts.manage", account.AccountId.ToString("D"), "allowed", "account_suspended", f.Clock.GetUtcNow())));
                Assert.Equal(revision, await Revision());
            }
            if (boundary == "security") await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Concurrent change", ["agents.read"], await Revision());
            if (boundary == "session") await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(setup.Session, 1, TenantSessionAction.Rename, "Changed while reading"), await Revision());
            if (boundary == "cancel") cancellation.Cancel();
        }};
        var service = new TenantBrowserStateService(f.Iam, (ITenantSessionStore)f.Iam, f.Identity, f.Authorizer, probe, f.Clock);
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(setup.Tenant, setup.Session, member, cancellation.Token).AsTask());
        else if (boundary is "security" or "session") await Assert.ThrowsAsync<SecurityConflictException>(() => service.ReadAsync(setup.Tenant, setup.Session, member).AsTask());
        else await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Session, member).AsTask());
        Assert.Single(probe.Seen);
    }
}
