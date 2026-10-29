using System.Text;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_publication_commits_one_owned_file_with_its_actual_Run_receipt_and_no_configuration_mutation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); var scope = TenantSessionRules.Scope(setup.Tenant, frame.SessionId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Publish in this Session", ["sessions.read", "artifacts.create"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var service = new TenantArtifactOperationService((ITenantArtifactOperationStore)f.Iam, Operations(f), f.RunConfiguration(), f.Ids, f.Clock, f.Authorizer, (ITenantApprovalStore)f.Iam);
        const string args = """{"displayName":"private-report.md","content":"# Private report\nExact UTF-8 content: thử nghiệm"}""";
        var result = await service.ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "publish-1", args);
        Assert.Equal(OperationDecisionKind.Allowed, result.Decision); Assert.NotNull(result.Receipt); Assert.Null(result.Receipt.RequestId);
        var artifact = await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(setup.Tenant, frame.SessionId, result.Receipt.OperationId);
        Assert.NotNull(artifact); Assert.Equal(frame.RunId, artifact.AgentRunId);
        Assert.Equal("# Private report\nExact UTF-8 content: thử nghiệm", Encoding.UTF8.GetString(await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(setup.Tenant, frame.SessionId, artifact.ArtifactId)));
        Assert.Equal(result, await service.ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "publish-1", args));
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "publish-1", args.Replace("Private report", "Changed report")).AsTask());
        Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(setup.Agent.Instance.Revision, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Revision);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        Assert.Equal("operation:" + result.Receipt.OperationId.ToString("D"), run.SideEffect.ToolCallId);
        Assert.Equal(artifact, await ArtifactService(f, (ITenantArtifactStore)f.Artifacts).ReadAsync(setup.Tenant, frame.SessionId, artifact.ArtifactId, setup.Owner));
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        Assert.DoesNotContain("Exact UTF-8 content", System.Text.Json.JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(setup.Tenant, 200)));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
            var replay = await PublicationService(f, (ITenantArtifactOperationStore)reopened).ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "publish-1", args);
            Assert.Equal(result, replay); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        }
    }

    private const string PublicationArguments = """{"displayName":"report.md","content":"# Private report"}""";
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_publication_keeps_requester_review_separate_from_the_Agents_scoped_creation_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var requester = await User(f, setup, "requester-publish@example.test", ["agents.read", "agents.chat", "sessions.create", "sessions.read"]);
        await GrantExactSpaceRead(f, setup, requester.PrincipalId, SpaceId(setup));
        var session = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, requester, AgentCore.Domain.Conversation.SessionMode.Text,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, setup.Agent.Instance.Revision);
        var admission = await f.RunService().SubmitTextAsync(setup.Tenant, session.Owner.SessionId, requester, f.Ids.NewId(), "Publish this report",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, admission.RunId))!;
        setup = setup with { Run = run }; var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup);
        var service = PublicationService(f); var result = await service.ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "requester-review", PublicationArguments);
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); Assert.Equal("review_required", result.ReasonCode); Assert.Null(result.Receipt);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Requester create in this Session", ["artifacts.create"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, requester.PrincipalId, role.RoleId, TenantSessionRules.Scope(setup.Tenant, frame.SessionId), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        result = await service.ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "requester-review", PublicationArguments);
        Assert.Equal(OperationDecisionKind.Allowed, result.Decision); Assert.NotNull(result.Receipt);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items);
    }
    [Theory] [InlineData(false, "role")] [InlineData(true, "role")]
    [InlineData(false, "policy")] [InlineData(true, "policy")]
    [InlineData(false, "lease")] [InlineData(true, "lease")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "run")] [InlineData(true, "run")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    public async Task Artifact_publication_rechecks_current_commit_authority_claim_and_cancellation_and_has_one_race_winner(bool sqlite, string change)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); var assignment = await GrantPublication(f, setup);
        var gate = new ArtifactCommitGate((ITenantArtifactOperationStore)f.Iam); using var cancellation = new CancellationTokenSource();
        var reference = new TenantRunReference(setup.Tenant, frame.RunId);
        var pending = PublicationService(f, gate).ExecuteAsync(reference, frame.Generation, "race", PublicationArguments, cancellation.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); TenantOperationReceipt? winner = null;
        try
        {
            if (change == "role") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            if (change == "policy") await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "artifacts.create", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 0);
            if (change == "lease") clock.Advance(TimeSpan.FromMinutes(6));
            if (change == "cancel") cancellation.Cancel();
            if (change == "run")
            {
                var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
                await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId, new AgentRunCommand.RequestCancellation(active.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            }
            if (change == "compete") winner = (await PublicationService(f).ExecuteAsync(reference, frame.Generation, "race", PublicationArguments)).Receipt;
        }
        finally { gate.Proceed.TrySetResult(); }
        if (winner is not null)
        {
            Assert.Equal(winner, (await pending).Receipt); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
            Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        }
        else
        {
            var failure = await Record.ExceptionAsync(async () => await pending);
            Assert.True(failure is AccessDeniedException or SecurityConflictException or OperationCanceledException, failure?.ToString());
            Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
            Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(setup.Tenant, frame.RunId, "race"));
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Artifact_publication_refuses_missing_independent_Session_read_even_at_the_store_boundary(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup, ["artifacts.create"]);
        var args = TenantArtifactToolContract.Parse(PublicationArguments); var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        var commit = new TenantArtifactCommit(f.Ids.NewId(), frame.RunId, frame.Generation, run.Revision, "bypass", args.CanonicalArguments, args.ParametersHash,
            setup.Agent.Instance.Revision, TenantArtifactToolContract.ActionHash(frame.SessionId, args.ParametersHash));
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantArtifactOperationStore)f.Iam).TryCommitArtifactAsync(
            state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), setup.Tenant, setup.Agent.PrincipalId, TenantArtifactOperationRules.AuditAction, frame.SessionId.ToString("D"), "allowed", "operation_completed", f.Clock.GetUtcNow())).AsTask());
        Assert.Equal("executor_read_required", denied.ReasonCode);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Fact]
    public async Task Artifact_publication_SQLite_rolls_back_metadata_Run_receipt_security_and_audit_if_receipt_save_fails()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true, artifactAuthority: true); var setup = await Setup(f, "customer-support", 3);
        var frame = await ClaimArtifactRun(f, setup); await GrantPublication(f, setup);
        string connection; await using (var current = f.Contexts!.CreateDbContext()) connection = current.Database.GetDbConnection().ConnectionString;
        var faulty = new ArtifactFaultContexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).AddInterceptors(new ArtifactReceiptFault()).Options);
        var store = new SqliteIamStore(faulty, builtIns: f.BuiltIns, clock: f.Clock, artifactBacking: (SqliteArtifactStore)f.Artifacts);
        var securityRevision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PublicationService(f, store).ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "save-fault", PublicationArguments).AsTask());
        Assert.Equal(securityRevision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, frame.RunId))!.Revision);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Null(await ((ITenantArtifactOperationStore)f.Iam).FindDirectOperationReceiptAsync(setup.Tenant, frame.RunId, "save-fault"));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == TenantArtifactOperationRules.AuditAction);
        var retry = await PublicationService(f, store).ExecuteAsync(new(setup.Tenant, frame.RunId), frame.Generation, "save-fault", PublicationArguments);
        Assert.NotNull(retry.Receipt); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(setup.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    private static TenantArtifactOperationService PublicationService(Fixture f, ITenantArtifactOperationStore? effects = null) => new(effects ?? (ITenantArtifactOperationStore)f.Iam,
        Operations(f), f.RunConfiguration(), f.Ids, f.Clock, f.Authorizer, (ITenantApprovalStore)f.Iam);
    private static async Task<RoleAssignment> GrantPublication(Fixture f, Environment setup, string[]? permissions = null)
    {
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Publish an Artifact", permissions ?? ["sessions.read", "artifacts.create"], (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, role.RoleId,
            TenantSessionRules.Scope(setup.Tenant, setup.Run.SessionId), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
    }
    private sealed class ArtifactCommitGate(ITenantArtifactOperationStore inner) : ITenantArtifactOperationStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenant, Guid run, string call, CancellationToken ct = default) => inner.FindDirectOperationReceiptAsync(tenant, run, call, ct);
        public async ValueTask<TenantOperationReceipt?> TryCommitArtifactAsync(TenantSecurityState next, long expected, TenantArtifactCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TryCommitArtifactAsync(next, expected, commit, audit, ct); }
    }
    private sealed class ArtifactFaultContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
    private sealed class ArtifactReceiptFault : SaveChangesInterceptor
    {
        private int _remaining = 1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<TenantOperationReceiptRecord>().Any(e => e.State == EntityState.Added) && Interlocked.Exchange(ref _remaining, 0) == 1)
                throw new InvalidOperationException("Injected receipt save failure");
            return ValueTask.FromResult(result);
        }
    }
}
