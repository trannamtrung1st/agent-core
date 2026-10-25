using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_configuration_commit_rejects_unnormalized_policy_without_state_or_audit_effect(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var before = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var at = f.Clock.GetUtcNow();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(
            before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } }, before.Tenant.Revision,
            new(setup.Agent.Instance.InstanceId, setup.Agent.Instance.Revision, HarnessManagement: new(HarnessManagementPolicy.Disabled)), at,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.management.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)).AsTask());
        Assert.Contains("Canonical self-management overrides", error.Message);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var retained = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance;
        Assert.Equal(setup.Agent.Instance.Revision, retained.Revision); Assert.Null(retained.HarnessManagement);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), row => row.Action == "agents.management.update");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_configuration_commit_cannot_create_retired_Harness_preparation_or_approval_state(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var before = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var preparation = new HarnessPreparation(f.Ids.NewId(), f.Ids.NewId(), setup.Agent.Instance.ActiveVersion, "Forged preparation", 1,
            HarnessPreparationStatus.Ready,
            [new(f.Ids.NewId(), new string('a', 64), new("instructions.update", 1, Content: "Unapproved private instructions"), "Approved")], []);
        var proposed = new HarnessManagementState(new(HarnessManagementMode.Managed, [HarnessManagementScope.Settings], [], []), Preparation: preparation);
        var at = f.Clock.GetUtcNow();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(
            before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } }, before.Tenant.Revision,
            new(setup.Agent.Instance.InstanceId, setup.Agent.Instance.Revision, HarnessManagement: proposed), at,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.management.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)).AsTask());
        Assert.Contains("Historical Harness preparation", error.Message);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var retained = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance;
        Assert.Equal(setup.Agent.Instance.Revision, retained.Revision); Assert.Null(retained.HarnessManagement);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), row => row.Action == "agents.management.update");
    }
}
