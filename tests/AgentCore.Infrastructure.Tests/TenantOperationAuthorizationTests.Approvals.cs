using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task Durable_approval_retains_exact_action_across_new_human_session_and_reopen_then_resumes_once(bool sqlite, bool approve)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var service = ApprovalService(f); var store = (ITenantApprovalStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var persona = setup.Agent.Instance.Persona with { Name = "Reviewed persona" };
        var prepared = await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, setup.Run.Revision);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); Assert.Equal(ApprovalKind.ExceptionalGrant, request.Kind);
        Assert.Equal(setup.Run.Admission.Activation.RootActivationId, request.RootActivationId);
        Assert.Equal(setup.Owner.PrincipalId, request.OriginalInitiatorPrincipalId);
        var waiting = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim); Assert.Null(waiting.Approval);
        Assert.Equal(request.RequestId, waiting.ApprovalRequestId); Assert.Equal(0, waiting.AttemptCount);
        var securityRevision = await Revision();
        var duplicate = await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, setup.Run.Revision);
        Assert.Equal(OperationDecisionKind.RequiresApproval, duplicate.Decision);
        Assert.Equal("already_pending", duplicate.ReasonCode); Assert.Equal(request, duplicate.Request); Assert.Equal(securityRevision, await Revision());
        Assert.Empty(await store.PendingApprovalSignalsAsync(10));
        await f.Identity.SignOutAsync(setup.Owner.AuthenticationSessionId);
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var reviewer = setup.Owner with { AuthenticationSessionId = login.SessionId };
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!) : f.Iam;
        store = (ITenantApprovalStore)reopened; service = ApprovalService(f, reopened);
        var retained = await service.ReadAsync(setup.Tenant, request.RequestId, reviewer);
        Assert.Equal(request, retained);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(setup.Tenant, request.RequestId, reviewer,
            approve, new string('0', 64), securityRevision, request.Revision).AsTask());
        Assert.Empty(await store.PendingApprovalSignalsAsync(10));
        var decided = await service.DecideAsync(setup.Tenant, request.RequestId, reviewer, approve, request.Operation.ActionHash, securityRevision, request.Revision);
        Assert.Equal(approve ? TenantApprovalStatus.Approved : TenantApprovalStatus.Rejected, decided.Status);
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!.Status);
        var signal = Assert.Single(await store.PendingApprovalSignalsAsync(10));
        var grant = await store.FindOperationGrantAsync(setup.Tenant, request.RequestId);
        if (approve)
        {
            Assert.NotNull(grant); Assert.Equal(request.Operation.OperationId, grant.OperationId); Assert.Equal(request.ExecutorPrincipalId, grant.ExecutorPrincipalId);
            Assert.Equal(request.Operation.ActionHash, grant.ActionHash); Assert.Equal(OperationGrantStatus.Available, grant.Status);
            Assert.Equal("operation:" + request.Operation.OperationId.ToString("D"), grant.EffectIdempotencyKey);
        }
        else Assert.Null(grant);
        Assert.Equal(1, await service.DispatchPendingAsync()); Assert.Equal(0, await service.DispatchPendingAsync());
        Assert.False(await store.TryResumeApprovalAsync(signal, f.Clock.GetUtcNow()));
        var resumed = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Queued, resumed.Status); Assert.Equal(request.RequestId, resumed.ApprovalRequestId); Assert.Equal(3, resumed.Revision);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.DecideAsync(setup.Tenant, request.RequestId, reviewer,
            approve, request.Operation.ActionHash, expectedSecurityRevision: 0, expectedRequestRevision: request.Revision).AsTask());
        var suppressed = await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), persona, resumed.Revision);
        Assert.Equal(approve ? "already_approved" : "previously_rejected", suppressed.ReasonCode);
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "approvals.request");
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "approvals.decide");
        Assert.Equal(setup.Agent.Instance.Persona, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Persona);
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments, a => a.PrincipalId == setup.Agent.PrincipalId);
    }

    private static TenantApprovalService ApprovalService(Fixture f, IIamStore? security = null, ITenantApprovalStore? store = null)
    {
        var current = security ?? f.Iam; var authorizer = new IamAuthorizer(current);
        return new(current, store ?? (ITenantApprovalStore)current, (ITenantAgentStore)current,
            new(current, (ITenantRunStore)current, (ITenantAgentStore)current, authorizer), authorizer, f.Identity, f.Ids, f.Clock, current as ITenantApprovedOperationStore);
    }
}
