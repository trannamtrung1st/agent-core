using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(true, false)]
    public async Task Exact_Artifact_Review_or_grant_commits_only_the_saved_bytes_with_one_receipt_and_no_read_or_role_grant(bool sqlite, bool review)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup, review ? ["sessions.read", "artifacts.create"] : ["sessions.read"]);
        if (review) await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "artifacts.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        const string args = """{"displayName":"reviewed.md","content":"# Exact reviewed file\nUTF-8: thử nghiệm"}""";
        var approval = ApprovalService(f); var prepared = await approval.PrepareArtifactAsync(new(setup.Tenant, frame.RunId), args, run.Revision, frame.Generation);
        var request = Assert.IsType<TenantApprovalRequest>(prepared.Request); Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        Assert.Equal("artifacts.create", request.Permission); Assert.Equal("session", request.ResourceType); Assert.Equal(frame.SessionId.ToString("D"), request.ResourceId);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        await approval.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approval.DispatchPendingAsync()); run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var generation = f.Ids.NewId(); run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), generation, f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var receipt = await Effects(f).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        var artifact = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(request.Operation.OperationId, artifact.ArtifactId); Assert.Equal(run.AgentRunId, artifact.AgentRunId);
        Assert.Equal("# Exact reviewed file\nUTF-8: thử nghiệm", Encoding.UTF8.GetString(await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(setup.Tenant, frame.SessionId, artifact.ArtifactId)));
        Assert.Equal(receipt, await Effects(f).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash));
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId);
        if (review) Assert.Null(grant); else Assert.Equal(OperationGrantStatus.Consumed, grant!.Status);
        Assert.False((await f.Authorizer.CheckAsync(setup.Agent.PrincipalId, "artifacts.read", TenantArtifactRules.Scope(setup.Tenant, artifact.ArtifactId))).Allowed);
        Assert.Equal(review, (await f.Authorizer.CheckAsync(setup.Agent.PrincipalId, "artifacts.create", TenantSessionRules.Scope(setup.Tenant, frame.SessionId))).Allowed);
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
    }
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "expiry")] [InlineData(true, "expiry")]
    [InlineData(false, "witness")] [InlineData(true, "witness")]
    public async Task Artifact_approval_rechecks_live_compound_authority_expiry_and_witness_at_effect_commit(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); var assignment = await GrantPublication(f, setup, ["sessions.read"]);
        var service = ApprovalService(f); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var request = (await service.PrepareArtifactAsync(new(setup.Tenant, frame.RunId), PublicationArguments, run.Revision, frame.Generation)).Request!;
        await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        await service.DispatchPendingAsync(); run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var generation = f.Ids.NewId(); run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), generation, clock.GetUtcNow().AddHours(2)), f.Ids.NewId());
        if (boundary == "read") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        if (boundary == "expiry") clock.Advance(TimeSpan.FromMinutes(61));
        if (boundary == "witness")
        {
            var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(setup.Agent.Instance.InstanceId, 1, Persona: setup.Agent.Instance.Persona with { Name = "Changed after review" }, ExpectedPersonaRevision: 1), clock.GetUtcNow(),
                new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", clock.GetUtcNow())));
        }
        var receipt = await Effects(f).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome);
        Assert.Equal(boundary == "read" ? "executor_read_required" : boundary == "expiry" ? "grant_expired" : "operation_witness_changed", receipt.ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
    }

    [Theory] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "expiry")] [InlineData(true, "expiry")]
    public async Task Artifact_terminal_approval_cannot_be_bypassed_by_the_direct_tool_after_policy_relaxation(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup);
        await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "artifacts.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 0);
        var service = ApprovalService(f); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var request = (await service.PrepareArtifactAsync(new(setup.Tenant, frame.RunId), PublicationArguments, run.Revision, frame.Generation)).Request!;
        if (boundary == "expiry") clock.Advance(TimeSpan.FromMinutes(61));
        else if (boundary == "cancel") await service.CancelAsync(setup.Tenant, request.RequestId, setup.Owner, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        else await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, false, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        await service.DispatchPendingAsync();
        await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "artifacts.create", new(OperationApprovalMode.WhenRequesterLacksPermission, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var generation = f.Ids.NewId(); run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), generation, clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var result = await PublicationService(f).ExecuteAsync(new(setup.Tenant, run.AgentRunId), generation, "try-direct-after-terminal", PublicationArguments);
        Assert.Equal(OperationDecisionKind.Denied, result.Decision); Assert.Null(result.Receipt);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_approval_requires_independent_Session_read_for_preparation_and_human_visibility_before_counting(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalService(f).PrepareArtifactAsync(new(setup.Tenant, frame.RunId), PublicationArguments, run.Revision, frame.Generation).AsTask());
        Assert.Equal("executor_read_required", denied.ReasonCode);
        await GrantPublication(f, setup, ["sessions.read"]);
        var request = (await ApprovalService(f).PrepareArtifactAsync(new(setup.Tenant, frame.RunId), PublicationArguments, run.Revision, frame.Generation)).Request!;
        var reader = await User(f, setup, "artifact-review-reader@example.test", ["approvals.read", "artifacts.create"]);
        Assert.Empty((await ApprovalService(f).ListAsync(setup.Tenant, reader, new(0, 1))).Items);
        Assert.Equal(0, (await ApprovalService(f).ListAsync(setup.Tenant, reader, new(0, 1))).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalService(f).ReadAsync(setup.Tenant, request.RequestId, reader).AsTask());
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read source Session", ["sessions.read"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, reader.PrincipalId, role.RoleId, TenantSessionRules.Scope(setup.Tenant, frame.SessionId), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Single((await ApprovalService(f).ListAsync(setup.Tenant, reader, new(0, 1))).Items);
        Assert.Equal(1, (await ApprovalService(f).ListAsync(setup.Tenant, reader, new(0, 1))).Total);
    }

    [Fact]
    public async Task Artifact_approval_SQLite_reopen_retains_exact_consent_and_schema_rejects_wrong_Session_and_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup, ["sessions.read"]);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var request = (await ApprovalService(f).PrepareArtifactAsync(new(setup.Tenant, frame.RunId), PublicationArguments, run.Revision, frame.Generation)).Request!;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(setup.Tenant, request.RequestId));
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var row = await db.Set<TenantApprovalRecord>().SingleAsync();
            var payload = System.Text.Json.Nodes.JsonNode.Parse(row.PayloadJson)!;
            payload["Operation"]!["ArtifactPublication"]!["SessionId"] = f.Ids.NewId().ToString("D"); row.PayloadJson = payload.ToJsonString();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(275, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = await f.Contexts!.CreateDbContextAsync())
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018140554_TenantArtifactOwnership"));
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(setup.Tenant, request.RequestId));
        await ApprovalService(f).DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        await ApprovalService(f).DispatchPendingAsync(); run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var generation = f.Ids.NewId(); run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), generation, f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        string connection; await using (var current = f.Contexts!.CreateDbContext()) connection = current.Database.GetDbConnection().ConnectionString;
        var faulty = new ArtifactFaultContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new ArtifactReceiptFault()).Options);
        var store = new SqliteIamStore(faulty, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Effects(f, store).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash).AsTask());
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Revision);
        Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        Assert.Equal(TenantOperationOutcome.Completed, (await Effects(f, store).ExecuteAsync(new(setup.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash)).Outcome);
        Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
    }

}
