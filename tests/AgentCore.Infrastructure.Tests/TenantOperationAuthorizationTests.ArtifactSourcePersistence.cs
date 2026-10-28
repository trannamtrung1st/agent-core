using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")] [InlineData(false, "expiry")] [InlineData(true, "expiry")]
    public async Task Tenant_Artifact_source_consent_terminal_refusal_survives_changed_source_and_relaxed_policy(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f); var s = v.Setup;
        if (boundary == "expiry") clock.Advance(TimeSpan.FromMinutes(61));
        else if (boundary == "cancel") await v.Approvals.CancelAsync(s.Tenant, v.Request.RequestId, s.Owner, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        else await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, false, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await v.Approvals.DispatchPendingAsync();
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.WhenRequesterLacksPermission, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 255, 128, 4]);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var direct = await SourcePublicationService(f).ExecuteWorkspaceAsync(new(s.Tenant, run.AgentRunId), run.Claim!.Generation, "refreshed-source", v.Arguments);
        Assert.Equal(OperationDecisionKind.Denied, direct.Decision); Assert.Null(direct.Receipt);
        var prepared = await v.Approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, run.AgentRunId), v.Arguments, run.Revision, run.Claim.Generation);
        Assert.Equal(OperationDecisionKind.Denied, prepared.Decision); Assert.Equal(v.Request.RequestId, prepared.Request!.RequestId);
        Assert.Equal(boundary == "reject" ? "previously_rejected" : boundary == "cancel" ? "previously_cancelled" : "previously_expired", prepared.ReasonCode);
        Assert.Single((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 20))).Items);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
    }

    [Fact]
    public async Task Tenant_Artifact_source_consent_SQLite_upgrade_preserves_inline_and_rejects_forged_source_shapes_and_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "artifacts.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var prior = (await ApprovalService(f).PrepareArtifactAsync(new(s.Tenant, run.AgentRunId), PublicationArguments, run.Revision, frame.Generation)).Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        { await db.GetService<IMigrator>().MigrateAsync("20261019041906_TenantWorkspaceOfficeMutation"); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); }
        Assert.Equal(prior, await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, prior.RequestId));
        var sourceAgent = await f.AgentsAtRelease(35).CreateAsync(s.Owner.PrincipalId, s.Tenant, "general-assistant", 35, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var session = await f.Sessions.CreateAsync(s.Tenant, sourceAgent.Instance.InstanceId, s.Owner, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, sourceAgent.Instance.Revision);
        var submission = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), "Publish exact source", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        s = s with { Agent = sourceAgent, Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submission.RunId))! }; frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, ["sessions.read", "agents.read", "workspaces.read", "artifacts.create"]); await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 3]);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f));
        var request = (await approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), """{"path":"/working/source.bin","displayName":"published.bin"}""", run.Revision, frame.Generation)).Request!;
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, request.RequestId));
        foreach (var change in new[] { "session", "path", "hash", "size", "mixed", "content", "nullType", "resource", "scalar" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<TenantApprovalRecord>().SingleAsync(r => r.RequestId == request.RequestId); var json = JsonNode.Parse(row.PayloadJson)!;
            var artifact = json["Operation"]!["ArtifactPublication"]!;
            if (change == "session") artifact["SessionId"] = prior.SessionId.ToString("D");
            if (change == "path") artifact["Source"]!["Path"] = "/home/source.bin";
            if (change == "hash") artifact["Source"]!["ObservationHash"] = new string('z', 64);
            if (change == "size") artifact["Source"]!["ByteSize"] = 52428801;
            if (change == "mixed") json["Operation"]!["WorkspaceRead"] = new JsonObject { ["SessionId"] = frame.SessionId.ToString("D") };
            if (change == "content") artifact["ArgumentsJson"] = """{"path":"/working/source.bin","displayName":"published.bin","content":"caller bytes"}""";
            if (change == "nullType") artifact["ArgumentsJson"] = """{"path":"/working/source.bin","displayName":"published.bin","contentType":null}""";
            if (change == "resource") row.ResourceId = prior.SessionId.ToString("D");
            if (change == "scalar") artifact["Source"] = "not a typed source";
            row.PayloadJson = json.ToJsonString();
            Assert.Equal(275, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261019041906_TenantWorkspaceOfficeMutation"));
        await using (var restored = f.Contexts!.CreateDbContext()) await restored.Database.MigrateAsync();
        Assert.Equal(request, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, request.RequestId));
        Assert.Equal(prior, await ((ITenantApprovalStore)reopened).FindApprovalAsync(s.Tenant, prior.RequestId));
    }

    [Fact]
    public async Task Tenant_Artifact_source_consent_SQLite_receipt_fault_rolls_back_output_Run_grant_security_and_audit_then_retries_exactly()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, grant: true); var s = v.Setup; var run = await ResumeSourceConsent(f, v);
        string connection; await using (var db = f.Contexts!.CreateDbContext()) connection = db.Database.GetDbConnection().ConnectionString;
        var faulty = new ArtifactFaultContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new ArtifactReceiptFault()).Options);
        var store = new SqliteIamStore(faulty, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        var effects = Effects(f, store, artifactSources: ArtifactSources(f)); var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<InvalidOperationException>(() => effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim!.Generation, v.Request.Operation.ActionHash).AsTask());
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Revision);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
        Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        var result = await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim!.Generation, v.Request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome); Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        var output = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
        Assert.Equal(v.Bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, v.Frame.SessionId, output.ArtifactId));
    }
}
