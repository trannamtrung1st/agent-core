using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "account")] [InlineData(true, "account")]
    [InlineData(false, "role")] [InlineData(true, "role")]
    [InlineData(false, "lease")] [InlineData(true, "lease")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    public async Task Exact_effect_commit_fences_global_account_status_IAM_changes_lease_cancellation_and_competition(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await EffectSetup(f, independentGrantor: true);
        var request = setup.Request; var store = (ITenantApprovedOperationStore)f.Iam; var gate = new PersonaEffectGate(store); using var cancellation = new CancellationTokenSource();
        var reference = new TenantRunReference(request.TenantId, setup.Run.AgentRunId);
        var pending = Effects(f, effects: gate).ExecuteAsync(reference, request.RequestId, request.Operation.OperationId,
            setup.Generation, request.Operation.ActionHash, cancellation.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        TenantOperationReceipt? winning = null;
        try
        {
            if (boundary == "account")
            {
                var account = (await f.Iam.ReadAccountAsync(setup.Grantor.AccountId))!;
                var actor = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Environment.Owner.AccountId);
                Assert.True(await f.Iam.TryUpdateAccountAsync(account with { Status = IdentityStatus.Suspended, SecurityStamp = f.Ids.NewId().ToString("N") }, account.SecurityStamp,
                    new(f.Ids.NewId(), null, actor.PrincipalId, "system.accounts.manage", account.AccountId.ToString("D"), "allowed", "account_suspended", clock.GetUtcNow())));
            }
            if (boundary == "role")
            {
                var state = (await f.Iam.ReadTenantAsync(request.TenantId))!; var assignment = state.Assignments.Single(a => a.PrincipalId == setup.Grantor.PrincipalId);
                await f.Access.RevokeAssignmentAsync(setup.Environment.Owner.PrincipalId, request.TenantId, assignment.AssignmentId, state.Tenant.Revision);
            }
            if (boundary == "lease") clock.Advance(TimeSpan.FromMinutes(5));
            if (boundary == "cancel") cancellation.Cancel();
            if (boundary == "compete") winning = await Effects(f).ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else if (boundary is "lease" or "role") await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        else if (boundary == "compete") Assert.Equal(winning, await pending);
        else { var denied = await pending; Assert.Equal(TenantOperationOutcome.Denied, denied.Outcome); Assert.Equal("grantor_authority_lost", denied.ReasonCode); }
        var grant = (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!;
        var receipt = await store.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId);
        var target = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId))!;
        Assert.Equal(boundary == "compete" ? request.Operation.Persona!.Name : setup.Environment.Agent.Instance.Persona.Name, target.Instance.Persona.Name);
        if (boundary == "compete")
        { Assert.Equal(OperationGrantStatus.Consumed, grant.Status); Assert.NotNull(receipt); Assert.Single(await f.Iam.ReadAuditAsync(request.TenantId, 200), a => a.Action == "operations.persona.apply"); }
        else if (boundary == "account")
        { Assert.Equal(OperationGrantStatus.Revoked, grant.Status); Assert.NotNull(receipt); Assert.Equal(2, target.Instance.Revision); }
        else
        {
            Assert.Equal(OperationGrantStatus.Available, grant.Status); Assert.Null(receipt);
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(request.TenantId, 200), a => a.Action == "operations.persona.apply");
            if (boundary == "role")
            {
                var denied = await Effects(f).ExecuteAsync(reference, request.RequestId, request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
                Assert.Equal(TenantOperationOutcome.Denied, denied.Outcome); Assert.Equal("grantor_authority_lost", denied.ReasonCode);
                Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!.Status);
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Expired_exact_grant_is_revoked_at_commit_under_a_fresh_live_recovery_claim_without_an_effect(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await EffectSetup(f); var request = setup.Request;
        clock.Advance(TimeSpan.FromMinutes(59)); var generation = f.Ids.NewId(); var at = clock.GetUtcNow();
        var recovered = setup.Run.RecoverExpiredClaim(at).TakeClaim(generation, at, at.AddMinutes(5));
        await SaveEffectTestRun(f, recovered);
        var gate = new PersonaEffectGate((ITenantApprovedOperationStore)f.Iam);
        var pending = Effects(f, effects: gate).ExecuteAsync(new(request.TenantId, recovered.AgentRunId), request.RequestId,
            request.Operation.OperationId, generation, request.Operation.ActionHash).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); clock.Advance(TimeSpan.FromMinutes(2)); gate.Proceed.TrySetResult();
        var receipt = await pending; Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("grant_expired", receipt.ReasonCode);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId))!.Status);
        var target = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId))!;
        Assert.Equal(2, target.Instance.Revision); Assert.Equal(setup.Environment.Agent.Instance.Persona, target.Instance.Persona);
    }

    private sealed class PersonaEffectGate(ITenantApprovedOperationStore inner) : ITenantApprovedOperationStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenant, Guid request, CancellationToken ct = default) => inner.FindApprovalDecisionAsync(tenant, request, ct);
        public ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenant, Guid operation, CancellationToken ct = default) => inner.FindOperationReceiptAsync(tenant, operation, ct);
        public ValueTask<TenantBrowserEffectStart?> TryStartBrowserEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryStartBrowserEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<IReadOnlyList<TenantBrowserEffectFence>> RecoverableBrowserEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableBrowserEffectsAsync(at, limit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleBrowserEffectAsync(TenantSecurityState next, long revision, TenantBrowserEffectFence fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default) => inner.TrySettleBrowserEffectAsync(next, revision, fence, outcome, reason, audit, recovery, ct);
        public async ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expected, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TryCommitLocalEffectAsync(next, expected, commit, audit, ct); }
    }
}
