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

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantContinuityReadService Reads, TenantApprovalService Approvals, TenantApprovedOperationService Effects, TenantRunContinuationService Continuation) ContinuityServices(Fixture f, IIamStore? current = null)
    {
        current ??= f.Iam; var iam = new IamAuthorizer(current); var runs = (ITenantRunStore)current;
        var operations = new TenantOperationAuthorizer(current, runs, (ITenantAgentStore)current, iam, new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var builtIns = new SessionNoBuiltinFallback(); var config = new TenantRunConfigurationResolver((ITenantAgentStore)current, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)current);
        var memory = new TenantMemoryOperationService((ITenantMemoryOperationStore)current, (ITenantLearnedMemoryStore)current, operations, iam, TimeProvider.System);
        var reads = new TenantContinuityReadService((ITenantContinuityStore)current, (ITenantLearnedMemoryStore)current, operations, iam, TimeProvider.System);
        var approvals = new TenantApprovalService(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, iam, f.Identity, f.Ids, TimeProvider.System,
            config, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, memoryOperations: memory, continuityReads: reads);
        var effects = new TenantApprovedOperationService(current, runs, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, operations,
            config, f.Ids, TimeProvider.System, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, memoryOperations: memory, continuityReads: reads);
        return (reads, approvals, effects, new(runs, (ITenantApprovalStore)current, effects, TimeProvider.System, (ITenantRuntimeStore)current, f.Ids));
    }
    private static ModelToolCall HistoryCall(Guid id, long after = 0, int limit = 20) => new("history", ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Session", id, afterEntrySequence = after, limit }));
    private static async Task<TenantApprovalRequest> PrepareContinuityReview(Fixture f, TenantRuntimeFrame frame, ModelToolCall call, TenantApprovalService approvals)
    {
        var authorization = new ModelToolCall("continuity-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_continuity", continuity = new { tool = call.Name, arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement } }));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId))!;
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint) with {
            TenantMemorySources = TenantMemoryOperationReferences.Read(run.Checkpoint), TenantHistoricalSources = TenantHistoricalReferences.Read(run.Checkpoint),
            TenantExperienceSourceReads = TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceSources = TenantExperienceReferences.Facts(run.Checkpoint) };
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, frame.Generation,
            new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [authorization])], executionState: state), 1, 0, 180000, 0), null), f.Ids.NewId());
        var result = await approvals.PrepareContinuityAsync(new(frame.Owner.TenantId!.Value, frame.RunId), call, run.Revision, frame.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); return Assert.IsType<TenantApprovalRequest>(result.Request);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_combines_owned_memory_and_same_Agent_history_and_exact_entry_windows(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner);
        await MemoryCommit(f, source, await MemoryPlan(f, source, Learned("Retain evidence grouping"))); var current = await MemoryClaim(f, owner); var s = ContinuityServices(f);
        var search = new ModelToolCall("search", ToolCatalog.ContinuitySearch, "{\"query\":\"Evidence\",\"limit\":10}");
        var body = await s.Reads.InspectAsync(current.Frame, search, 6000); using var json = JsonDocument.Parse(body); var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, i => i.GetProperty("kind").GetString() == "Memory"); Assert.Contains(items, i => i.GetProperty("kind").GetString() == "Session" && i.GetProperty("id").GetGuid() == source.Frame.SessionId);
        Assert.DoesNotContain(items, i => i.GetProperty("id").GetGuid() == current.Frame.SessionId); Assert.True(json.RootElement.GetProperty("observationOnly").GetBoolean());
        var exact = await s.Reads.InspectAsync(current.Frame, HistoryCall(source.Frame.SessionId, 0, 1), 6000); using var page = JsonDocument.Parse(exact); var entry = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Single(entry.GetProperty("entries").EnumerateArray()); Assert.True(entry.GetProperty("hasMore").GetBoolean()); Assert.Equal(1, entry.GetProperty("nextAfter").GetInt64());
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, HistoryCall(current.Frame.SessionId), 6000).AsTask());
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, body, ToolCallId: "search", Name: ToolCatalog.ContinuitySearch)]), 1, 0, 180000, 0);
        var proofs = TenantHistoricalReferences.Read(checkpoint); Assert.Single(proofs); await ((ITenantContinuityStore)f.Iam).RequireTenantHistoricalSourcesAsync(new(owner.Tenant, current.Frame.RunId), proofs, current.Frame.Generation);
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges()); var reopened = ContinuityServices(f, new SqliteIamStore(f.Contexts, f.Content)); Assert.Contains("evidence", await reopened.Reads.InspectAsync(current.Frame, HistoryCall(source.Frame.SessionId, 0, 1), 6000)); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_read_Review_withholds_then_consumes_once_and_reuses_only_the_exact_task_window(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always); var s = ContinuityServices(f); var call = HistoryCall(source.Frame.SessionId);
        var before = await s.Reads.InspectAsync(current.Frame, call, 6000); Assert.Contains("requiresApproval", before); Assert.DoesNotContain("Evidence response", before);
        var request = await PrepareContinuityReview(f, current.Frame, call, s.Approvals); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(owner.Tenant, request.RequestId));
        var view = await s.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId)); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason);
        Assert.Contains(view.Review.Changes, c => c.Field == "Current historical excerpts");
        var frame = await DecideMemoryReview(f, owner, request, s.Approvals); if (sqlite) s = ContinuityServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await s.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); var output = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        Assert.Contains("Evidence response completed.", output); Assert.Contains("\"readAuthorized\":true", output); Assert.Contains("\"applied\":false", output);
        var proofs = TenantHistoricalReferences.Read(resumed.Run.Checkpoint); Assert.Single(proofs); await ((ITenantContinuityStore)f.Iam).RequireTenantHistoricalSourcesAsync(new(owner.Tenant, frame.RunId), proofs, frame.Generation);
        Assert.Contains("Evidence response", await s.Reads.InspectAsync(frame, call, 6000)); Assert.Contains("requiresApproval", await s.Reads.InspectAsync(frame, HistoryCall(source.Frame.SessionId, 1, 1), 6000));
        var other = await MemoryClaim(f, owner); Assert.Contains("requiresApproval", await s.Reads.InspectAsync(other.Frame, call, 6000));
        Assert.Equal(resumed.Outcome.OperationId, (await s.Effects.ExecuteAsync(new(owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash)).OperationId);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_search_omits_unreadable_private_memory_without_losing_independent_history(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); var plan = await MemoryPlan(f, source, Learned("PRIVATE_LEARNED_VALUE")); await MemoryCommit(f, source, plan);
        var current = await MemoryClaim(f, owner); await MemoryPermissions(f, owner, "agents.read", "agents.chat", "sessions.read"); var s = ContinuityServices(f);
        var body = await s.Reads.InspectAsync(current.Frame, new("search", ToolCatalog.ContinuitySearch, "{\"query\":\"Evidence\"}"), 6000); Assert.Contains("\"kind\":\"Session\"", body); Assert.DoesNotContain("PRIVATE_LEARNED_VALUE", body); Assert.DoesNotContain("\"kind\":\"Memory\"", body);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, new("memory", ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Memory", id = plan.Changes[0].Value.MemoryId })), 6000).AsTask());
        await MemoryPermissions(f, owner, "agents.read", "agents.chat", "memory.read"); await Assert.ThrowsAsync<AccessDeniedException>(() => s.Reads.InspectAsync(current.Frame, HistoryCall(source.Frame.SessionId), 6000).AsTask());
    }
}
