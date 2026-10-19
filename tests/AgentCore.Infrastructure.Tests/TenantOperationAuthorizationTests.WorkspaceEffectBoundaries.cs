using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
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
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, ModelToolCall Call, RoleAssignment Grant)> WorkspaceEffectCase(Fixture f)
    {
        var s = await Setup(f, "general-assistant", 32); var frame = await ClaimArtifactRun(f, s);
        var grant = await GrantWorkspaceRuntimeReads(f, s, ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"]);
        var call = new ModelToolCall("owned-workspace-effect", ToolCatalog.WorkspaceWrite, """{"path":"/working/saved.txt","content":"Exact mutation body"}""");
        await WorkspaceEffectCheckpoint(f, frame, call); return (s, frame, call, grant);
    }
    private static async Task<TenantWorkspaceDispatch> StageWorkspaceFence(Fixture f, TenantRuntimeFrame frame, ModelToolCall call)
    {
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId))!; var state = (await f.Iam.ReadTenantAsync(frame.Owner.TenantId.Value))!;
        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(state.Tenant.TenantId, run.AgentInstanceId))!;
        var args = TenantWorkspaceMutationContract.Parse(run.SessionId, call.Name, call.ArgumentsJson);
        var op = new AgentRunDirectOperation(f.Ids.NewId(), call.Id, call.Name, args.CanonicalArguments, args.ParametersHash, agent.Instance.Revision, agent.Instance.PersonaRevision);
        var commit = new TenantWorkspaceEffectCommit(run.AgentRunId, frame.Generation, run.Revision, op, TenantWorkspaceMutationContract.ActionHash(run.SessionId, args.ParametersHash));
        var scope = TenantWorkspaceRules.Scope(TenantWorkspaceEffectRules.Owner(run, args.Path!));
        var start = await ((ITenantWorkspaceOperationStore)f.Iam).TryStartWorkspaceEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), state.Tenant.TenantId, agent.PrincipalId, TenantWorkspaceEffectRules.Dispatch, scope.ResourceId!, "allowed", "workspace_mutation_dispatched", f.Clock.GetUtcNow()));
        return start!.Fence!;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_unknown_acknowledgement_is_receipted_once_and_never_replayed_after_revocation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceEffectCase(f); var count = 0;
        var hook = new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            afterEffect: () => throw new IOException("Private native acknowledgement lost"));
        var service = WorkspaceEffects(f, hook); var first = await service.ExecuteAsync(v.Frame, v.Call);
        Assert.Equal(TenantOperationOutcome.Indeterminate, first.Receipt!.Outcome); Assert.DoesNotContain("Private native", JsonSerializer.Serialize(first));
        var content = await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/saved.txt", 1024);
        Assert.Equal("Exact mutation body", System.Text.Encoding.UTF8.GetString(content.Bytes));
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        Assert.Equal(first, await service.ExecuteAsync(v.Frame, v.Call)); Assert.Equal(1, count);
        Assert.Equal(0, await service.RecoverAsync());
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(v.Frame, v.Call with { ArgumentsJson = """{"path":"/working/saved.txt","content":"changed"}""" }).AsTask());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_commit_gate_blocks_current_permission_loss_and_records_uncertainty_without_retry(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceEffectCase(f);
        await SeedRuntimeWorkspace(f, v.Setup, true, "saved.txt", "original"u8.ToArray());
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeCommit: async () =>
            await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision)));
        var result = await service.ExecuteAsync(v.Frame, v.Call); Assert.Equal(TenantOperationOutcome.Indeterminate, result.Receipt!.Outcome);
        var content = await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/saved.txt", 1024);
        Assert.Equal("original", System.Text.Encoding.UTF8.GetString(content.Bytes));
        Assert.Equal(result, await service.ExecuteAsync(v.Frame, v.Call));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_reserved_abandoned_dispatch_recovers_uncertain_without_calling_files(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T02:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await WorkspaceEffectCase(f); var fence = await StageWorkspaceFence(f, v.Frame, v.Call);
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId, (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        clock.Advance(TimeSpan.FromMinutes(6)); var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        Assert.Equal(1, await service.RecoverAsync()); Assert.Equal(0, await service.RecoverAsync()); Assert.Equal(0, calls);
        var receipt = await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(v.Setup.Tenant, v.Frame.RunId, v.Call.Id);
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt!.Outcome); Assert.Equal(fence.Origin.DirectOperation!.OperationId, receipt.OperationId);
        Assert.Empty((await WorkspaceReader(f).ListTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working", null, 100)).Items);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_concurrent_same_call_dispatches_only_one_native_mutation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceEffectCase(f); var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
        async Task Attempt() { try { await service.ExecuteAsync(v.Frame, v.Call); } catch (SecurityConflictException) { } }
        await Task.WhenAll(Attempt(), Attempt()); var final = await service.ExecuteAsync(v.Frame, v.Call);
        Assert.Equal(TenantOperationOutcome.Completed, final.Receipt!.Outcome); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Tenant_workspace_effects_SQLite_shape_reopen_and_rollback_guards_preserve_dispatch()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T02:00:00Z"));
        await using var f = await Fixture.Create(true, clock, builtInAuthority: true);
        await using (var db = f.Contexts!.CreateDbContext()) { await db.GetService<IMigrator>().MigrateAsync("20261018225721_TenantWorkspaceSearchReview"); await db.Database.MigrateAsync(); }
        var v = await WorkspaceEffectCase(f); var fence = await StageWorkspaceFence(f, v.Frame, v.Call);
        foreach (var field in new[] { "sourceCallId", "toolName", "operationId", "parametersHash", "instanceRevision", "argumentsJson" })
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.Set<AgentRunRecord>().SingleAsync(r => r.AgentRunId == v.Frame.RunId.ToString("D"));
            var payload = JsonNode.Parse(row.PayloadJson)!; var direct = payload["sideEffect"]!["externalEffect"]!["directOperation"]!;
            if (field is "sourceCallId" or "toolName") direct.AsObject().Remove(field);
            if (field == "operationId") direct[field] = f.Ids.NewId().ToString("D");
            if (field == "parametersHash") direct[field] = new string('z', 64);
            if (field == "instanceRevision") direct[field] = 0;
            if (field == "argumentsJson") direct[field] = "[]";
            row.PayloadJson = payload.ToJsonString(); Assert.Equal(1811, Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = f.Contexts!.CreateDbContext()) await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261018225721_TenantWorkspaceSearchReview"));
        var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
        clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(fence, Assert.Single(await ((ITenantWorkspaceOperationStore)reopened).RecoverableWorkspaceEffectsAsync(f.Clock.GetUtcNow(), 100)));
        Assert.Equal(1, await WorkspaceEffects(f).RecoverAsync());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_unavailable_settlement_returns_uncertain_and_recovers_without_native_replay(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T02:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var v = await WorkspaceEffectCase(f); var count = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { count++; return Task.CompletedTask; }), new UnavailableWorkspaceSettlement((ITenantWorkspaceOperationStore)f.Iam));
        var result = await service.ExecuteAsync(v.Frame, v.Call);
        Assert.True(result.IsUncertain); Assert.Equal(TenantWorkspaceEffectRules.Uncertain, result.ReasonCode); Assert.NotNull(result.UnsettledOperationId); Assert.Null(result.Receipt);
        Assert.Null(await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(v.Setup.Tenant, v.Frame.RunId, v.Call.Id));
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ExecuteAsync(v.Frame, v.Call).AsTask()); Assert.Equal(1, count);
        clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(1, await WorkspaceEffects(f).RecoverAsync()); Assert.Equal(0, await WorkspaceEffects(f).RecoverAsync());
        var receipt = await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(v.Setup.Tenant, v.Frame.RunId, v.Call.Id);
        Assert.Equal(result.UnsettledOperationId, receipt!.OperationId); Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome);
        Assert.Equal("Exact mutation body", System.Text.Encoding.UTF8.GetString((await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/saved.txt", 1024)).Bytes));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_workspace_effects_deleted_Session_keeps_factual_recovery_without_live_resource_lookup_or_native_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await WorkspaceEffectCase(f);
        var fence = await StageWorkspaceFence(f, v.Frame, v.Call); await CancelArtifactRun(f, v.Setup, v.Frame);
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(v.Setup.Tenant, v.Frame.SessionId))!;
        await f.Sessions.ChangeAsync(v.Setup.Tenant, v.Setup.Owner, new(v.Frame.SessionId, session.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Setup.Tenant, v.Frame.RunId));
        var calls = 0;
        var service = WorkspaceEffects(f, new WorkspaceMutationHook(WorkspaceReader(f), beforeEffect: () => { calls++; return Task.CompletedTask; }));
        Assert.Equal(1, await service.RecoverAsync()); Assert.Equal(0, await service.RecoverAsync()); Assert.Equal(0, calls);
        var receipt = await ((ITenantDirectOperationStore)f.Iam).FindDirectOperationReceiptAsync(v.Setup.Tenant, v.Frame.RunId, v.Call.Id);
        Assert.Equal(fence.Origin.DirectOperation!.OperationId, receipt!.OperationId); Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome);
    }

    private sealed class UnavailableWorkspaceSettlement(ITenantWorkspaceOperationStore inner) : ITenantWorkspaceOperationStore
    {
        public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenant, Guid run, string call, CancellationToken ct = default) => inner.FindDirectOperationReceiptAsync(tenant, run, call, ct);
        public ValueTask<TenantWorkspaceEffectStart?> TryStartWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantWorkspaceEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryStartWorkspaceEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default) => ValueTask.FromResult<TenantOperationReceipt?>(null);
        public ValueTask<IReadOnlyList<TenantWorkspaceDispatch>> RecoverableWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableWorkspaceEffectsAsync(at, limit, ct);
        public ValueTask<TenantApprovedWorkspaceStart?> TryStartApprovedWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryStartApprovedWorkspaceEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleApprovedWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantApprovedWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default) => ValueTask.FromResult<TenantOperationReceipt?>(null);
        public ValueTask<IReadOnlyList<TenantApprovedWorkspaceDispatch>> RecoverableApprovedWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableApprovedWorkspaceEffectsAsync(at, limit, ct);
    }
}
