using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_chat_discovery_filters_communication_scope_before_count_and_page_without_granting_admin_reads(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId); var service = f.ChatService();
        var second = await f.Agents.CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "examiner", 1, await Revision(), setup.Agent.Instance.Persona with { Name = "Scoped support", Tone = "private-only-marker" });
        await f.Agents.CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "examiner", 1, await Revision(), setup.Agent.Instance.Persona with { Name = "Hidden match" });
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Configuration reader", ["agents.read"], await Revision());
        var readAssignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, read.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        Assert.Equal(3, (await f.Agents.ListAsync(member.PrincipalId, setup.Tenant)).Total);
        Assert.Equal(0, (await service.ListAgentsAsync(setup.Tenant, member)).Total);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat audience", ["agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var secondAssignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", second.Instance.InstanceId.ToString("D")), await Revision());
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, readAssignment.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Agents.ReadAsync(member.PrincipalId, setup.Tenant, setup.Agent.Instance.InstanceId).AsTask());
        var firstPage = await service.ListAgentsAsync(setup.Tenant, member, 0, 1); var nextPage = await service.ListAgentsAsync(setup.Tenant, member, 1, 1);
        Assert.Equal(2, firstPage.Total); Assert.Equal(2, nextPage.Total); Assert.False(firstPage.CanCreateSessions);
        var actual = firstPage.Items.Concat(nextPage.Items).ToArray();
        Assert.Equal(new[] { setup.Agent.Instance.InstanceId, second.Instance.InstanceId }.Order(), actual.Select(a => a.InstanceId).Order());
        Assert.All(actual, a => { Assert.False(a.VoiceAvailable); Assert.Equal(1, a.Revision); Assert.NotEmpty(a.Name); Assert.NotEmpty(a.Language); });
        Assert.Empty((await service.ListAgentsAsync(setup.Tenant, member, 2, 1)).Items);
        var search = await service.ListAgentsAsync(setup.Tenant, member, 0, 1, query: " sCoPeD ");
        Assert.Equal(1, search.Total); Assert.Equal(second.Instance.InstanceId, Assert.Single(search.Items).InstanceId);
        Assert.Equal(0, (await service.ListAgentsAsync(setup.Tenant, member, query: "Hidden match")).Total);
        Assert.Equal(0, (await service.ListAgentsAsync(setup.Tenant, member, query: "private-only-marker")).Total);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.ListAgentsAsync(setup.Tenant, member, query: new string('x', 201)).AsTask());
        var create = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Start conversations", ["sessions.create", "sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, create.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var current = await service.ListAgentsAsync(setup.Tenant, member); Assert.True(current.CanCreateSessions);
        var selected = current.Items.First(a => a.InstanceId == setup.Agent.Instance.InstanceId);
        var created = await f.Sessions.CreateAsync(setup.Tenant, selected.InstanceId, member, SessionMode.Text, current.SecurityRevision, selected.Revision);
        Assert.Equal(member.PrincipalId, created.Owner.CreatedByPrincipalId); Assert.Equal(setup.Agent.PrincipalId, created.Owner.ExecutorPrincipalId);
        await f.Agents.SetLifecycleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(), 1);
        Assert.Equal(second.Instance.InstanceId, Assert.Single((await service.ListAgentsAsync(setup.Tenant, member)).Items).InstanceId);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, secondAssignment.AssignmentId, await Revision());
        Assert.Equal(0, (await service.ListAgentsAsync(setup.Tenant, member)).Total);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        var betaHuman = setup.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        Assert.Equal(0, (await service.ListAgentsAsync(beta.Tenant.TenantId, betaHuman)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAgentsAsync(beta.Tenant.TenantId, setup.Owner).AsTask());
        foreach (var (page, size) in new[] { (-1, 1), (0, 0), (100001, 1), (0, 101) })
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.ListAgentsAsync(setup.Tenant, member, page, size).AsTask());
        await f.Identity.SignOutAsync(member.AuthenticationSessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAgentsAsync(setup.Tenant, member).AsTask());
    }

    [Theory]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "archive")] [InlineData(true, "archive")]
    public async Task Tenant_chat_discovery_rechecks_human_and_security_after_store_io(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat", ["agents.chat"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var gate = new ChatInventoryGate((ITenantAgentStore)f.Iam);
        var pending = f.ChatService(gate).ListAgentsAsync(setup.Tenant, member).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
            if (boundary == "archive") await f.Agents.SetLifecycleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(), 1);
        }
        finally { gate.Proceed.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => pending);
        Assert.True(error is AccessDeniedException or SecurityConflictException, error?.ToString());
    }

    private sealed class ChatInventoryGate(ITenantAgentStore inner) : ITenantAgentStore
    {
        public ValueTask<IReadOnlyList<Guid>> ListAssistantTenantPageAsync(int page, int limit, CancellationToken ct = default) => inner.ListAssistantTenantPageAsync(page, limit, ct);
        public ValueTask<AgentCore.Domain.Security.AuthorizationAudit?> FindAgentOperationAsync(Guid tenant, Guid operation, CancellationToken ct = default) => inner.FindAgentOperationAsync(tenant, operation, ct);
        public ValueTask<bool> TryProvisionGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, AgentCore.Domain.Definitions.AgentInstance instance, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec> skills, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryProvisionGeneralAssistantAsync(next, expected, instance, skills, audit, ct);
        public ValueTask<TenantAgent?> TryAdoptGeneralAssistantAsync(AgentCore.Domain.Security.TenantSecurityState next, long expected, TenantAgentConfigurationChange change, DateTimeOffset at, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => inner.TryAdoptGeneralAssistantAsync(next, expected, change, at, audit, ct);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default)
        { var value = await inner.ListTenantAgentsAsync(tenantId, allowedInstanceIds, page, pageSize, ct); Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return value; }
        public ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default) => inner.ReadTenantAgentConfigurationAsync(tenantId, instanceId, ct);
        public ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantAgentConfigurationAsync(next, expectedSecurityRevision, change, at, audit, ct);
        public ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default) => inner.FindTenantAgentAsync(tenantId, instanceId, ct);
        public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision, AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TryCreateTenantAgentAsync(next, expectedSecurityRevision, instance, skills, audit, ct, initiator);
        public ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) => inner.TrySetTenantAgentLifecycleAsync(next, expectedSecurityRevision, instanceId, expectedInstanceRevision, lifecycle, at, audit, ct, initiator);
    }
}
