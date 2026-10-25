using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static TenantCompletionReadService CompletionReads(Fixture f) => new(AttachmentOperations(f), (ITenantRunStore)f.Iam,
        (ITenantSessionStore)f.Iam, (ITenantArtifactStore)f.Artifacts, f.RunConfiguration(), f.Iam, f.Authorizer, f.Clock,
        (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam);
    private static TenantApprovalService CompletionReadApprovals(Fixture f) => new(f.Iam, (ITenantApprovalStore)f.Iam, (ITenantAgentStore)f.Iam,
        AttachmentOperations(f), f.Authorizer, f.Identity, f.Ids, f.Clock, f.RunConfiguration(), new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), SyntheticProviderAliases.Default,
        (ITenantApprovedOperationStore)f.Iam, completionReads: CompletionReads(f));
    private static TenantApprovedOperationService CompletionReadEffects(Fixture f) => new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam,
        (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam, AttachmentOperations(f), f.RunConfiguration(), f.Ids, f.Clock,
        new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), SyntheticProviderAliases.Default, completionReads: CompletionReads(f));
    private sealed record CompletionReadCase(BackgroundSetup Parent, Guid ChildSession, AgentRun Child, Guid Reader, TenantApprovalRequest Request);
    private static async Task<CompletionReadCase> CompletionReadPending(Fixture f, string permission = "agent-runs.read", bool metadata = false)
    {
        var parent = await BackgroundParent(f); var reader = await WaitReadAuthority(f, parent); var tenant = parent.Setup.Tenant;
        var child = await BackgroundChild(f, parent); var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run);
        if (metadata)
        {
            var role = await f.Access.SaveRoleAsync(parent.Setup.Owner.PrincipalId, tenant, null, "Completion metadata reader", ["artifacts.read"], (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(parent.Setup.Owner.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            await ((ITenantArtifactStore)f.Artifacts).CreateTenantAsync(active.Frame, f.Ids.NewId(), "exact private metadata.txt", "text/plain", "UNREAD-PRIVATE-FILE-BODY"u8.ToArray());
        }
        var outcome = BackgroundOutcome(f, active.Run, active.Snapshot);
        var final = await runtime.CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(tenant, parent.Setup.Owner,
            permission, new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 0);
        var arguments = JsonSerializer.Serialize(new { backgroundSessionId = child.Session.SessionId });
        var call = new ModelToolCall("read-exact-completion", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_background_completion", read = new { backgroundSessionId = child.Session.SessionId } }));
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 100000, 100);
        var current = await runtime.ApplyTenantRuntimeCommandAsync(parent.Parent.Owner, parent.Parent.AgentRunId,
            new AgentRunCommand.Checkpoint(parent.Parent.Revision, f.Clock.GetUtcNow(), parent.Frame.Generation, checkpoint, null), f.Ids.NewId());
        parent = parent with { Parent = current };
        var prepared = await CompletionReadApprovals(f).PrepareCompletionReadAsync(new(tenant, current.AgentRunId), arguments, current.Revision, parent.Frame.Generation);
        Assert.NotNull(prepared.Request); Assert.Equal(ApprovalKind.Review, prepared.Request.Kind);
        return new(parent, child.Session.SessionId, final, reader, prepared.Request);
    }
    private static async Task<(AgentRun Run, TenantApprovalOutcome? Outcome)> ResumeCompletionRead(Fixture f, CompletionReadCase v)
    {
        var approvals = CompletionReadApprovals(f); var tenant = v.Parent.Setup.Tenant;
        await approvals.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, true, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, v.Request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync());
        var source = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, v.Parent.Parent.AgentRunId))!;
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, source.Result!.ContinuationRunId!.Value))!;
        var active = await ClaimBackground(f, next);
        return await new TenantRunContinuationService((ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, CompletionReadEffects(f), f.Clock,
            (ITenantRuntimeStore)f.Iam, f.Ids).ResumeAsync(active.Run);
    }

    [Theory] [InlineData(false, "agent-runs.read")] [InlineData(true, "agent-runs.read")]
    [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "artifacts.read")] [InlineData(true, "artifacts.read")]
    public async Task Tenant_completion_read_compound_Review_freezes_metadata_and_reuses_only_exact_saved_source(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f, permission, metadata: true); var read = v.Request.Operation.CompletionRead!;
        Assert.Equal(v.Child.AgentRunId, read.SourceRunId); Assert.Equal(v.Child.Revision, read.SourceRevision); Assert.Single(TenantCompletionReadRules.Artifacts(read));
        Assert.DoesNotContain("UNREAD-PRIVATE-FILE-BODY", JsonSerializer.Serialize(v.Request));
        var result = await ResumeCompletionRead(f, v); Assert.Equal(TenantOperationOutcome.Completed, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages));
        var saved = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        using var json = JsonDocument.Parse(saved.Text); Assert.True(json.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(json.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(v.Child.AgentRunId, json.RootElement.GetProperty("completion").GetProperty("sourceAgentRunId").GetGuid());
        Assert.DoesNotContain("UNREAD-PRIVATE-FILE-BODY", saved.Text);
        var frame = new TenantRuntimeFrame(result.Run.Owner, result.Run.SessionId, result.Run.AgentRunId, result.Run.Claim!.Generation);
        var runtime = (ITenantRuntimeStore)f.Iam; var inspected = (await runtime.ReadTenantCompletionsAsync(frame, ToolCatalog.BackgroundInspect, v.ChildSession, null, false, 1)).Items[0];
        Assert.Equal(v.Request.RequestId, inspected.Source.CompletionReadRequestId); Assert.Single(inspected.Artifacts);
        var guard = new TenantWaitSourceAuthority(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Authorizer, (ITenantArtifactStore)f.Artifacts, CompletionReads(f));
        await guard.RequireStoredAsync(result.Run, default);
        var continuations = new TenantRunContinuationService((ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, CompletionReadEffects(f), f.Clock, runtime, f.Ids);
        var again = await continuations.ResumeAsync(result.Run); Assert.Equal(result.Run.Revision, again.Run.Revision); Assert.Equal(result.Run.Checkpoint!.PayloadJson, again.Run.Checkpoint!.PayloadJson);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(v.Request.TenantId, v.Request.RequestId));
        Assert.Single(await f.Iam.ReadAuditAsync(v.Request.TenantId, 200), a => a.Action == TenantCompletionReadRules.AuditAction);
    }

    [Theory] [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "independence")] [InlineData(true, "independence")]
    public async Task Tenant_completion_read_current_authority_can_fence_previously_delivered_reviewed_observation(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f); var result = await ResumeCompletionRead(f, v); var tenant = v.Request.TenantId;
        if (boundary == "read") await f.Access.RevokeAssignmentAsync(v.Parent.Setup.Owner.PrincipalId, tenant, v.Reader, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        else await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(tenant, v.Parent.Setup.Owner,
            "agent-runs.read", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        await Assert.ThrowsAsync<AccessDeniedException>(() => CompletionReads(f).RequireStoredSourceAsync(result.Run, v.ChildSession, v.Child.AgentRunId, v.Request.RequestId, default).AsTask());
        var frame = new TenantRuntimeFrame(result.Run.Owner, result.Run.SessionId, result.Run.AgentRunId, result.Run.Claim!.Generation);
        var page = await ((ITenantRuntimeStore)f.Iam).ReadTenantCompletionsAsync(frame, ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Empty(page.Items); Assert.Equal(0, page.PendingCount);
        var snapshot = (await ((ITenantRuntimeStore)f.Iam).LoadTenantRuntimeAsync(frame))!; var outcome = BackgroundOutcome(f, result.Run, snapshot);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantRuntimeStore)f.Iam).CommitTenantOutcomeAsync(frame, outcome.Snapshot, snapshot.Revision, outcome.Complete, null, f.Ids.NewId()).AsTask());
        Assert.Equal(CompletionInboxStatus.Pending, (await ((ITenantRuntimeStore)f.Iam).ReadTenantBackgroundCompletionAsync(result.Run.Owner, v.Child.AgentRunId))!.Item!.Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_read_visibility_filters_source_and_metadata_before_paging_and_refuses_missing_raw_reader(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f, metadata: true); var tenant = v.Request.TenantId; var member = await Member(f, tenant, v.Parent.Setup.Owner.PrincipalId);
        async Task Grant(string name, string[] keys, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(v.Parent.Setup.Owner.PrincipalId, tenant, null, name, keys, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(v.Parent.Setup.Owner.PrincipalId, tenant, member.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        }
        await Grant("Read request", ["approvals.read", "sessions.read"], AccessScope.ForTenant(tenant));
        var approvals = CompletionReadApprovals(f); Assert.Equal(0, (await approvals.ListAsync(tenant, member, new(PageSize: 1))).Total);
        await Grant("Exact terminal source", ["agent-runs.read"], TenantRunRules.Scope(tenant, v.Child.AgentRunId));
        Assert.Equal(0, (await approvals.ListAsync(tenant, member, new(PageSize: 1))).Total);
        await Grant("Exact file metadata", ["artifacts.read"], TenantArtifactRules.Scope(tenant, Assert.Single(TenantCompletionReadRules.Artifacts(v.Request.Operation.CompletionRead!)).ArtifactId));
        Assert.Equal(1, (await approvals.ListAsync(tenant, member, new(PageSize: 1))).Total);
        Assert.False((await approvals.ReadViewAsync(tenant, v.Request.RequestId, member)).Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(v.Parent.Setup.Owner.PrincipalId, tenant, v.Reader, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var revision = (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var refusal = await Record.ExceptionAsync(() => approvals.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, true,
            v.Request.Operation.ActionHash, revision, 1).AsTask());
        Assert.IsType<AccessDeniedException>(refusal);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_completion_read_rejection_and_cancel_retain_pending_accounting_without_data_or_new_same_root_request(bool sqlite, bool cancel)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f); var approvals = CompletionReadApprovals(f); var tenant = v.Request.TenantId;
        if (cancel) await approvals.CancelAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        else await approvals.DecideAsync(tenant, v.Request.RequestId, v.Parent.Setup.Owner, false, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approvals.DispatchPendingAsync());
        var runs = (ITenantRunStore)f.Iam; var source = (await runs.FindTenantRunAsync(tenant, v.Parent.Parent.AgentRunId))!;
        var active = await ClaimBackground(f, (await runs.FindTenantRunAsync(tenant, source.Result!.ContinuationRunId!.Value))!);
        var result = await new TenantRunContinuationService(runs, (ITenantApprovalStore)f.Iam, CompletionReadEffects(f), f.Clock, (ITenantRuntimeStore)f.Iam, f.Ids).ResumeAsync(active.Run);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var messages)); var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var json = JsonDocument.Parse(text); Assert.False(json.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("completion").ValueKind);
        var duplicate = await approvals.PrepareCompletionReadAsync(new(tenant, result.Run.AgentRunId), JsonSerializer.Serialize(new { backgroundSessionId = v.ChildSession }), result.Run.Revision, result.Run.Claim!.Generation);
        Assert.Equal(cancel ? "previously_cancelled" : "previously_rejected", duplicate.ReasonCode);
        var page = await ((ITenantRuntimeStore)f.Iam).ReadTenantCompletionsAsync(new(result.Run.Owner, result.Run.SessionId, result.Run.AgentRunId, result.Run.Claim!.Generation), ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Empty(page.Items); Assert.Equal(CompletionInboxStatus.Pending, (await ((ITenantRuntimeStore)f.Iam).ReadTenantBackgroundCompletionAsync(result.Run.Owner, v.Child.AgentRunId))!.Item!.Status);
    }

    [Fact]
    public async Task Tenant_completion_read_empty_SQLite_upgrade_restores_current_guards_and_model_parity()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using var db = f.Contexts!.CreateDbContext();
        const string query = "SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Tenant%' ORDER BY name";
        var before = await db.Database.SqlQueryRaw<string>(query).ToArrayAsync();
        Assert.Contains("TR_TenantApprovals_CompletionRead_Insert", before); Assert.Contains("TR_TenantApprovals_CompletionRead_Update", before);
        Assert.Contains("TR_TenantApprovals_IndependentTask_Insert", before); Assert.Contains("TR_TenantApprovals_IndependentTask_Update", before);
        await db.GetService<IMigrator>().MigrateAsync("20261020120000_TenantBackgroundReportReview");
        Assert.Equal(9, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Tenant%'").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(query).ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Fact]
    public async Task Tenant_completion_read_SQLite_retains_eleven_guards_and_refuses_changed_evidence_or_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f, metadata: true); await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        var guards = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Tenant%'").ToArrayAsync();
        foreach (var name in new[] { "TR_TenantApprovals_CompletionRead_Insert", "TR_TenantApprovals_CompletionRead_Update", "TR_TenantApprovals_BackgroundReport_Insert", "TR_TenantApprovals_BackgroundReport_Update",
            "TR_TenantApprovals_BackgroundCreation_Update", "TR_TenantApprovals_BrowserArtifacts_Insert", "TR_TenantApprovals_BrowserArtifacts_Update", "TR_TenantReceipts_BrowserArtifacts_Insert",
            "TR_TenantReceipts_BrowserArtifacts_Update", "TR_TenantApprovals_BrowserFiles_Insert", "TR_TenantApprovals_BrowserFiles_Update" }) Assert.Contains(name, guards);
        var id = v.Request.RequestId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson, '$.Operation.CompletionRead.SourceRevision', 999) WHERE RequestId={id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020120000_TenantBackgroundReportReview"));
        Assert.Equal(v.Request.Operation, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(v.Request.TenantId, id))!.Operation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_read_saved_proof_cannot_authorize_another_human_root_or_an_invented_reference(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f); var result = await ResumeCompletionRead(f, v); var tenant = v.Request.TenantId;
        await Assert.ThrowsAsync<AccessDeniedException>(() => CompletionReads(f).RequireStoredSourceAsync(result.Run, v.ChildSession, v.Child.AgentRunId, f.Ids.NewId(), default).AsTask());
        var runtime = (ITenantRuntimeStore)f.Iam;
        await runtime.ApplyTenantRuntimeCommandAsync(result.Run.Owner, result.Run.AgentRunId, new AgentRunCommand.Complete(result.Run.Revision,
            f.Clock.GetUtcNow(), result.Run.Claim!.Generation, "No user-facing answer", AgentRunOutcomeKind.NoAction, null), f.Ids.NewId());
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, result.Run.SessionId))!;
        var nextInput = await f.RunService().SubmitTextAsync(tenant, result.Run.SessionId, v.Parent.Setup.Owner, f.Ids.NewId(), "A separate human task",
            (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, session.Snapshot.Revision);
        var active = await ClaimBackground(f, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, nextInput.RunId))!);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(result.Run.Checkpoint, out var oldMessages));
        var copied = await runtime.ApplyTenantRuntimeCommandAsync(active.Run.Owner, active.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(active.Run.Revision, f.Clock.GetUtcNow(), active.Frame.Generation,
                new(AgentRunToolCallCheckpoint.Write(oldMessages!), 1, 0, 100000, 100), null), f.Ids.NewId());
        Assert.NotEqual(result.Run.Admission.ExecutionContext!.RootActivationId, copied.Admission.ExecutionContext!.RootActivationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => CompletionReads(f).RequireStoredSourceAsync(copied, v.ChildSession, v.Child.AgentRunId, v.Request.RequestId, default).AsTask());
        var page = await runtime.ReadTenantCompletionsAsync(active.Frame, ToolCatalog.BackgroundList, null, null, true, 1);
        Assert.Empty(page.Items);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_completion_read_human_can_inspect_and_cancel_saved_request_when_executor_or_configuration_changes(bool sqlite, bool configuration)
    {
        await using var f = await Fixture.Create(sqlite, new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T05:00:00Z")), builtInAuthority: true, artifactAuthority: true);
        var v = await CompletionReadPending(f); var tenant = v.Request.TenantId; var owner = v.Parent.Setup.Owner;
        if (configuration)
        {
            var state = (await f.Iam.ReadTenantAsync(tenant))!; var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, v.Parent.Parent.AgentInstanceId))!;
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                new(agent.Instance.InstanceId, 1, Persona: agent.Instance.Persona with { Name = "Changed read persona" }, ExpectedPersonaRevision: 1), f.Clock.GetUtcNow(),
                new(f.Ids.NewId(), tenant, owner.PrincipalId, "agents.update", agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", f.Clock.GetUtcNow())));
        }
        else await f.Access.RevokeAssignmentAsync(owner.PrincipalId, tenant, v.Reader, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var approvals = CompletionReadApprovals(f); var view = await approvals.ReadViewAsync(tenant, v.Request.RequestId, owner);
        Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        Assert.Contains(view.Review.Changes, c => c.Field == "completionObservation" && Equals(c.After, v.Request.Operation.CompletionRead!.ObservationJson));
        var cancelled = await approvals.CancelAsync(tenant, v.Request.RequestId, owner, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(TenantApprovalStatus.Cancelled, cancelled.Status);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, v.Request.Operation.OperationId));
    }

}
