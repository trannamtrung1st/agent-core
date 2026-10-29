using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static TenantMemoryManagementService HumanMemories(Fixture f) => new((ITenantMemoryManagementStore)f.Iam, f.Iam, f.Identity, f.Ids, TimeProvider.System);
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_exact_forget_and_scope_reset_preserve_independent_copies_and_private_tombstones(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner);
        await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned("FIRST_PRIVATE_VALUE")));
        var next = await MemoryClaim(f, owner, turn.Frame.SessionId); await MemoryCommit(f, next, await MemoryPlan(f, next, Learned("SECOND_PRIVATE_VALUE")));
        var service = HumanMemories(f); var query = new TenantMemoryQuery(MemoryScope.IdentityUser);
        var before = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query); Assert.Equal(2, before.Total); Assert.Equal(2, before.RetainedCount); Assert.True(before.CanReset);
        var source = Assert.Single(before.Items, i => i.Memory.Status == MemoryItemStatus.Active); Assert.True(source.CanForget);
        var forgotten = await service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(query, before.SecurityRevision, source.Memory.MemoryId, source.Memory.Revision));
        Assert.Equal(1, forgotten.ItemsForgotten); Assert.Equal(before.SecurityRevision + 1, forgotten.SecurityRevision);
        var after = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query); Assert.Equal(forgotten.SecurityRevision, after.SecurityRevision);
        var tombstone = Assert.Single(after.Items, i => i.Memory.MemoryId == source.Memory.MemoryId); Assert.Equal(MemoryItemStatus.Deleted, tombstone.Memory.Status);
        Assert.Equal("", tombstone.Memory.Content); Assert.Equal("", tombstone.Memory.Subject); Assert.False(tombstone.CanForget); Assert.Equal(source.Memory.ContentHash, tombstone.Memory.ContentHash);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(query, before.SecurityRevision, source.Memory.MemoryId, source.Memory.Revision)).AsTask());
        var reset = await service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(query, after.SecurityRevision, ExpectedRetainedCount: after.RetainedCount)); Assert.Equal(1, reset.ItemsForgotten);
        var all = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query); Assert.Equal(0, all.RetainedCount); Assert.False(all.CanReset); Assert.All(all.Items, i => Assert.Equal("", i.Memory.Content));
        foreach (var other in new[] { new TenantMemoryQuery(MemoryScope.User), new TenantMemoryQuery(MemoryScope.Session, turn.Frame.SessionId) })
            Assert.Equal(2, (await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, other)).RetainedCount);
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Equal(2, await db.Set<TenantLearnedMemoryRecord>().CountAsync(i => i.Status == (int)MemoryItemStatus.Deleted)); }
    }
    [Theory] [InlineData(false, "count")] [InlineData(true, "count")] [InlineData(false, "revision")] [InlineData(true, "revision")]
    [InlineData(false, "logout")] [InlineData(true, "logout")] [InlineData(false, "foreign-agent")] [InlineData(true, "foreign-agent")]
    public async Task Tenant_human_memory_changed_selection_or_original_login_refuses_without_partial_forgetting(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned()));
        var service = HumanMemories(f); var query = new TenantMemoryQuery(MemoryScope.User); var page = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query);
        var change = new TenantMemoryForget(query, page.SecurityRevision, ExpectedRetainedCount: page.RetainedCount);
        if (boundary == "count") change = change with { ExpectedRetainedCount = 2 };
        if (boundary == "revision") change = change with { ExpectedSecurityRevision = page.SecurityRevision - 1 };
        if (boundary == "logout") await f.Identity.SignOutAsync(owner.Human.AuthenticationSessionId);
        if (boundary is "logout" or "foreign-agent") await Assert.ThrowsAsync<AccessDeniedException>(() => service.ForgetAsync(owner.Tenant,
            boundary == "foreign-agent" ? f.Ids.NewId() : owner.Agent.Instance.InstanceId, owner.Human, change).AsTask());
        else await Assert.ThrowsAsync<SecurityConflictException>(() => service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, change).AsTask());
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Equal(0, await db.Set<TenantLearnedMemoryRecord>().CountAsync(i => i.Status == (int)MemoryItemStatus.Deleted)); }
        else if (boundary != "logout") Assert.Equal(1, (await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query)).RetainedCount);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_pages_and_exact_lookup_cannot_disclose_another_User_even_with_full_raw_reads(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned("OWNER_PRIVATE_FACT")));
        var account = await f.Identity.RegisterAsync("management-peer@example.test", "Peer", "strong-management-peer-password");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Read all source metadata", ["agents.read", "memory.read", "memory.manage", "sessions.read"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-management-peer-password"))!.Session; var peer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        var service = HumanMemories(f);
        foreach (var query in new[] { new TenantMemoryQuery(MemoryScope.IdentityUser), new TenantMemoryQuery(MemoryScope.User), new TenantMemoryQuery(MemoryScope.Session, turn.Frame.SessionId) })
        {
            var own = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query); var row = Assert.Single(own.Items);
            var page = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query); Assert.Empty(page.Items); Assert.Equal(0, page.Total); Assert.Equal(0, page.RetainedCount); Assert.False(page.CanReset);
            Assert.Empty((await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query with { MemoryId = row.Memory.MemoryId })).Items);
            await Assert.ThrowsAsync<SecurityConflictException>(() => service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer,
                new(query, own.SecurityRevision, row.Memory.MemoryId, row.Memory.Revision)).AsTask());
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_forgetting_a_captured_value_fences_the_existing_Run_without_deleting_other_scopes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f); var turn = await MemoryClaim(f, owner); await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned()));
        var active = await MemoryClaim(f, owner); var learned = (ITenantLearnedMemoryStore)f.Iam; var observed = await learned.ReadTenantRunMemoryAsync(active.Frame); var source = Assert.Single(observed.Sources);
        var service = HumanMemories(f); var query = new TenantMemoryQuery(MemoryScope.IdentityUser); var page = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, query); var row = Assert.Single(page.Items);
        // A fresh Session normally ranks Agent-User before organization-wide duplicates.
        Assert.Equal(row.Memory.MemoryId, source.MemoryId);
        await service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(query, page.SecurityRevision, row.Memory.MemoryId, row.Memory.Revision));
        await Assert.ThrowsAsync<AccessDeniedException>(() => learned.RequireTenantMemorySourcesAsync(new(owner.Tenant, active.Run.AgentRunId), [source], active.Frame.Generation).AsTask());
        Assert.Single((await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, owner.Human, new(MemoryScope.User))).Items);
    }
    [Theory] [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_human_memory_actual_owner_still_requires_independent_raw_read_and_manage(bool sqlite, bool read, bool manage)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f);
        var account = await f.Identity.RegisterAsync("raw-memory-peer@example.test", "Peer", "strong-memory-peer-password");
        var permissions = new List<string> { "agents.read", "agents.chat", "sessions.create", "sessions.read" }; if (read) permissions.Add("memory.read"); if (manage) permissions.Add("memory.manage");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Independent human Memory authority", permissions.Union(new[] { "memory.read", "memory.manage" }).ToArray(), (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-memory-peer-password"))!.Session; var peer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        await GrantMemoryContext(f, owner, peer.PrincipalId);
        var turn = await MemoryClaim(f, owner, requester: peer); await MemoryCommit(f, turn, await MemoryPlan(f, turn, Learned()));
        await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, role.RoleId, "Independent human Memory authority", permissions, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var service = HumanMemories(f); var query = new TenantMemoryQuery(MemoryScope.IdentityUser);
        if (!read) { await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query).AsTask()); return; }
        var page = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query); var item = Assert.Single(page.Items); Assert.Equal(manage, item.CanForget); Assert.Equal(manage, page.CanReset);
        var change = new TenantMemoryForget(query, page.SecurityRevision, item.Memory.MemoryId, item.Memory.Revision);
        if (manage) Assert.Equal(1, (await service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, change)).ItemsForgotten);
        else { await Assert.ThrowsAsync<AccessDeniedException>(() => service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, change).AsTask()); Assert.Equal(1, (await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query)).RetainedCount); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_origin_filter_precedes_counts_and_reset_cannot_skip_a_readable_unmanageable_source(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision;
        var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, new SessionNoBuiltinFallback(), f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var second = await agents.CreateFromPublicationAsync(owner.Human.PrincipalId, owner.Tenant, owner.Definition, 1, await Revision());
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, second.PrincipalId, owner.Role, AccessScope.ForTenant(owner.Tenant), await Revision());
        var account = await f.Identity.RegisterAsync("origin-memory-peer@example.test", "Peer", "strong-memory-peer-password");
        var full = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Seed own facts", ["agents.read", "agents.chat", "sessions.create", "sessions.read", "memory.read", "memory.manage"], await Revision());
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, full.RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-memory-peer-password"))!.Session; var peer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        await GrantMemoryContext(f, owner, peer.PrincipalId);
        await GrantMemoryContext(f, owner, second.PrincipalId, second);
        await GrantMemoryContext(f, owner, peer.PrincipalId, second);
        var a = await MemoryClaim(f, owner, requester: peer); await MemoryCommit(f, a, await MemoryPlan(f, a, Learned("FIRST_ORIGIN") with { Subject = "First source" }));
        var b = await MemoryClaim(f, owner, requester: peer, otherAgent: second); await MemoryCommit(f, b, await MemoryPlan(f, b, Learned("SECOND_ORIGIN") with { Subject = "Second source" }));
        var read = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Scoped Memory read", ["agents.read", "memory.read"], await Revision());
        var manage = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Scoped Memory manage", ["memory.manage"], await Revision());
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, peer.PrincipalId, read.RoleId, AccessScope.ForResource(owner.Tenant, "agent", owner.Agent.Instance.InstanceId.ToString("D")), await Revision());
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, peer.PrincipalId, manage.RoleId, AccessScope.ForResource(owner.Tenant, "agent", owner.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var initial = (await f.Iam.ReadTenantAsync(owner.Tenant))!.Assignments.Single(i => i.PrincipalId == peer.PrincipalId && i.RoleId == full.RoleId);
        await f.Access.RevokeAssignmentAsync(owner.Human.PrincipalId, owner.Tenant, initial.AssignmentId, await Revision());
        var service = HumanMemories(f); var query = new TenantMemoryQuery(MemoryScope.User, PageSize: 1);
        var one = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query); Assert.Equal(1, one.Total); Assert.Equal(1, one.RetainedCount); Assert.True(one.CanReset); Assert.Equal("FIRST_ORIGIN", Assert.Single(one.Items).Memory.Content);
        Assert.Empty((await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query with { Page = 2 })).Items);
        var extraRead = await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, peer.PrincipalId, read.RoleId, AccessScope.ForResource(owner.Tenant, "agent", second.Instance.InstanceId.ToString("D")), await Revision());
        var two = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query); Assert.Equal(2, two.Total); Assert.Equal(2, two.RetainedCount); Assert.False(two.CanReset);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, new(query, two.SecurityRevision, ExpectedRetainedCount: 2)).AsTask());
        Assert.Equal(2, (await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query)).RetainedCount);
        await f.Access.RevokeAssignmentAsync(owner.Human.PrincipalId, owner.Tenant, extraRead.AssignmentId, await Revision());
        one = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query); Assert.Equal(1, (await service.ForgetAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, new(query, one.SecurityRevision, ExpectedRetainedCount: 1))).ItemsForgotten);
        await f.Access.AssignRoleAsync(owner.Human.PrincipalId, owner.Tenant, peer.PrincipalId, read.RoleId, AccessScope.ForResource(owner.Tenant, "agent", second.Instance.InstanceId.ToString("D")), await Revision());
        var retained = await service.ReadAsync(owner.Tenant, owner.Agent.Instance.InstanceId, peer, query with { PageSize = 25 }); Assert.Equal(1, retained.RetainedCount); Assert.Equal("SECOND_ORIGIN", Assert.Single(retained.Items, i => i.Memory.Status != MemoryItemStatus.Deleted).Memory.Content);
    }

}
