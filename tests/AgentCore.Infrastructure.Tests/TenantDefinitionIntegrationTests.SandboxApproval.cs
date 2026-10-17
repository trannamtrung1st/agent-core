using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json.Nodes;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantApprovalService Approvals, TenantSandboxApprovedOperationService Execution) SandboxApprovalServices(Fixture f, RuntimeSandboxSetup setup, IIamStore? reopened = null)
    {
        var current = reopened ?? f.Iam; var authorizer = new IamAuthorizer(current); var p = SetupProfiles(f);
        var operations = new TenantOperationAuthorizer(current, (ITenantRunStore)current, (ITenantAgentStore)current, authorizer,
            new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var target = new TenantSandboxOperationTarget((ITenantSessionStore)current, (ITenantSandboxProfileStore)current, (ITenantSandboxRuntimeStore)current,
            setup.Resolver, authorizer, current, p.Images, p.Ready, TimeProvider.System);
        return (new(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, authorizer, f.Identity, f.Ids, TimeProvider.System,
                setup.Resolver, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, sandboxTargets: target),
            new((ITenantRunStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, (ITenantSandboxRuntimeStore)current, target, operations, f.Ids, TimeProvider.System));
    }
    private static TenantSessionInitiator SandboxHuman(RuntimeSandboxSetup s) => new(s.Human.AccountId, s.Human.AuthenticationSessionId, s.Human.PrincipalId);
    private static async Task ReadySandbox(Fixture f, RuntimeSandboxSetup s)
    {
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_exact_approval_pauses_Run_records_human_review_and_reserves_only_one_owned_command(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var services = SandboxApprovalServices(f, s);
        var prepared = await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf 'exact approved command'", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = prepared.Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("sandbox.execute", request.Permission);
        Assert.Equal(2, request.Operation.SandboxCommand!.ExpectedLeaseRevision); Assert.False(request.Snapshot!.Sandbox!.WillInitialize);
        var runs = (ITenantRunStore)f.Iam; var waiting = (await runs.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        Assert.True(view.Review!.CanDecide); Assert.Contains(view.Review.Changes, c => c.Field == "command");
        var approved = await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash,
            view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        Assert.Equal(1, await services.Approvals.DispatchPendingAsync());
        var queued = (await runs.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var at = DateTimeOffset.UtcNow;
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        if (sqlite) services = SandboxApprovalServices(f, s, new SqliteIamStore(f.Contexts!, f.Content));
        var started = await services.Execution.ReserveAsync(frame, approved.RequestId);
        Assert.True(started.Reserved); Assert.Equal(approved.Operation.OperationId, started.Execution.OperationId);
        Assert.Equal(request.Operation.ActionHash, started.Execution.ActionHash); Assert.Equal("printf 'exact approved command'", started.Execution.CommandText);
        var duplicate = await services.Execution.ReserveAsync(frame, approved.RequestId);
        Assert.False(duplicate.Reserved); Assert.Equal(started.Execution.Id, duplicate.Execution.Id);
        var current = (await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, current.SideEffect.Disposition);
        Assert.Equal("operation:" + approved.Operation.OperationId.ToString("D"), current.SideEffect.ToolCallId);
        var saved = (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(started.Execution, saved.Execution); Assert.Equal(started.Execution.Id, saved.Lease!.ActiveExecutionId);
        Assert.Null(await f.Sandbox.GetExecutionAsync(s.Session.SessionId, started.Execution.Id));
        Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "sandbox.command.reserve");
    }
    private static async Task<TenantRuntimeFrame> ResumeSandboxApproval(Fixture f, RuntimeSandboxSetup s, TenantApprovalService service)
    {
        Assert.Equal(1, await service.DispatchPendingAsync());
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; var at = DateTimeOffset.UtcNow;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_approved_concurrent_reservation_has_one_winner_and_cannot_promote_raw_metadata(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var services = SandboxApprovalServices(f, s);
        var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new(null, ["printf", "line one\nline two"], TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => services.Execution.ReserveAsync(frame, request.RequestId).AsTask()));
        var winner = Assert.Single(results, r => r.Reserved); Assert.All(results, r => Assert.Equal(winner.Execution.Id, r.Execution.Id));
        var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantSandboxRuntimeStore)f.Iam).TryReserveTenantSandboxExecutionAsync(
            new(frame, 0, current.Revision), winner.Execution with { Id = f.Ids.NewId() }, DateTimeOffset.UtcNow).AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId)); // Reservation is not completion.
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_rejection_or_changed_generation_cannot_reserve_the_approved_command(bool sqlite, bool change)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var services = SandboxApprovalServices(f, s); var storage = (ITenantSandboxRuntimeStore)f.Iam;
        var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf never-repeat", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        if (change)
        {
            var lease = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease with { Revision = 3, State = SandboxLeaseState.Terminated }, 2));
            var stale = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.False(stale.Review!.CanDecide);
            await Assert.ThrowsAsync<SecurityConflictException>(() => services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true,
                request.Operation.ActionHash, stale.SecurityRevision, request.Revision).AsTask());
            await services.Approvals.CancelAsync(s.Tenant, request.RequestId, SandboxHuman(s), request.Operation.ActionHash, stale.SecurityRevision, request.Revision);
        }
        else await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), false, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        await Assert.ThrowsAsync<SecurityConflictException>(() => services.Execution.ReserveAsync(frame, request.RequestId).AsTask());
        Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_review_filters_counts_by_exact_profile_Session_and_execution_scopes_and_rechecks_grantor(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var services = SandboxApprovalServices(f, s);
        var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf protected-preview", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var minimal = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Review inventory", ["approvals.read"], await Revision());
        var account = await f.Identity.RegisterAsync("sandbox-reviewer@example.test", "Reviewer", "strong-reviewer-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, minimal.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        var human = await SetupHuman(f, account.AccountId, member.PrincipalId, account.LoginKey, "strong-reviewer-password");
        var reviewer = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId);
        async Task<Guid> Grant(string name, string[] permissions, AccessScope scope) {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, reviewer.PrincipalId, role.RoleId, scope, await Revision())).AssignmentId;
        }
        var agentScope = AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D"));
        var grantor = await Grant("Execute and approve one", ["sandbox.execute", "approvals.approve"], agentScope);
        Assert.Equal(0, (await services.Approvals.ListAsync(s.Tenant, reviewer, new())).Total);
        await Grant("Read one Session", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        Assert.Equal(0, (await services.Approvals.ListAsync(s.Tenant, reviewer, new())).Total);
        var profile = await Grant("Read exact source", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        Assert.Equal(1, (await services.Approvals.ListAsync(s.Tenant, reviewer, new())).Total);
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.True(view.Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, profile, await Revision());
        Assert.Equal(0, (await services.Approvals.ListAsync(s.Tenant, reviewer, new())).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer).AsTask());
        await Grant("Restore exact source", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, grantor, await Revision());
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => services.Execution.ReserveAsync(frame, request.RequestId).AsTask());
        Assert.Equal("grantor_authority_lost", denied.ReasonCode);
        Assert.Null((await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }

    [Fact]
    public async Task Tenant_Sandbox_approval_SQLite_constraints_bind_owned_profile_and_exact_review_receipt()
    {
        await using var f = await Fixture.Create(true); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var services = SandboxApprovalServices(f, s);
        var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf constrained", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        await using var db = f.Contexts!.CreateDbContext();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET SandboxProfileRevision=NULL WHERE RequestId={request.RequestId}"));
        var saved = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(); var json = JsonNode.Parse(saved.PayloadJson)!;
        json["Operation"]!["SandboxCommand"]!["ProfileId"] = "missing-owned-profile"; var changed = json.ToJsonString();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET SandboxProfileId={"missing-owned-profile"}, PayloadJson={changed} WHERE RequestId={request.RequestId}"));
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals); var start = await services.Execution.ReserveAsync(frame, request.RequestId);
        var executionId = start.Execution.Id.ToString("D"); var unknown = f.Ids.NewId();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxExecutions SET ApprovalRequestId=NULL WHERE Id={executionId}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SandboxExecutions SET OperationId={unknown} WHERE Id={executionId}"));
        Assert.Equal(start.Execution, (await ((ITenantSandboxRuntimeStore)new SqliteIamStore(f.Contexts!, f.Content)).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }
    [Fact]
    public async Task Tenant_Sandbox_approval_migration_preserves_prior_persona_wait_and_exact_payload_bytes()
    {
        await using var f = await Fixture.Create(true); var s = await PrepareRuntimeSandbox(f); var service = SandboxApprovalServices(f, s).Approvals;
        var prepared = await service.PreparePersonaAsync(new(s.Tenant, s.Run.AgentRunId), new("Legacy saved name", "Legacy role", "Legacy description", "Clear"), s.Run.Revision, s.Run.Claim!.Generation);
        var request = prepared.Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var row = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(); var json = JsonNode.Parse(row.PayloadJson)!;
            json["Operation"]!.AsObject().Remove("SandboxCommand"); json["Snapshot"]!.AsObject().Remove("Sandbox"); var priorJson = json.ToJsonString();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson={priorJson} WHERE RequestId={request.RequestId}");
            await db.GetService<IMigrator>().MigrateAsync("20261017213309_TenantSandboxRuntimeOwnership");
            await db.Database.MigrateAsync();
            Assert.Equal(priorJson, (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync()).PayloadJson);
        }
        var reopened = new SqliteIamStore(f.Contexts!, f.Content);
        Assert.Equal(request, await reopened.FindApprovalAsync(s.Tenant, request.RequestId));
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await reopened.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_private_source_rotation_invalidates_review_without_secret_or_command_audit_content(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "sandbox-approval-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            var credentials = new TenantCredentialService(f.Iam, (ITenantCredentialStore)f.Iam, f.Authorizer,
                new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(root), f.Ids, TimeProvider.System);
            var s = await PrepareRuntimeSandbox(f, registry: async (tenant, human) =>
                (await credentials.CreateAsync(human.PrincipalId, tenant, new("Registry", AgentCore.Domain.Credentials.CredentialKind.Token,
                    AgentCore.Domain.Credentials.CredentialPurpose.ContainerRegistry, null, null, "PRIVATE_SOURCE_ORIGINAL",
                    ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"])), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision)).Credential.CredentialId);
            await ReadySandbox(f, s); var service = SandboxApprovalServices(f, s).Approvals;
            var request = (await service.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf PRIVATE_COMMAND_PREVIEW", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await service.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.True(view.Review!.CanDecide);
            await credentials.ReplaceAsync(s.Human.PrincipalId, s.Tenant, s.Lease.Profile.RegistryCredentialId!.Value, "PRIVATE_SOURCE_ROTATED", view.SecurityRevision, 1);
            view = await service.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.False(view.Review!.CanDecide);
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash,
                view.SecurityRevision, request.Revision).AsTask());
            await service.CancelAsync(s.Tenant, request.RequestId, SandboxHuman(s), request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var audit = System.Text.Json.JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(s.Tenant, 200));
            Assert.DoesNotContain("PRIVATE_SOURCE_ORIGINAL", audit); Assert.DoesNotContain("PRIVATE_SOURCE_ROTATED", audit); Assert.DoesNotContain("PRIVATE_COMMAND_PREVIEW", audit);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_expired_generation_is_refused_before_creating_review_or_releasing_claim(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var storage = (ITenantSandboxRuntimeStore)f.Iam; var lease = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease with { Revision = 3, ExpiresAt = lease.CreatedAt.AddTicks(1) }, 2));
        var service = SandboxApprovalServices(f, s).Approvals;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => service.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf no-expired-dispatch", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation).AsTask());
        Assert.Equal("sandbox_recovery_required", denied.ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items);
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
    }

}
