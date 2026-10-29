using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "grantor")] [InlineData(true, "grantor")]
    [InlineData(false, "executor")] [InlineData(true, "executor")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    [InlineData(false, "witness")] [InlineData(true, "witness")]
    [InlineData(false, "pinned_disabled")] [InlineData(true, "pinned_disabled")]
    public async Task Approved_effect_revalidates_authority_policy_and_witness_and_records_a_durable_denial_without_changing_the_persona(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite);
        var setup = await EffectSetup(f, review: boundary == "executor", independentGrantor: boundary == "grantor", managementEnabled: boundary != "pinned_disabled");
        var tenant = setup.Environment.Tenant; var request = setup.Request;
        var before = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, request.Operation.InstanceId))!;
        if (boundary is "grantor" or "executor")
        {
            var principal = boundary == "grantor" ? setup.Grantor.PrincipalId : request.ExecutorPrincipalId;
            var state = (await f.Iam.ReadTenantAsync(tenant))!; var assignment = state.Assignments.Single(a => a.PrincipalId == principal && state.Roles.Single(r => r.RoleId == a.RoleId).Permissions.Contains("agents.update"));
            await f.Access.RevokeAssignmentAsync(setup.Environment.Owner.PrincipalId, tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        else if (boundary is "freeze" or "witness")
        {
            var state = (await f.Iam.ReadTenantAsync(tenant))!; var at = f.Clock.GetUtcNow();
            var change = boundary == "witness" ? new TenantAgentConfigurationChange(before.Instance.InstanceId, before.Instance.Revision,
                before.Instance.Persona with { Name = "Independent current persona" }, before.Instance.PersonaRevision)
                : new TenantAgentConfigurationChange(before.Instance.InstanceId, before.Instance.Revision, HarnessManagement: before.Instance.HarnessManagement! with
                    { Policy = before.Instance.HarnessManagement!.Policy with { Frozen = true }, PolicyRevision = before.Instance.HarnessManagement.PolicyRevision + 1 });
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
                state.Tenant.Revision, change, at, new(f.Ids.NewId(), tenant, setup.Environment.Owner.PrincipalId, "agents.configuration.update", before.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)));
        }
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, request.Operation.InstanceId))!;
        var continuation = await Continuations(f).ResumeAsync(setup.Run);
        var result = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
        Assert.Equal(result.Outcome, continuation.Outcome!.Outcome); Assert.Equal(result.ReasonCode, continuation.Outcome.ReasonCode);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome);
        Assert.Equal(boundary switch { "grantor" => "grantor_authority_lost", "executor" => "operation_authority_changed", "witness" => "operation_witness_changed", _ => "management_unavailable" }, result.ReasonCode);
        Assert.Equal(current.Instance.Persona, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, request.Operation.InstanceId))!.Instance.Persona);
        Assert.Equal(current.Instance.Revision, result.InstanceRevision);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(tenant, request.RequestId);
        if (boundary == "executor") Assert.Null(grant); else Assert.Equal(OperationGrantStatus.Revoked, grant!.Status);
        var replay = await Effects(f).ExecuteAsync(new(tenant, setup.Run.AgentRunId), request.RequestId, request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        Assert.Equal(result, replay); Assert.Single(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == "operations.persona.apply" && a.Decision == "denied");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Wrong_operation_hash_Run_tenant_and_generation_cannot_consume_a_legitimate_grant(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await EffectSetup(f); var request = setup.Request; var service = Effects(f);
        var reference = new TenantRunReference(request.TenantId, setup.Run.AgentRunId);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, setup.Generation, new string('0', 64)).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(reference, request.RequestId, f.Ids.NewId(), setup.Generation, request.Operation.ActionHash).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, f.Ids.NewId(), request.Operation.ActionHash).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ExecuteAsync(new(f.Ids.NewId(), setup.Run.AgentRunId), request.RequestId, request.Operation.OperationId, setup.Generation, request.Operation.ActionHash).AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId));
        Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!.Status);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(request.TenantId, 200), a => a.Action == "operations.persona.apply");
        Assert.Equal(TenantOperationOutcome.Completed, (await service.ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, setup.Generation, request.Operation.ActionHash)).Outcome);
    }
}
