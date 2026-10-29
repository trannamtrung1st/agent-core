using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Governance_read_is_independent_bounded_coherent_and_revoked_without_policy_or_audit_mutation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var reader = await User(f, setup, "governance-reader@example.test", ["tenant.governance.read"]);
        var governor = await User(f, setup, "governance-manager@example.test", ["tenant.governance.manage"]);
        var service = Policies(f); var before = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var page = await service.ListAsync(setup.Tenant, reader, pageSize: 100);
        Assert.Equal(PermissionCatalog.All.Count(p => !p.SystemOnly), page.Total);
        var tail = await service.ListAsync(setup.Tenant, reader, page: 1, pageSize: 100);
        Assert.Equal(page.Total, page.Items.Count + tail.Items.Count);
        Assert.Equal(PermissionCatalog.All.Where(p => !p.SystemOnly).Select(p => p.Key).Order(StringComparer.Ordinal),
            page.Items.Concat(tail.Items).Select(p => p.Permission)); Assert.All(page.Items, p => { Assert.False(p.CanManage); Assert.Equal(before.Tenant.Revision, p.SecurityRevision); });
        Assert.Equal(page.Items.OrderBy(p => p.Permission, StringComparer.Ordinal), page.Items);
        Assert.True(page.Items.Single(p => p.Permission == "tenant.governance.manage").HumanOnly);
        Assert.False(page.Items.Single(p => p.Permission == "browser.use").Delegatable);
        Assert.Equal(page.Items.Take(2), (await service.ListAsync(setup.Tenant, reader, pageSize: 2)).Items);
        Assert.Equal(page.Items.Skip(2).Take(2), (await service.ListAsync(setup.Tenant, reader, page: 1, pageSize: 2)).Items);
        Assert.Empty((await service.ListAsync(setup.Tenant, reader, search: "system.")).Items);
        Assert.Equal("agents.update", Assert.Single((await service.ListAsync(setup.Tenant, reader, search: "AGENTS.UPDATE")).Items).Permission);
        var view = await service.ReadAsync(setup.Tenant, reader, "agents.read"); Assert.False(view.CanManage);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveAsync(setup.Tenant, reader, "agents.read", new(OperationApprovalMode.Always, false), before.Tenant.Revision, 0).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, governor, "agents.read").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(setup.Tenant, reader, pageSize: 101).AsTask());
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "tenant.operation_policy.update");
        var saved = await service.SaveAsync(setup.Tenant, governor, "agents.read", new(OperationApprovalMode.Always, true), before.Tenant.Revision, 0);
        Assert.True(saved.CanManage);
        Assert.Equal(saved.Effective, (await service.ReadAsync(setup.Tenant, reader, "agents.read")).Effective);
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, state.Assignments.Single(a => a.PrincipalId == reader.PrincipalId).AssignmentId, state.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, reader).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, reader, "agents.read").AsTask());
        var ownerView = await service.ReadAsync(setup.Tenant, setup.Owner, "agents.read");
        await service.SaveAsync(setup.Tenant, setup.Owner, "agents.read", ownerView.Minimum, ownerView.SecurityRevision, ownerView.PolicyRevision);
        Assert.Equal(OperationApprovalMode.None, (await service.ReadAsync(setup.Tenant, setup.Owner, "agents.read")).Effective.Mode);
    }
}
