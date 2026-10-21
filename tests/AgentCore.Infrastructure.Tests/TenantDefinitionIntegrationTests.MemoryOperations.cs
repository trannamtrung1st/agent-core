using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static (TenantMemoryOperationService Memory, TenantApprovalService Approvals, TenantApprovedOperationService Effects, TenantRunContinuationService Continuation) MemoryServices(Fixture f, IIamStore? current = null)
    {
        current ??= f.Iam; var iam = new IamAuthorizer(current); var runs = (ITenantRunStore)current;
        var operations = new TenantOperationAuthorizer(current, runs, (ITenantAgentStore)current, iam, new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
        var builtIns = new SessionNoBuiltinFallback(); var config = new TenantRunConfigurationResolver((ITenantAgentStore)current, f.Definitions, builtIns,
            new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)current);
        var memory = new TenantMemoryOperationService((ITenantMemoryOperationStore)current, (ITenantLearnedMemoryStore)current, operations, iam, TimeProvider.System);
        var approvals = new TenantApprovalService(current, (ITenantApprovalStore)current, (ITenantAgentStore)current, operations, iam, f.Identity, f.Ids, TimeProvider.System,
            config, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, (ITenantApprovedOperationStore)current, memoryOperations: memory);
        var effects = new TenantApprovedOperationService(current, runs, (ITenantAgentStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, operations,
            config, f.Ids, TimeProvider.System, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, memoryOperations: memory);
        return (memory, approvals, effects, new(runs, (ITenantApprovalStore)current, effects, TimeProvider.System, (ITenantRuntimeStore)current, f.Ids, memoryOperations: memory));
    }
    private static async Task<(MemorySetup Owner, MemoryTurn Turn, TenantLearnedMemory[] Sources)> MemoryOperationFixture(Fixture f)
    {
        var owner = await MemoryOwner(f, operations: true); var learned = await MemoryClaim(f, owner);
        var plan = await MemoryPlan(f, learned, Learned("Prefer complete grouped evidence") with { Subject = "Evidence grouping" },
            Learned("Keep exact durable witnesses") with { Subject = "Evidence retention" });
        await MemoryCommit(f, learned, plan); var turn = await MemoryClaim(f, owner);
        return (owner, turn, plan.Changes.Select(c => c.Value).Where(m => m.Scope == MemoryScope.IdentityUser).OrderBy(i => i.MemoryId).ToArray());
    }
    private static ModelToolCall Consolidate(TenantLearnedMemory[] sources) => new("consolidate", ToolCatalog.MemoryConsolidate,
        JsonSerializer.Serialize(new { sourceMemoryIds = sources.Select(m => m.MemoryId).ToArray(), kind = "Preference", subject = "Evidence practice", content = "Group complete evidence and retain durable witnesses" }));
    private static async Task<TenantApprovalRequest> PrepareMemoryReview(Fixture f, TenantRuntimeFrame frame, ModelToolCall exact, TenantApprovalService approvals)
    {
        var authorization = new ModelToolCall("memory-consent", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = TenantMemoryToolContract.ReadOnly(exact.Name) ? "inspect_memory" : "manage_memory", memory = new { tool = exact.Name, arguments = JsonDocument.Parse(exact.ArgumentsJson).RootElement } }));
        var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(frame.Owner.TenantId!.Value, frame.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, DateTimeOffset.UtcNow, frame.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [authorization])]), 1, 0, 180000, 0), null), f.Ids.NewId());
        var result = await approvals.PrepareMemoryAsync(new(frame.Owner.TenantId!.Value, frame.RunId), exact, run.Revision, frame.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, result.Decision); var request = Assert.IsType<TenantApprovalRequest>(result.Request);
        var reader = new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer);
        var feed = new TenantConversationApprovalService(reader, approvals, (ITenantApprovalStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantApprovedOperationStore)f.Iam);
        var rootUser = request.Operation.MemoryOperation!.UserPrincipalId!.Value;
        var user = (await f.Iam.ReadTenantAsync(request.TenantId))!.Users.Single(u => u.PrincipalId == rootUser);
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var item = Assert.Single((await feed.ListAsync(request.TenantId, request.SessionId, new(user.AccountId, login.SessionId, user.PrincipalId))).Items);
        Assert.Equal(request.RequestId, item.RequestId); Assert.True(item.CanInspect); Assert.True(item.Waiting); Assert.Equal(TenantMemoryToolContract.ReadOnly(exact.Name), item.MemoryRead);
        Assert.DoesNotContain("grouped evidence", JsonSerializer.Serialize(item)); return request;
    }
    private static async Task<TenantRuntimeFrame> DecideMemoryReview(Fixture f, MemorySetup owner, TenantApprovalRequest request, TenantApprovalService approvals, bool approve = true)
    {
        var view = await approvals.ReadViewAsync(owner.Tenant, request.RequestId, new TenantSessionInitiator(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId)); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason);
        await approvals.DecideAsync(owner.Tenant, request.RequestId, new TenantSessionInitiator(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId), approve, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); var runs = (ITenantRunStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(owner.Tenant, request.RunId))!;
        if (run.Result?.ContinuationRunId is { } successor) run = (await runs.FindTenantRunAsync(owner.Tenant, successor))!;
        var at = DateTimeOffset.UtcNow; run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_memory_operation_exact_consent_consolidates_atomically_and_replays_receipt_without_reapplying(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await MemoryOperationFixture(f); var services = MemoryServices(f); var call = Consolidate(s.Sources);
        Assert.True(TenantTaskExecutionContract.Allows(s.Turn.Run), JsonSerializer.Serialize(s.Turn.Run.Admission.ExecutionContext));
        Assert.True(TenantMemoryToolContract.Allows(s.Turn.Run.Admission.Configuration!.Definition, call.Name), JsonSerializer.Serialize(new { s.Turn.Run.Admission.Configuration.Definition.MemoryPolicy, s.Turn.Run.Admission.Configuration.Definition.Environment, s.Turn.Run.Admission.Configuration.Definition.SelfManagement }));
        var direct = await services.Memory.InspectAsync(s.Turn.Frame, call, 6000); Assert.Contains("requiresApproval", direct); Assert.DoesNotContain("Evidence practice", direct);
        var request = await PrepareMemoryReview(f, s.Turn.Frame, call, services.Approvals); Assert.Equal(ApprovalKind.Review, request.Kind);
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, s.Turn.Run.AgentRunId))!.Status);
        var frame = await DecideMemoryReview(f, s.Owner, request, services.Approvals);
        if (sqlite) services = MemoryServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var resumed = await services.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Owner.Tenant, frame.RunId))!);
        Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); var output = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var json = JsonDocument.Parse(output); Assert.True(json.RootElement.GetProperty("applied").GetBoolean()); Assert.False(json.RootElement.GetProperty("replayAllowed").GetBoolean());
        var replay = await services.Memory.InspectAsync(frame, call, 6000); Assert.Contains("alreadyCompleted", replay); Assert.Contains("\"applied\":false", replay);
        var get = await services.Memory.PrepareAsync(new(s.Owner.Tenant, frame.RunId), frame.Generation, ToolCatalog.ContinuityGet,
            JsonSerializer.Serialize(new { kind = "Memory", id = request.Operation.MemoryOperation!.ResultMemoryId })); var result = Assert.Single(get.Items);
        Assert.Equal("Evidence practice", result.Subject); Assert.Equal(s.Sources.Select(m => m.MemoryId).Order(), TenantMemoryOperationMutation.Sources(result));
        foreach (var source in s.Sources)
        {
            var old = Assert.Single((await services.Memory.PrepareAsync(new(s.Owner.Tenant, frame.RunId), frame.Generation, ToolCatalog.ContinuityGet, JsonSerializer.Serialize(new { kind = "Memory", id = source.MemoryId }))).Items);
            Assert.Equal(MemoryItemStatus.Superseded, old.Status); Assert.Equal(source.Content, old.Content); Assert.Equal(source.Revision + 1, old.Revision);
        }
        Assert.Equal(resumed.Outcome.OperationId, (await services.Effects.ExecuteAsync(new(s.Owner.Tenant, frame.RunId), request.RequestId, request.Operation.OperationId, frame.Generation, request.Operation.ActionHash)).OperationId);
        Assert.Single(await f.Iam.ReadAuditAsync(s.Owner.Tenant, 200), a => a.Action == "operations.memory.apply");
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(7, await db.Set<TenantLearnedMemoryRecord>().CountAsync());
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantLearnedMemories SET SourceMemoryIdsJson=NULL WHERE MemoryId={result.MemoryId}"));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.MemoryOperation.ArgumentsJson','{{}}') WHERE RequestId={request.RequestId}"));
        }
    }
}
