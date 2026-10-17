using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    public static IEnumerable<object[]> AgentAtomicHumanCases =>
        from sqlite in new[] { false, true }
        from operation in new[] { "configuration", "creation", "lifecycle" }
        from retirement in new[] { "logout", "relogin", "password", "wrong-principal" }
        select new object[] { sqlite, operation, retirement };

    [Theory]
    [MemberData(nameof(AgentAtomicHumanCases))]
    public async Task Atomic_agent_writes_validate_original_human_without_partial_state_or_audit(bool sqlite, string operation, string retirement)
    {
        await using var f = await Fixture.Create(sqlite);
        const string login = "owner@example.test", password = "strong-owner-password", replacement = "replacement-owner-password";
        var owner = await f.Identity.BootstrapAsync(login, "Owner", password, "Alpha", "alpha");
        var (agents, _) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!;
        var session = (await f.Identity.SignInAsync(login, password))!.Session;
        HumanSessionReference human = new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var store = (ITenantAgentStore)f.Iam; var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var audits = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count; var now = DateTimeOffset.UtcNow;
        if (retirement == "password") await f.Identity.ChangePasswordAsync(owner.AccountId, password, replacement);
        else if (retirement == "wrong-principal") human = human with { PrincipalId = f.Ids.NewId() };
        else
        {
            await f.Identity.SignOutAsync(session.SessionId);
            if (retirement == "relogin") Assert.NotNull(await f.Identity.SignInAsync(login, password));
        }
        async Task<TenantAgent?> Execute(HumanSessionReference witness)
        {
            if (operation == "creation") return await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, before.Tenant.Revision, initiator: witness);
            if (operation == "lifecycle") return await agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, id, AgentInstanceLifecycle.Archived, before.Tenant.Revision, 1, initiator: witness);
            // Bypass the Application precheck: this must be enforced inside the actual transaction.
            var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
            var change = new TenantAgentConfigurationChange(id, 1, agent.Instance.Persona with { Name = "Atomic human saved" }, 1, Initiator: witness);
            return await store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, change, now,
                new(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, "agents.persona.update", id.ToString("D"), "allowed", "test", now));
        }
        Assert.Equal("initiator_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => Execute(human))).ReasonCode);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(before.SystemIdentities.Count, (await f.Iam.ReadTenantAsync(owner.TenantId))!.SystemIdentities.Count);
        Assert.Equal(agent, await store.FindTenantAgentAsync(owner.TenantId, id));
        Assert.Equal(audits, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        var fresh = (await f.Identity.SignInAsync(login, retirement == "password" ? replacement : password))!.Session;
        var saved = (await Execute(new(owner.AccountId, fresh.SessionId, owner.OwnerPrincipalId)))!;
        Assert.Equal(before.Tenant.Revision + 1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(audits + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(operation == "creation" ? 1 : 2, saved.Instance.Revision);
        if (sqlite)
        {
            var committed = (await store.FindTenantAgentAsync(owner.TenantId, saved.Instance.InstanceId))!;
            Assert.Equal(saved.Instance.Revision, committed.Instance.Revision); Assert.Equal(saved.Instance.Persona, committed.Instance.Persona); Assert.Equal(saved.Instance.Lifecycle, committed.Instance.Lifecycle);
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(committed, await reopened.FindTenantAgentAsync(owner.TenantId, saved.Instance.InstanceId));
            Assert.Equal(before.Tenant.Revision + 1, (await reopened.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
            Assert.Equal(audits + 1, (await reopened.ReadAuditAsync(owner.TenantId, 200)).Count);
        }
    }
}
