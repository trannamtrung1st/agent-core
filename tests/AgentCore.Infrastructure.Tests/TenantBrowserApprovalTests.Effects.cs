using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private sealed class EffectProbe(TargetProbe target) : IBrowser, ITenantBrowserCommandTarget
    {
        public Func<Task>? BeforeGate { get; set; }
        public Func<Task>? AfterEffect { get; set; }
        public bool Uncertain { get; set; }
        public int Invocations;
        public int NativeEffects;
        public BrowserProviderDescriptor Provider => target.Provider;
        public BrowserHostPolicy HostPolicy => target.HostPolicy;
        public bool IsAvailable => true;
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default) => target.PrepareTenantCommandAsync(owner, command, ct);
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Invocations);
            Assert.NotNull(request.TenantDispatchCheck); Assert.NotNull(request.TenantWitness);
            if (BeforeGate is { } before) await before();
            var denied = await request.TenantDispatchCheck(ct); if (denied is not null) return new(denied);
            Interlocked.Increment(ref NativeEffects); target.CommandRevision++;
            if (AfterEffect is { } after) await after();
            return Uncertain ? new("close_uncertain", EffectAttempted: true)
                : new(null, DataJson: "{\"scope\":\"owned observation\"}", EffectAttempted: true, EffectConfirmedBySdk: true);
        }
    }
    private static TenantBrowserEffectService Effects(Fixture f, IBrowser browser, IIamStore? current = null, ITenantApprovedOperationStore? effects = null)
    {
        current ??= f.Iam;
        return new(current, (ITenantRunStore)current, (ITenantApprovalStore)current, effects ?? (ITenantApprovedOperationStore)current,
            new((ITenantSessionStore)current, f.RunConfiguration(), current, new(current), browser), new(current), browser, Operations(f, current), f.Ids, f.Clock);
    }
    private static async Task<(Setup Setup, TenantApprovalRequest Request)> ApprovedEffect(Fixture f, Setup s, IBrowser browser, Action? beforeDecision = null)
    {
        var service = Service(f, browser); var request = (await Request(service, s)).Request!;
        beforeDecision?.Invoke();
        var view = await service.ReadViewAsync(s.Tenant, request.RequestId, s.Human);
        request = await service.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await service.DispatchPendingAsync());
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var at = f.Clock.GetUtcNow();
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return (s with { Run = run }, request);
    }
    private static ValueTask<TenantBrowserEffectResult> ExecuteEffect(TenantBrowserEffectService service, Setup s, TenantApprovalRequest request, CancellationToken ct = default) =>
        service.ExecuteAsync(new(s.Tenant, s.Run.AgentRunId), request.RequestId, request.Operation.OperationId, s.Run.Claim!.Generation, request.Operation.ActionHash, ct);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_dispatches_once_and_reopen_retains_receipt_without_replaying_observation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe); var service = Effects(f, probe);
        var result = await ExecuteEffect(service, s, request);
        Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.Equal(TenantBrowserEffectRules.Completed, result.Receipt.ReasonCode);
        Assert.Equal(1, probe.NativeEffects); Assert.NotNull(result.Result); Assert.Contains("owned observation", result.Result.DataJson);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition); Assert.Equal(s.Run.Claim!.Generation, run.SideEffect.ExternalEffect!.Generation);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var duplicate = await ExecuteEffect(Effects(f, probe, reopened), s, request);
        Assert.Equal(result.Receipt, duplicate.Receipt); Assert.Null(duplicate.Result); Assert.Equal("browser_observation_not_retained", duplicate.ObservationUnavailableReason);
        Assert.Equal(1, probe.NativeEffects); Assert.Equal(0, await service.RecoverAsync());
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_native_gate_rechecks_revocation_after_the_durable_start(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeGate = async () => { entered.TrySetResult(); await proceed.Task; };
        var service = Effects(f, probe); var pending = ExecuteEffect(service, s, request).AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, started.SideEffect.Disposition); Assert.NotNull(started.SideEffect.ExternalEffect);
        Assert.Throws<AgentRunTransitionException>(() => started.AcceptBrowserSnapshot(started.Revision, started.Claim!.Generation, f.Clock.GetUtcNow()));
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        proceed.TrySetResult(); var result = await pending;
        Assert.Equal(TenantOperationOutcome.Denied, result.Receipt.Outcome); Assert.Equal("permission_denied", result.Receipt.ReasonCode); Assert.Null(result.Result);
        Assert.Equal(0, probe.NativeEffects); Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_effect_retains_uncertain_attempts_or_withholds_output_after_read_authority_loss(bool sqlite, bool revoke)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s)) { Uncertain = !revoke };
        (s, var request) = await ApprovedEffect(f, s, probe);
        if (revoke) probe.AfterEffect = async () => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var service = Effects(f, probe); var result = await ExecuteEffect(service, s, request);
        Assert.Equal(revoke ? TenantOperationOutcome.Completed : TenantOperationOutcome.Indeterminate, result.Receipt.Outcome);
        Assert.Null(result.Result); Assert.Equal(revoke ? "browser_observation_unavailable" : null, result.ObservationUnavailableReason);
        Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_effect_abandoned_Run_fence_recovers_from_expired_claim_or_deleted_Session_without_native_IO(bool sqlite, bool deleted)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var s = await Prepare(f); var probe = new EffectProbe(Probe(s)); (s, var request) = await ApprovedEffect(f, s, probe);
        var storage = (ITenantApprovedOperationStore)f.Iam; var decision = (await storage.FindApprovalDecisionAsync(s.Tenant, request.RequestId))!;
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, s.Run.AgentRunId, s.Run.Claim!.Generation,
            s.Run.Revision, request.Revision, decision.DecisionId, null, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        var start = (await storage.TryStartBrowserEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantBrowserEffectRules.DispatchAction, request.ResourceId, "allowed", "browser_command_dispatched", clock.GetUtcNow())))!;
        Assert.NotNull(start.Fence); var service = Effects(f, probe); Assert.Equal(0, await service.RecoverAsync());
        var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        AgentRun recovered;
        if (deleted)
        {
            var cancelled = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.RequestCancellation(current.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            recovered = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.CommitCancellation(cancelled.Revision, clock.GetUtcNow(), s.Run.Claim.Generation, null), f.Ids.NewId());
            var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Run.SessionId))!;
            await f.Sessions.ChangeAsync(s.Tenant, s.Human, new(s.Run.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            recovered = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId, new AgentRunCommand.Recover(current.Revision, clock.GetUtcNow()), f.Ids.NewId());
        }
        Assert.Equal(start.Fence.Origin, recovered.SideEffect.ExternalEffect); Assert.Equal(deleted ? AgentRunSideEffectDisposition.InFlight : AgentRunSideEffectDisposition.Indeterminate, recovered.SideEffect.Disposition);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        service = Effects(f, probe, reopened); Assert.Equal(1, await service.RecoverAsync()); Assert.Equal(0, await service.RecoverAsync()); Assert.Equal(0, probe.NativeEffects);
        var receipt = (await ((ITenantApprovedOperationStore)reopened).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(TenantBrowserEffectRules.Uncertain, receipt.ReasonCode);
        if (!deleted) Assert.Equal(start.Fence.Origin, (await ((ITenantRunStore)reopened).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.SideEffect.ExternalEffect);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_concurrent_callers_never_share_native_dispatch_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe); var service = Effects(f, probe);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => {
            try { return await ExecuteEffect(service, s, request); }
            catch (SecurityConflictException) { return null; }
        }));
        Assert.Equal(1, probe.NativeEffects); var completed = results.Where(r => r is not null).ToArray(); Assert.NotEmpty(completed);
        Assert.All(completed, item => Assert.Equal(completed[0]!.Receipt, item!.Receipt));
        Assert.Equal(1, completed.Count(r => r!.Result is not null));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_cancellation_seals_uncertainty_before_late_noncooperative_success(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.AfterEffect = async () => { entered.TrySetResult(); await proceed.Task; returned.TrySetResult(); };
        using var cancel = new CancellationTokenSource(); var service = Effects(f, probe); var pending = ExecuteEffect(service, s, request, cancel.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); proceed.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_expired_consent_after_durable_start_never_calls_native_port(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe, () => clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59)));
        var store = new DelayedBrowserFence((ITenantApprovedOperationStore)f.Iam, () => clock.Advance(TimeSpan.FromSeconds(2)));
        var service = Effects(f, probe, effects: store); var result = await ExecuteEffect(service, s, request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Receipt.Outcome); Assert.Equal("browser_command_not_dispatched", result.Receipt.ReasonCode);
        Assert.Null(result.Result); Assert.Equal(0, probe.Invocations); Assert.Equal(0, probe.NativeEffects); Assert.Equal(1, store.Starts);
        Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(0, probe.Invocations); Assert.Equal(1, store.Starts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_effect_Run_cancellation_preserves_facts_but_withholds_late_observation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe);
        probe.AfterEffect = async () => {
            var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
            await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.RequestCancellation(current.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        };
        var service = Effects(f, probe); var result = await ExecuteEffect(service, s, request);
        Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.Null(result.Result);
        Assert.Equal("browser_observation_unavailable", result.ObservationUnavailableReason); Assert.Equal(1, probe.NativeEffects);
        Assert.True((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.CancellationRequested);
        Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(1, probe.Invocations);
    }

    private sealed class DelayedBrowserFence(ITenantApprovedOperationStore inner, Action afterStart) : ITenantApprovedOperationStore
    {
        public int Starts;
        public ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenant, Guid request, CancellationToken ct = default) => inner.FindApprovalDecisionAsync(tenant, request, ct);
        public ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenant, Guid operation, CancellationToken ct = default) => inner.FindOperationReceiptAsync(tenant, operation, ct);
        public ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCommitLocalEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<IReadOnlyList<TenantBrowserEffectFence>> RecoverableBrowserEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableBrowserEffectsAsync(at, limit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleBrowserEffectAsync(TenantSecurityState next, long revision, TenantBrowserEffectFence fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantBrowserEffectObservation? observation = null) => inner.TrySettleBrowserEffectAsync(next, revision, fence, outcome, reason, audit, recovery, ct, observation);
        public async ValueTask<TenantBrowserEffectStart?> TryStartBrowserEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        {
            var start = await inner.TryStartBrowserEffectAsync(next, revision, commit, audit, ct);
            if (start?.Fence is not null) { Interlocked.Increment(ref Starts); afterStart(); }
            return start;
        }
    }

}
