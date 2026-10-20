using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "expiry")] [InlineData(true, "expiry")]
    public async Task Durable_decision_commits_current_human_qualification_expiry_CAS_grant_and_signal_atomically(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var reviewer = await User(f, setup, "reviewer@example.test", ["approvals.read", "approvals.approve", "approvals.grant_exception", "agents.update"]);
        var assignment = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments.Single(a => a.PrincipalId == reviewer.PrincipalId);
        var request = (await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Raced persona" }, 1)).Request!;
        var store = (ITenantApprovalStore)f.Iam; var gate = new ApprovalDecisionGate(store); using var cancelled = new CancellationTokenSource();
        var pending = ApprovalService(f, store: gate).DecideAsync(setup.Tenant, request.RequestId, reviewer, true,
            request.Operation.ActionHash, await Revision(), request.Revision, cancelled.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "logout") await f.Identity.SignOutAsync(reviewer.AuthenticationSessionId);
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
            if (boundary == "compete") await ApprovalService(f).DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash, await Revision(), 1);
            if (boundary == "cancel") cancelled.Cancel();
            if (boundary == "expiry") clock.Advance(TimeSpan.FromHours(1));
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "logout") await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        else if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        var saved = (await store.FindApprovalAsync(setup.Tenant, request.RequestId))!;
        Assert.Equal(boundary == "compete" ? TenantApprovalStatus.Rejected : TenantApprovalStatus.Pending, saved.Status);
        Assert.Null(await store.FindOperationGrantAsync(setup.Tenant, request.RequestId));
        Assert.Equal(boundary == "compete" ? 1 : 0, (await store.PendingApprovalSignalsAsync(100)).Count);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "approvals.decide" && a.ActorPrincipalId == reviewer.PrincipalId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Durable_request_creation_rechecks_a_running_generation_lease_at_commit(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f); var generation = f.Ids.NewId(); var at = clock.GetUtcNow();
        var claimed = setup.Run.TakeClaim(generation, at, at.AddMinutes(1));
        if (sqlite)
        {
            await using var db = await f.Contexts!.CreateDbContextAsync();
            var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == claimed.AgentRunId.ToString("D"));
            AgentRunStoreMapping.Apply(row, claimed); await db.SaveChangesAsync();
        }
        else
        {
            var memory = Assert.IsType<InMemoryMemoryStore>(f.Memory);
            lock (memory.AdmissionGate) memory.AgentRuns.Runs[claimed.AgentRunId] = claimed;
        }
        var gate = new ApprovalDecisionGate((ITenantApprovalStore)f.Iam, gateCreation: true);
        var pending = ApprovalService(f, store: gate).PreparePersonaAsync(new(setup.Tenant, claimed.AgentRunId),
            setup.Agent.Instance.Persona with { Name = "Expired generation action" }, claimed.Revision, generation).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); clock.Advance(TimeSpan.FromMinutes(1)); gate.Proceed.TrySetResult();
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => pending); Assert.Equal("execution_claim_expired", error.ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items);
        var retained = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, claimed.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Running, retained.Status); Assert.Equal(claimed.Revision, retained.Revision);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Action == "approvals.request");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_same_root_preparation_returns_the_single_winning_request_without_another_audit_or_signal(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var store = (ITenantApprovalStore)f.Iam;
        var gate = new ApprovalDecisionGate(store, gateCreation: true); var persona = setup.Agent.Instance.Persona with { Name = "One root action" };
        var pending = ApprovalService(f, store: gate).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, 1).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        TenantApprovalPreparationResult winner; long revision;
        try
        {
            winner = await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, 1);
            revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        }
        finally { gate.Proceed.TrySetResult(); }
        var repeated = await pending; Assert.Equal(winner.Request, repeated.Request); Assert.Equal("already_pending", repeated.ReasonCode);
        Assert.Equal(OperationDecisionKind.RequiresApproval, repeated.Decision);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "approvals.request");
        Assert.Single((await store.ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items); Assert.Empty(await store.PendingApprovalSignalsAsync(100));
    }

    private sealed class ApprovalDecisionGate(ITenantApprovalStore inner, bool gateCreation = false) : ITenantApprovalStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expected, TenantApprovalRequest request, long revision,
            TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal, TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default)
        { if (!gateCreation) { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); } return await inner.TryDecideApprovalAsync(next, expected, request, revision, decision, grant, signal, human, audit, ct); }
        public ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenant, Guid id, CancellationToken ct = default) => inner.FindApprovalAsync(tenant, id, ct);
        public ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenant, Guid root, Guid executor, PreparedTenantOperation operation, CancellationToken ct = default) => inner.FindRootApprovalAsync(tenant, root, executor, operation, ct);
        public ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenant, IReadOnlyList<Guid>? requests, IReadOnlyList<Guid>? instances, TenantApprovalQuery query, CancellationToken ct = default, IReadOnlyList<Guid>? credentials = null, TenantSandboxApprovalVisibility? sandboxVisibility = null, TenantBrowserApprovalVisibility? browserVisibility = null, TenantCredentialReadApprovalVisibility? credentialReadVisibility = null, IReadOnlyList<Guid>? artifactCreateSessions = null, TenantArtifactReadApprovalVisibility? artifactReadVisibility = null, TenantWorkspaceReadApprovalVisibility? workspaceReadVisibility = null, TenantWorkspaceMutationApprovalVisibility? workspaceMutationVisibility = null, TenantAttachmentReadApprovalVisibility? attachmentReadVisibility = null, bool backgroundCreationReadable = true, TenantBackgroundReportApprovalVisibility? reportVisibility = null) => inner.ListApprovalsAsync(tenant, requests, instances, query, ct, credentials, sandboxVisibility, browserVisibility, credentialReadVisibility, artifactCreateSessions, artifactReadVisibility, workspaceReadVisibility, workspaceMutationVisibility, attachmentReadVisibility, backgroundCreationReadable, reportVisibility);
        public async ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expected, TenantApprovalRequest request, long revision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default)
        { if (gateCreation) { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); } return await inner.TryCreateApprovalAsync(next, expected, request, revision, generation, audit, ct); }
        public ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default) => inner.PendingApprovalSignalsAsync(limit, ct);
        public ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null) => inner.TryResumeApprovalAsync(signal, at, ct, continuation);
        public ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenant, Guid id, CancellationToken ct = default) => inner.FindOperationGrantAsync(tenant, id, ct);
        public ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.DueApprovalsAsync(at, limit, ct);
        public ValueTask<bool> TryExpireApprovalAsync(Guid tenant, Guid id, long revision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default) => inner.TryExpireApprovalAsync(tenant, id, revision, signal, at, ct);
    }
}
