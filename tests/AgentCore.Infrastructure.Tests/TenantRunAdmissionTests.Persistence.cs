using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Owning_admission_rejects_forged_requester_reference_without_input_or_audit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var gate = new Gate((ITenantRunStore)f.Iam); using var cancellation = new CancellationTokenSource();
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var pending = f.RunService(gate).SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Original input", state.Tenant.Revision, 1, cancellation.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var original = gate.Prepared!.Run; var c = original.Admission.ExecutionContext!;
        var forgedContext = new TrustedExecutionContext(c.TenantId, c.AgentPrincipalId, c.AgentPrincipalId, c.SessionId,
            c.RunId, c.ActivationId, c.RootActivationId, c.ParentRunId, c.ParentActivationId, c.Origin);
        var a = original.Admission;
        var forgedRun = AgentRun.Create(original.AgentRunId, original.Owner,
            new(a.Activation, a.DefinitionId, a.DefinitionVersion, a.PinnedPersona, a.ResponseId, a.OutputContract,
                a.ExecutionBudget, a.Configuration, a.ComposerInput, a.TaskIntent, forgedContext), original.PinnedModel, original.MaxAttempts, original.CreatedAtUtc,
            original.PinnedSkillCatalog, original.ActiveSkillKeys);
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var audit = new AgentCore.Domain.Security.AuthorizationAudit(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agent-runs.admit",
            original.AgentRunId.ToString("D"), "allowed", "fixture", f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantRunStore)f.Iam).TryAdmitTenantHumanRunAsync(next, state.Tenant.Revision,
            gate.Prepared with { Run = forgedRun }, setup.Owner, audit).AsTask());
        Assert.Empty(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, setup.Session));
        Assert.Equal(1, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), item => item.Action == "agent-runs.admit");
    }

    [Fact]
    public async Task Sqlite_actual_executor_requester_and_profile_constraints_reject_cross_tenant_or_payload_substitution()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Private input",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Beta", "beta");
        await using var db = f.Contexts!.CreateDbContext();
        var run = receipt.RunId.ToString("D"); var profile = Guid.NewGuid().ToString("D"); var wrongRequester = beta.Users.Single().PrincipalId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET TenantId = {beta.Tenant.TenantId} WHERE AgentRunId = {run}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET ExecutorPrincipalId = {setup.Owner.PrincipalId} WHERE AgentRunId = {run}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET ProfileId = {profile} WHERE AgentRunId = {run}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET OriginalInitiatorPrincipalId = {wrongRequester} WHERE AgentRunId = {run}"));
        var sessionId = setup.Session.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET Status = 'Attached' WHERE SessionId = {sessionId}");
        var securityRevision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var cancelled = await f.RunService().CancelAsync(setup.Tenant, receipt.RunId, setup.Owner, securityRevision, 1);
        Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(SessionStatus.Attached, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Status);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET Status = 'Created' WHERE SessionId = {sessionId}");
        var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == run);
        var payload = JsonNode.Parse(row.PayloadJson)!;
        payload["admission"]!["executionContext"]!["originalInitiatorPrincipalId"] = wrongRequester;
        row.PayloadJson = payload.ToJsonString(); await db.SaveChangesAsync();
        Assert.Equal("SessionPersistenceUnavailable", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId).AsTask())).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Session_deletion_retires_run_grants_and_retained_run_cannot_restore_legacy_visibility(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Private input", await Revision(), 1);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Run read", ["agent-runs.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Owner.PrincipalId, role.RoleId, TenantRunRules.Scope(setup.Tenant, receipt.RunId), await Revision());
        var failure = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Sessions.ChangeAsync(setup.Tenant, setup.Owner,
            new(setup.Session, 2, TenantSessionAction.Delete), receipt.SecurityRevision + 2).AsTask());
        Assert.Equal("session_in_use", failure.ReasonCode);
        var terminal = await f.RunService().CancelAsync(setup.Tenant, receipt.RunId, setup.Owner, await Revision(), 1);
        Assert.Equal(AgentRunStatus.Cancelled, terminal.Status);
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Owner, new(setup.Session, 2, TenantSessionAction.Delete), await Revision());
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId));
        Assert.False(await f.Iam.OwnsResourceAsync(TenantRunRules.Scope(setup.Tenant, receipt.RunId)));
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments, a => a.Scope == TenantRunRules.Scope(setup.Tenant, receipt.RunId));
        Assert.Empty(await f.Memory.ReadHistoryAsync(setup.Session, 0, 10));
    }
}
