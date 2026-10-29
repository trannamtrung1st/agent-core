using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Current_chat_and_session_read_are_independent_and_requester_is_not_the_session_creator(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Session reader", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var service = f.RunService(); var message = f.Ids.NewId(); var revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SubmitTextAsync(setup.Tenant, setup.Session, member, message, "Request", revision, 1).AsTask());
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat", ["agents.chat"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var receipt = await service.SubmitTextAsync(setup.Tenant, setup.Session, member, message, "Request", await Revision(), 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        Assert.Equal(member.PrincipalId, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.NotEqual(setup.Owner.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agent-runs.create", AccessScope.ForTenant(setup.Tenant))).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, receipt.RunId, member).AsTask());
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Exact Run reader", ["agent-runs.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, read.RoleId, TenantRunRules.Scope(setup.Tenant, receipt.RunId), await Revision());
        Assert.Equal(member.PrincipalId, (await service.ReadAsync(setup.Tenant, receipt.RunId, member)).Context.OriginalInitiatorPrincipalId);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        var foreign = setup.Owner with { PrincipalId = beta.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Tenant.TenantId, receipt.RunId, foreign).AsTask());
        Assert.False(await f.Iam.OwnsResourceAsync(TenantRunRules.Scope(beta.Tenant.TenantId, receipt.RunId)));
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
        revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SubmitTextAsync(setup.Tenant, setup.Session, member, f.Ids.NewId(), "Later", revision, 2).AsTask());
        Assert.Single(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
    }

    [Theory]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "archive")] [InlineData(true, "archive")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Prepared_admission_rechecks_authority_and_generations_without_losing_graph_or_false_audit(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat and read", ["agents.chat", "sessions.read"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        var gate = new Gate((ITenantRunStore)f.Iam); using var cancel = new CancellationTokenSource();
        var pending = f.RunService(gate).SubmitTextAsync(setup.Tenant, setup.Session, member, f.Ids.NewId(), "Prepared private input", await Revision(), 1, cancel.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(member.AuthenticationSessionId);
            if (boundary == "archive") await f.Agents.SetLifecycleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(), 1);
            if (boundary == "compete") await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Winning input", await Revision(), 1);
            if (boundary == "cancel") cancel.Cancel();
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else if (boundary == "logout") await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        else await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, gate.Prepared!.Run.AgentRunId));
        Assert.Equal(boundary == "compete" ? 1 : 0, (await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session)).Count);
        Assert.Equal(boundary == "compete" ? 2 : 1, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
        var audit = await f.Iam.ReadAuditAsync(setup.Tenant, 200);
        Assert.DoesNotContain(audit, a => a.Action == "agent-runs.admit" && a.Target == gate.Prepared.Run.AgentRunId.ToString("D"));
        Assert.DoesNotContain("Prepared private input", System.Text.Json.JsonSerializer.Serialize(audit));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_message_admission_pins_requester_and_executor_without_profile_and_is_not_dispatched_by_legacy_workers(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1);
        var service = f.RunService(); var messageId = f.Ids.NewId();
        var receipt = await service.SubmitTextAsync(owner.TenantId, session.Owner.SessionId, actor, messageId, "Inspect this request", await Revision(), 1);
        Assert.True(receipt.Created); Assert.Equal(2, receipt.SessionRevision);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, receipt.RunId))!;
        Assert.Null(run.Owner.ProfileIdOrNull); Assert.Throws<InvalidOperationException>(() => run.ProfileId);
        Assert.Equal(agent.PrincipalId, run.Owner.ExecutorPrincipalId); Assert.Equal(owner.TenantId, run.Owner.TenantId);
        var context = run.Admission.ExecutionContext!;
        Assert.Equal(owner.OwnerPrincipalId, context.OriginalInitiatorPrincipalId); Assert.Equal(agent.PrincipalId, context.AgentPrincipalId);
        Assert.Equal(run.ActivationId, context.RootActivationId); Assert.Null(context.ParentRunId); Assert.Equal(ExecutionOrigin.UserMessage, context.Origin);
        Assert.Equal(agent.Instance.Revision, run.Admission.Configuration!.InstanceRevision);
        Assert.Equal(AgentRunStatus.Queued, (await service.ReadAsync(owner.TenantId, run.AgentRunId, actor)).Status);
        var legacy = f.Contexts is null ? (IAgentRunStore)new InMemoryAgentRunStore((InMemoryMemoryStore)f.Memory, new Diagnostics())
            : new SqliteAgentRunStore(f.Contexts, (SqliteMemoryStore)f.Memory, new Diagnostics());
        Assert.Null(await legacy.GetAsync(run.Owner, run.AgentRunId));
        Assert.Empty(await legacy.ListRunnableAsync(f.Clock.GetUtcNow(), 100));
        Assert.Empty(await f.Memory.ReadHistoryAsync(run.SessionId, 0, 100));
        var ownedSession = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, run.SessionId))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => legacy.AdmitAsync(ownedSession.Snapshot, 1, run).AsTask());
        await f.Memory.RecoverCrashedSessionsAsync();
        Assert.Equal(2, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, run.SessionId))!.Snapshot.Revision);
        Assert.Equal(AgentRunStatus.Queued, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, run.AgentRunId))!.Status);
        var duplicate = await service.SubmitTextAsync(owner.TenantId, run.SessionId, actor, messageId, "Inspect this request", await Revision(), 2);
        Assert.False(duplicate.Created); Assert.Equal(receipt.RunId, duplicate.RunId); Assert.Equal(receipt.EntryId, duplicate.EntryId);
        Assert.Equal(receipt.SecurityRevision, await Revision());
        var currentRevision = await Revision();
        var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => service.SubmitTextAsync(owner.TenantId, run.SessionId, actor, messageId,
            "Changed payload", currentRevision, 2).AsTask());
        Assert.Equal("message_id_conflict", conflict.ReasonCode);
        if (sqlite)
        {
            var reopened = (await new SqliteIamStore(f.Contexts!).FindTenantRunAsync(owner.TenantId, receipt.RunId))!;
            Assert.Equal(context, reopened.Admission.ExecutionContext); Assert.Null(reopened.Owner.ProfileIdOrNull);
        }
    }
    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }

    private sealed record Environment(Guid Tenant, TenantSessionInitiator Owner, TenantAgent Agent, Guid Session);
    private static async Task<Environment> Setup(Fixture f, string template = "examiner", int version = 1)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var agent = await (template == "general-assistant" && version < 36 ? f.AgentsAtRelease(version) : f.Agents)
            .CreateAsync(actor.PrincipalId, owner.TenantId, template, version, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(owner.TenantId, agent.Instance.InstanceId))!;
        var context = await f.Access.SaveRoleAsync(actor.PrincipalId, owner.TenantId, null, "Read exact admission context", ["spaces.read"], (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(actor.PrincipalId, owner.TenantId, agent.PrincipalId, context.RoleId, TenantSpaceService.Scope(owner.TenantId, space.SpaceId), (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision, 1);
        return new(owner.TenantId, actor, agent, session.Owner.SessionId);
    }
    private static async Task<TenantSessionInitiator> Member(Fixture f, Guid tenant, Guid owner)
    {
        var account = await f.Identity.RegisterAsync("run-user@example.test", "Requester", "strong-requester-password");
        var role = await f.Access.SaveRoleAsync(owner, tenant, null, "Directory", ["members.read"], (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var invitation = await f.Access.InviteAsync(owner, tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-requester-password"))!.Session;
        return new(account.AccountId, login.SessionId, user.PrincipalId);
    }
    private sealed class Gate(ITenantRunStore inner) : ITenantRunStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TenantHumanRunAdmission? Prepared;
        public ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid session, CancellationToken ct = default) => inner.ResolveTenantSessionOwnerAsync(session, ct);
        public ValueTask<AgentRun?> FindTenantRunAsync(Guid tenant, Guid run, CancellationToken ct = default) => inner.FindTenantRunAsync(tenant, run, ct);
        public ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenant, Guid session, CancellationToken ct = default) => inner.ListTenantRunIdsAsync(tenant, session, ct);
        public ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expected, Guid run, long revision, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, ConversationEntry? response = null)
            => inner.TryCancelTenantRunAsync(next, expected, run, revision, initiator, at, audit, ct, response);
        public async ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expected, TenantHumanRunAdmission admission,
            TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
        { Prepared = admission; Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TryAdmitTenantHumanRunAsync(next, expected, admission, initiator, audit, ct); }
    }
}
