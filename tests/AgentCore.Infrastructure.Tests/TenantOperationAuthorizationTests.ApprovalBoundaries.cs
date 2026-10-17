using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approval_expiry_persists_a_terminal_result_resumes_once_and_only_a_new_root_can_request_again(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        var service = ApprovalService(f); var store = (ITenantApprovalStore)f.Iam;
        var persona = setup.Agent.Instance.Persona with { Name = "Expiry persona" };
        var request = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, 1)).Request!;
        clock.Advance(TimeSpan.FromHours(1));
        var securityRevision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true,
            request.Operation.ActionHash, securityRevision, request.Revision).AsTask());
        Assert.Equal(1, await service.DispatchPendingAsync()); Assert.Equal(0, await service.DispatchPendingAsync());
        var expired = (await store.FindApprovalAsync(setup.Tenant, request.RequestId))!;
        Assert.Equal(TenantApprovalStatus.Expired, expired.Status); Assert.Null(await store.FindOperationGrantAsync(setup.Tenant, request.RequestId));
        var resumed = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Queued, resumed.Status);
        var suppressed = await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, resumed.Revision);
        Assert.Equal("previously_expired", suppressed.ReasonCode);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Run.SessionId, setup.Owner, f.Ids.NewId(), "Independent retry",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 2);
        var independent = (await service.PreparePersonaAsync(new(setup.Tenant, receipt.RunId), persona, 1)).Request!;
        Assert.NotEqual(request.RootActivationId, independent.RootActivationId); Assert.NotEqual(request.RequestId, independent.RequestId);
        Assert.Equal(TenantApprovalStatus.Pending, independent.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ordinary_review_requires_the_current_underlying_human_permission_and_independence_without_creating_exceptional_grants(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        await Grant(f, setup, setup.Agent.PrincipalId, "Executor persona", ["agents.update"]);
        await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "agents.update", new(OperationApprovalMode.Always, true), await Revision(), 0);
        var reviewer = await User(f, setup, "reviewer@example.test", ["approvals.read", "approvals.approve", "agents.update"]);
        var service = ApprovalService(f);
        var request = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Ordinary review" }, 1)).Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind);
        var securityRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true,
            request.Operation.ActionHash, securityRevision, 1).AsTask());
        var accepted = await service.DecideAsync(setup.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, await Revision(), 1);
        Assert.Equal(TenantApprovalStatus.Approved, accepted.Status);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Material_target_changes_invalidate_approval_but_a_scoped_canceller_can_release_the_wait_without_effect_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var service = ApprovalService(f);
        var request = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Old prepared persona" }, 1)).Request!;
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var at = f.Clock.GetUtcNow();
        var changed = await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
            state.Tenant.Revision, new(setup.Agent.Instance.InstanceId, 1, setup.Agent.Instance.Persona with { Name = "Independent human edit" }, 1), at,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.persona.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at));
        Assert.NotNull(changed);
        var witness = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true,
            request.Operation.ActionHash, witness, 1).AsTask());
        Assert.Equal("operation_witness_changed", conflict.ReasonCode);
        var canceller = await User(f, setup, "canceller@example.test", ["approvals.cancel"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, request.RequestId, canceller).AsTask());
        witness = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var cancelled = await service.CancelAsync(setup.Tenant, request.RequestId, canceller, request.Operation.ActionHash, witness, 1);
        Assert.Equal(TenantApprovalStatus.Cancelled, cancelled.Status); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        Assert.Equal(1, await service.DispatchPendingAsync());
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!;
        Assert.Equal("Independent human edit", current.Instance.Persona.Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Approval_inventory_filters_exact_request_and_underlying_target_authority_before_the_limit(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f); var service = ApprovalService(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var first = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Visible older action" }, 1)).Request!;
        clock.Advance(TimeSpan.FromSeconds(1));
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Run.SessionId, setup.Owner, f.Ids.NewId(), "Another independent request", await Revision(), 2);
        var second = (await service.PreparePersonaAsync(new(setup.Tenant, receipt.RunId), setup.Agent.Instance.Persona with { Name = "Hidden newer action" }, 1)).Request!;
        var viewer = await User(f, setup, "viewer@example.test", ["agents.update"]);
        Assert.Empty(await service.ListAsync(setup.Tenant, viewer, 1));
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact approval viewer", ["approvals.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, viewer.PrincipalId, role.RoleId,
            AccessScope.ForResource(setup.Tenant, "approval", first.RequestId.ToString("D")), await Revision());
        var visible = Assert.Single(await service.ListAsync(setup.Tenant, viewer, 1)); Assert.Equal(first, visible.Request);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, second.RequestId, viewer).AsTask());
        var underlying = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments.Single(a => a.PrincipalId == viewer.PrincipalId && a.Scope.Kind == AccessScopeKind.Tenant);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, underlying.AssignmentId, await Revision());
        Assert.Empty(await service.ListAsync(setup.Tenant, viewer, 1));
    }
}
