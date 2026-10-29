using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json.Nodes;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantApprovalService Approvals, TenantApprovedOperationService Effects, TenantSandboxRuntimeReadService Reads,
        TenantRunContinuationService Continuation) SandboxReadReviewServices(Fixture f, RuntimeSandboxSetup s, IIamStore? current = null,
        ITenantSandboxRuntimeStore? storage = null, ISandboxImageAuthority? images = null)
    {
        current ??= f.Iam; var authorizer = new IamAuthorizer(current); var p = SetupProfiles(f);
        var operations = new TenantOperationAuthorizer(current, (ITenantRunStore)current, (ITenantAgentStore)current, authorizer,
            new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var reads = new TenantSandboxRuntimeReadService(current, (ITenantSessionStore)current, storage ?? (ITenantSandboxRuntimeStore)current, f.Identity, authorizer,
            operations, s.Resolver, (ITenantSandboxProfileStore)current, images ?? p.Images, p.Ready, TimeProvider.System,
            (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current);
        var approvals = new TenantApprovalService(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, authorizer, f.Identity, f.Ids, TimeProvider.System,
            s.Resolver, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(current), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, sandboxReads: reads);
        var effects = new TenantApprovedOperationService(current, (ITenantRunStore)current, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current,
            operations, s.Resolver, f.Ids, TimeProvider.System, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(current), SyntheticProviderAliases.Default, sandboxReads: reads);
        return (approvals, effects, reads, new((ITenantRunStore)current, (ITenantApprovalStore)current, effects, TimeProvider.System, (ITenantRuntimeStore)current, f.Ids));
    }
    private static async Task<RuntimeSandboxSetup> CheckpointSandboxRead(Fixture f, RuntimeSandboxSetup s, SandboxReadKind kind, Guid? execution = null, bool large = false)
    {
        var call = new ModelToolCall("exact-read", ToolCatalog.RequestAuthorization, kind == SandboxReadKind.Process
            ? JsonSerializer.Serialize(new { operation = TenantSandboxReadAuthorizationContract.Operation(kind), executionId = execution })
            : JsonSerializer.Serialize(new { operation = TenantSandboxReadAuthorizationContract.Operation(kind) }));
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, DateTimeOffset.UtcNow, s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write(large ? [new(ModelRole.User, new string('x', 205000)), new(ModelRole.Assistant, "", ToolCalls: [call])]
                    : [new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 180000, 0), null), f.Ids.NewId());
        return s with { Run = run };
    }
    private static async Task GrantReadReviewScopes(Fixture f, RuntimeSandboxSetup s)
    {
        await GrantSandboxRead(f, s, "sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await GrantSandboxRead(f, s, "sandbox.read", AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_read_Review_consumes_exact_status_once_and_delivers_stored_observation_without_SDK_work(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Status); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Status, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("sandbox.read", request.Permission); Assert.NotNull(request.Snapshot!.SandboxRead);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Kind=1 WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET SandboxProfileId={s.Lease.Profile.Id}, SandboxProfileRevision=1 WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.SandboxRead.Kind',3) WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.SandboxRead.ProfileReferencesJson','{{}}') WHERE RequestId={request.RequestId}"));
        }
        Assert.Equal(s.Lease.Profile.Id, Assert.Single(TenantApprovalRules.ReadProfiles(request.Operation.SandboxRead!)).Id);
        var correlated = TenantApprovalRules.ForRun(TenantApprovalRules.PrepareSandboxRead(f.Ids.NewId(), request.Operation.InstanceId, request.Operation.SandboxRead!, 2, 2), s.Run);
        Assert.Equal(request.Fingerprint, correlated.OperationFingerprint);
        Assert.Equal(request.RequestId, (await ((ITenantApprovalStore)f.Iam).FindRootApprovalAsync(s.Tenant, request.RootActivationId, request.ExecutorPrincipalId, correlated))!.RequestId);
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.True(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "readDisclosure");
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var before = (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        if (sqlite) services = SandboxReadReviewServices(f, s, new SqliteIamStore(f.Contexts!, f.Content));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var resumed = await services.Continuation.ResumeAsync(run);
        Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome); Assert.Equal(TenantSandboxReadOperationRules.Reason, resumed.Outcome.ReasonCode);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages));
        var output = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-read").Text;
        using var json = JsonDocument.Parse(output); Assert.True(json.RootElement.GetProperty("readAuthorized").GetBoolean()); Assert.False(json.RootElement.GetProperty("applied").GetBoolean());
        Assert.False(json.RootElement.GetProperty("inspectionUnavailable").GetBoolean()); var observation = json.RootElement.GetProperty("inspection");
        Assert.Equal("Ready", observation.GetProperty("environment").GetProperty("storedState").GetString()); Assert.True(observation.GetProperty("observationOnly").GetBoolean());
        Assert.False(observation.GetProperty("liveAuthorityConfirmed").GetBoolean());
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(receipt, await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash));
        var decided = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(s.Tenant, request.RequestId))!;
        await Assert.ThrowsAsync<SecurityConflictException>(() => services.Reads.InspectApprovedForRunAsync(frame, decided, receipt, ToolLimits.MaxOutputBytes).AsTask());
        var direct = await Assert.ThrowsAsync<AccessDeniedException>(() => services.Reads.InspectForRunAsync(frame, new("ordinary-read", ToolCatalog.SandboxStatus, "{}"), ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal("sandbox_read_review_required", direct.ReasonCode);
        var after = (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(before.Lease, after.Lease); Assert.Equal(before.Execution, after.Execution); Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId));
    }
    [Theory] [InlineData(false, "progress")] [InlineData(true, "progress")] [InlineData(false, "large")] [InlineData(true, "large")]
    [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "stale")] [InlineData(true, "stale")] [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "deliveryRevoked")] [InlineData(true, "deliveryRevoked")] [InlineData(false, "mismatch")] [InlineData(true, "mismatch")]
    public async Task Tenant_Sandbox_read_Review_process_binds_output_target_and_current_delivery_authority(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true, alternate: mode == "stale"); await ReadySandbox(f, s);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var execution = RuntimeExecution(f, s) with { CommandText = "OWNER_ONLY_COMMAND", Background = true };
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        execution = execution with { Revision = 2, State = SandboxExecutionState.Running, Phase = "execution", ProviderCommandId = "PRIVATE_PROVIDER_COMMAND",
            Stdout = mode == "large" ? new string('界', 10000) : "PAYLOAD_ONLY_7fa198", Stderr = mode == "large" ? new string('界', 10000) : "stored process error" };
        Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 1));
        await GrantReadReviewScopes(f, s);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Process, execution.Id, mode == "large"); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Process, execution.Id, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.DoesNotContain("OWNER_ONLY", JsonSerializer.Serialize(request)); Assert.DoesNotContain("PAYLOAD_ONLY_7fa198", JsonSerializer.Serialize(request));
        if (mode == "progress")
        {
            execution = execution with { Revision = 3, Stdout = execution.Stdout + " after factual progress" };
            Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 2));
        }
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.True(view.Review!.CanDecide);
        if (mode == "cancel") await services.Approvals.CancelAsync(s.Tenant, request.RequestId, SandboxHuman(s), request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        else await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), mode != "reject", request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        if (mode == "stale")
        {
            execution = execution with { Revision = 3, State = SandboxExecutionState.Completed, TerminationConfirmed = true, ExitCode = 0, FinishedAt = DateTimeOffset.UtcNow };
            Assert.True(await store.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 2));
            var active = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
            Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, active with { Revision = active.Revision + 1, State = SandboxLeaseState.Ready, ActiveExecutionId = null }, active.Revision));
            var saved = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!; var p = SetupProfiles(f);
            await new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host)
                .RequestProfileSwitchAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision,
                    execution.Id, s.Alternate!.Id, 1, 1), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        if (mode == "deliveryRevoked") await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
        if (mode is "revoked" or "deliveryRevoked")
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            var scope = mode == "revoked" ? AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")) : TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id);
            var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == scope
                && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("sandbox.read")));
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        }
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        if (mode == "mismatch")
        {
            var wrong = new ModelToolCall("exact-read", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_sandbox_process", executionId = f.Ids.NewId() }));
            run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, frame.Generation, new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [wrong])]), run.Checkpoint!.StepCount, run.Checkpoint.OutputBytes,
                    run.Checkpoint.RemainingOverallBudgetMs, run.Checkpoint.ActiveExecutionMs, run.Checkpoint.BudgetRecordedAtUtc), null), f.Ids.NewId());
            var mismatch = await Assert.ThrowsAsync<SecurityConflictException>(() => services.Continuation.ResumeAsync(run).AsTask()); Assert.Equal("approval_continuation_mismatch", mismatch.ReasonCode);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId)); return;
        }
        var resumed = await services.Continuation.ResumeAsync(run); Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
        var text = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-read").Text; using var observed = JsonDocument.Parse(text);
        Assert.DoesNotContain("OWNER_ONLY", text); Assert.DoesNotContain("PRIVATE_PROVIDER_COMMAND", text);
        var unavailable = mode is "reject" or "cancel" or "stale" or "revoked" or "deliveryRevoked";
        Assert.Equal(unavailable, observed.RootElement.GetProperty("inspectionUnavailable").GetBoolean());
        Assert.Equal(mode is "progress" or "large" or "deliveryRevoked", observed.RootElement.GetProperty("readAuthorized").GetBoolean());
        if (!unavailable)
        {
            var result = observed.RootElement.GetProperty("inspection").GetProperty("execution");
            if (mode == "large") { Assert.True(result.GetProperty("truncated").GetBoolean()); Assert.NotEmpty(result.GetProperty("stdout").GetString()!); Assert.DoesNotContain("�", text); }
            else Assert.Equal(execution.Stdout, result.GetProperty("stdout").GetString());
        }
        else Assert.DoesNotContain("PAYLOAD_ONLY_7fa198", text);
        var after = (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        Assert.Equal(execution, after.Execution); Assert.Null(after.Execution!.ProcessStop); Assert.Null(await f.Sandbox.GetExecutionAsync(s.Session.SessionId, execution.Id));
        Assert.All(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => { var audit = JsonSerializer.Serialize(a); Assert.DoesNotContain("PAYLOAD_ONLY_7fa198", audit); Assert.DoesNotContain("OWNER_ONLY", audit); });
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_read_Review_discovery_cannot_expand_approved_profiles_and_rechecks_post_IO_revocation(bool sqlite, bool race)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true, inspection: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var denied = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate!.Id));
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, denied.AssignmentId, state.Tenant.Revision);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Profiles); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Profiles, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.Equal(s.Lease.Profile.Id, Assert.Single(TenantApprovalRules.ReadProfiles(request.Operation.SandboxRead!)).Id);
        await GrantSandboxRead(f, s, "sandbox.read", TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate!.Id));
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.True(view.Review!.CanDecide);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        if (race)
        {
            var p = SetupProfiles(f);
            services = SandboxReadReviewServices(f, s, images: new InspectionImageHook(p.Images, async () => {
                var current = (await f.Iam.ReadTenantAsync(s.Tenant))!;
                var assignment = Assert.Single(current.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
                await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, current.Tenant.Revision);
            }));
        }
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!; var resumed = await services.Continuation.ResumeAsync(run);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
        var text = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-read").Text; using var observed = JsonDocument.Parse(text);
        Assert.DoesNotContain(s.Alternate!.Id, text); Assert.DoesNotContain(s.Alternate.Name, text);
        Assert.Equal(race, observed.RootElement.GetProperty("inspectionUnavailable").GetBoolean());
        if (!race)
        {
            var result = observed.RootElement.GetProperty("inspection"); Assert.Equal(s.Lease.Profile.Id, Assert.Single(result.GetProperty("profiles").EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        }
        else Assert.Equal("stale_revision", observed.RootElement.GetProperty("inspectionUnavailableReason").GetString());
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_read_Review_delivery_fences_authority_changed_by_the_last_target_snapshot_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Status); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Status, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        var receipt = await services.Effects.ExecuteAsync(new(s.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash);
        var reads = 0;
        var hook = new RuntimeSandboxReadHook((ITenantSandboxRuntimeStore)f.Iam, async () => {
            if (++reads != 2) return;
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
            var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
        });
        services = SandboxReadReviewServices(f, s, storage: hook);
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!);
        Assert.Equal(2, reads); Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
        using var result = JsonDocument.Parse(Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-read").Text);
        Assert.True(result.RootElement.GetProperty("inspectionUnavailable").GetBoolean()); Assert.Equal("stale_revision", result.RootElement.GetProperty("inspectionUnavailableReason").GetString());
        Assert.Equal(receipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_read_Review_scoped_inbox_and_reviewer_cover_every_exact_profile_without_tenant_wide_approval_authority(bool sqlite, bool revoke)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true, alternate: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("read-reviewer@example.test", "Scoped reviewer", "strong-reviewer-password");
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, state.Roles.Single(r => r.Key == "approver").RoleId, state.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        var accepted = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        foreach (var assignment in accepted.Assignments.Where(a => a.PrincipalId == user.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var login = await SetupHuman(f, account.AccountId, user.PrincipalId, account.LoginKey, "strong-reviewer-password");
        var reviewer = new TenantSessionInitiator(account.AccountId, login.AuthenticationSessionId, user.PrincipalId);
        async Task<Guid> Grant(string name, string[] permissions, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, user.PrincipalId, role.RoleId, scope, await Revision())).AssignmentId;
        }
        await Grant("Read exact review inbox", ["approvals.read"], AccessScope.ForTenant(s.Tenant));
        await Grant("Review exact agent reads", ["sandbox.read", "approvals.approve"], AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D")));
        await Grant("Read exact Session", ["sessions.read"], TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        await Grant("Read source A", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), await Revision(), 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Profiles); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Profiles, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.Equal(2, TenantApprovalRules.ReadProfiles(request.Operation.SandboxRead!).Count);
        var hidden = await services.Approvals.ListAsync(s.Tenant, reviewer, new()); Assert.Equal(0, hidden.Total); Assert.Empty(hidden.Items);
        Assert.Equal("approval_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer).AsTask())).ReasonCode);
        await Grant("Read source B", ["sandbox.read"], TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate!.Id));
        var visible = await services.Approvals.ListAsync(s.Tenant, reviewer, new()); Assert.Equal(1, visible.Total); Assert.Single(visible.Items);
        var incomplete = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.False(incomplete.Review!.CanDecide);
        Assert.Equal("approver_unqualified", incomplete.Review.DecisionUnavailableReason);
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.DecideAsync(s.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, incomplete.SecurityRevision, request.Revision).AsTask());
        await Grant("Review source A reads", ["approvals.approve"], TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        var last = await Grant("Review source B reads", ["approvals.approve"], TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate.Id));
        var qualified = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer); Assert.True(qualified.Review!.CanDecide);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, reviewer, true, request.Operation.ActionHash, qualified.SecurityRevision, request.Revision);
        var frame = await ResumeSandboxApproval(f, s, services.Approvals);
        if (revoke) await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, last, await Revision());
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!);
        Assert.Equal(revoke ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
        using var result = JsonDocument.Parse(Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.ToolCallId == "exact-read").Text);
        Assert.Equal(revoke, result.RootElement.GetProperty("inspectionUnavailable").GetBoolean());
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(s.Session.ExecutorPrincipalId, receipt.ExecutorPrincipalId);
        Assert.Equal(user.PrincipalId, (await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(s.Tenant, request.RequestId))!.HumanPrincipalId);
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_read_Review_obeys_secondary_independence_and_does_not_create_unneeded_or_unapprovable_waits(bool sqlite, bool independent)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        if (independent)
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
                .SaveAsync(s.Tenant, SandboxHuman(s), "sessions.read", new(OperationApprovalMode.Always, true), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var services = SandboxReadReviewServices(f, s);
        var result = await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Status, null, s.Run.Revision, s.Run.Claim!.Generation);
        Assert.Equal(independent ? OperationDecisionKind.Denied : OperationDecisionKind.Allowed, result.Decision);
        Assert.Equal(independent ? "no_eligible_approver" : "allowed", result.ReasonCode); Assert.Null(result.Request);
        Assert.Equal(AgentRunStatus.Running, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
        Assert.Equal(SandboxLeaseState.Ready, (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!.State);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_read_Review_decision_transaction_rechecks_exact_selection_and_keeps_stale_cancellation_available(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, inspection: true, alternate: true); await ReadySandbox(f, s); await GrantReadReviewScopes(f, s);
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System)
            .SaveAsync(s.Tenant, SandboxHuman(s), "sandbox.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        s = await CheckpointSandboxRead(f, s, SandboxReadKind.Status); var services = SandboxReadReviewServices(f, s);
        var request = (await services.Approvals.PrepareSandboxReadAsync(new(s.Tenant, s.Run.AgentRunId), SandboxReadKind.Status, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        var p = SetupProfiles(f); var saved = (await ((ITenantSandboxRuntimeStore)f.Iam).ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        await new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantSandboxRuntimeStore)f.Iam, f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host)
            .RequestProfileSwitchAsync(s.Tenant, s.Human, new(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, null, s.Alternate!.Id, 1, 1),
                (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var approvals = (ITenantApprovalStore)f.Iam;
        async Task<bool> Decide(TenantApprovalStatus outcome)
        {
            var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var at = DateTimeOffset.UtcNow; var next = request.Decide(outcome, request.Revision, at);
            return await approvals.TryDecideApprovalAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                next, request.Revision, new(f.Ids.NewId(), request.RequestId, s.Tenant, s.Human.PrincipalId, outcome, request.Operation.ActionHash, next.Revision, at), null,
                new(f.Ids.NewId(), s.Tenant, request.RequestId, request.RunId, next.Revision, 1, at, null), SandboxHuman(s),
                new(f.Ids.NewId(), s.Tenant, s.Human.PrincipalId, "approvals.decide", request.RequestId.ToString("D"), "allowed", outcome == TenantApprovalStatus.Cancelled ? "approval_cancelled" : "approval_approved", at));
        }
        var denied = await Assert.ThrowsAsync<SecurityConflictException>(() => Decide(TenantApprovalStatus.Approved)); Assert.Equal("sandbox_read_target_changed", denied.ReasonCode);
        Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(s.Tenant, request.RequestId))!.Status);
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s)); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
        Assert.True(await Decide(TenantApprovalStatus.Cancelled));
    }

    [Fact]
    public async Task Tenant_Sandbox_read_Review_migration_preserves_prior_control_payload_and_owned_wait_without_rewriting_JSON()
    {
        await using var f = await Fixture.Create(true); var s = await PrepareRuntimeSandbox(f, controls: true); await ReadySandbox(f, s); await GrantModelControl(f, s);
        var service = SandboxControlServices(f, s).Approvals;
        var request = (await service.PrepareSandboxControlAsync(new(s.Tenant, s.Run.AgentRunId), SandboxControlKind.ResetEnvironment, null, s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var row = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(); var json = JsonNode.Parse(row.PayloadJson)!;
            json["Operation"]!.AsObject().Remove("SandboxRead"); json["Snapshot"]!.AsObject().Remove("SandboxRead"); var prior = json.ToJsonString();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson={prior} WHERE RequestId={request.RequestId}");
            await HistoricalTenantDatabase.AssertApprovalUpgradeAsync(f.Contexts!,
                "20261018021925_TenantSandboxControlReview", "20261018025233_TenantSandboxReadReview",
                [.. HistoricalTenantDatabase.CredentialAdmissionTables.TakeWhile(table => table != "TenantApprovalRequests"),
                    "TenantSandboxProfiles", "SandboxProfiles", "SandboxSessionLeases", "SandboxExecutions",
                    .. HistoricalTenantDatabase.CredentialAdmissionTables.SkipWhile(table => table != "TenantApprovalRequests")]);
            Assert.Equal(prior, (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync()).PayloadJson);
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
        var reopened = new SqliteIamStore(f.Contexts!, f.Content);
        Assert.Equal(request, await reopened.FindApprovalAsync(s.Tenant, request.RequestId));
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await reopened.FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!.Status);
    }

}
