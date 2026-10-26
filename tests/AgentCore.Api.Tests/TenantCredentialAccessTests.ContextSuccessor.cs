using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class ContextSqlDiagnostics : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception?.GetBaseException() is Microsoft.Data.Sqlite.SqliteException error) Errors.Enqueue(error.Message); else if (eventId.Id == 4721) Errors.Enqueue(formatter(state, exception)); }
    }
    private sealed class ContextSuccessorModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(); private int switched;
        public Guid Destination; public bool Continue; public int SwitchCalls;
        public bool HoldAfterSwitch; public TaskCompletionSource AfterSwitch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            var atDestination = request.Context?.Text(ContextSectionId.Space).Contains(Destination.ToString("D"), StringComparison.Ordinal) == true;
            if (Interlocked.Exchange(ref switched, 1) == 0 && !atDestination)
            {
                Assert.Contains(request.Tools ?? [], t => t.Name == "spaces.activate"); Interlocked.Increment(ref SwitchCalls);
                yield return new ModelToolCallEvent(new("switch-context-once", "spaces.activate", JsonSerializer.Serialize(new { spaceId = Destination, expectedSelectionRevision = 1, continueTask = Continue })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (HoldAfterSwitch && !atDestination) { AfterSwitch.TrySetResult(); await Finish.Task.WaitAsync(cancellationToken); }
            await foreach (var e in inner.GenerateAsync(request, cancellationToken)) yield return e;
        }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public Task Model_context_switch_continues_at_most_once_in_same_Session_without_rebinding_source(bool sqlite, bool continueTask) => ContextSuccessorWorkflow(sqlite, continueTask);
    [Fact]
    public Task Queued_context_successor_recovers_once_after_SQLite_restart() => ContextSuccessorWorkflow(true, true, restart: true);
    [Fact]
    public Task Context_successor_SQL_rejects_rebinding_and_lossy_downgrade_of_actual_completed_work() => ContextSuccessorWorkflow(true, true, schemaProof: true);
    [Theory] [InlineData(false,"cancel")] [InlineData(true,"cancel")] [InlineData(false,"later-human")] [InlineData(true,"later-human")]
    [InlineData(false,"member-removed")] [InlineData(true,"member-removed")]
    public Task Context_successor_does_not_revive_cancelled_superseded_or_unavailable_destination_work(bool sqlite,string boundary) =>
        ContextSuccessorWorkflow(sqlite,true,boundary:boundary);
    private async Task ContextSuccessorWorkflow(bool sqlite, bool continueTask, bool restart = false, string? boundary = null, bool schemaProof = false)
    {
        var model = new ContextSuccessorModel { Continue = continueTask, HoldAfterSwitch = boundary is not null }; var diagnostics = new ContextSqlDiagnostics();
        await using var factory = new OAuthFactory(sqlite: sqlite, preserve: restart, execute: true, model: model, servicesOverride: services => services.AddLogging(b => { b.AddProvider(diagnostics); b.AddFilter<ContextSqlDiagnostics>("AgentCore.Application.Security.TenantRunExecutionAuthority", LogLevel.Debug); }));
        using var owner = ConnectedClient(factory);
        if (restart) await factory.Services.GetServices<IHostedService>().OfType<AgentCore.Api.AgentRunHostedService>().Single().StopAsync(CancellationToken.None);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 33, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var principal = agent.GetProperty("principalId").GetGuid();
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Explicit context transition", ["spaces.read", "sessions.read", "sessions.manage", "agents.read", "agents.chat"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, principal, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var spaces = factory.Services.GetRequiredService<TenantSpaceService>();
        var shared = await spaces.CreateAsync(human, tenant, Guid.NewGuid(), "Successor destination", "", "Use the new Space for the remaining work.", await Revision(owner, tenant));
        await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "Worker", "Retain the original task.", [], [], await Revision(owner, tenant), shared.Revision, 0); model.Destination = shared.SpaceId;
        var created = await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync()); var session = (await Json(created)).GetProperty("sessionId").GetGuid();
        var before = await Json(await owner.GetAsync($"{api}/sessions/{session}/context")); var originalSpace = before.GetProperty("selection").GetProperty("spaceId").GetGuid();
        var submitted = await owner.PostAsJsonAsync($"{api}/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Switch to Successor destination" + (continueTask ? " and continue this task." : "."), expectedRevision = await Revision(owner, tenant), expectedSessionRevision = before.GetProperty("sessionRevision").GetInt64() });
        Assert.True(submitted.StatusCode == HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync()); var sourceId = (await Json(submitted)).GetProperty("runId").GetGuid();
        if (boundary is not null)
        {
            await model.AfterSwitch.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var store = factory.Services.GetRequiredService<ITenantRunStore>(); Guid? laterRunId=null;
            if(boundary=="cancel")
            {
                var current=(await store.FindTenantRunAsync(tenant,sourceId))!;
                var cancelled=await owner.PostAsJsonAsync(api+$"/agent-runs/{sourceId}/cancel",new{expectedRevision=await Revision(owner,tenant),expectedRunRevision=current.Revision});
                Assert.True(cancelled.IsSuccessStatusCode,await cancelled.Content.ReadAsStringAsync());
            }
            else if(boundary=="later-human")
            {
                var current=(await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant,session))!;
                var later=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new{messageId=Guid.NewGuid(),text="This is a newer human request in the selected Space.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=current.Snapshot.Revision});
                Assert.True(later.IsSuccessStatusCode,await later.Content.ReadAsStringAsync());laterRunId=(await Json(later)).GetProperty("runId").GetGuid();
            }
            else
            {
                var destination=await spaces.ReadAsync(human,tenant,shared.SpaceId);
                var member=Assert.Single(await spaces.MembersAsync(human,tenant,shared.SpaceId));
                await spaces.SetMemberAsync(human,tenant,shared.SpaceId,instance,"","",[],[],await Revision(owner,tenant),destination.Revision,member.Revision,remove:true);
            }
            model.Finish.TrySetResult();
            var expected=boundary=="later-human"?2:1;var until=DateTimeOffset.UtcNow.AddSeconds(30);AgentRun[] all=[];
            do
            {
                var list=new List<AgentRun>();foreach(var id in await store.ListTenantRunIdsAsync(tenant,session))list.Add((await store.FindTenantRunAsync(tenant,id))!);all=list.ToArray();
                if(all.Length==expected&&all.All(r=>r.IsTerminal))break;await Task.Delay(20);
            }while(DateTimeOffset.UtcNow<until);
            Assert.Equal(expected,all.Length);Assert.All(all,r=>Assert.True(r.IsTerminal,$"{r.Status}: {r.Failure}"));
            Assert.DoesNotContain(all,r=>r.Admission.Activation.Kind==ActivationKind.ContextContinuation);
            var original=Assert.Single(all,r=>r.AgentRunId==sourceId);Assert.Equal(originalSpace,original.Admission.Configuration!.SpaceContext!.SpaceId);
            // New typed input is queued by the existing mailbox; it blocks an obsolete automatic continuation even when the original turn completes normally.
            Assert.Equal(boundary=="cancel"?AgentRunStatus.Cancelled:AgentRunStatus.Completed,original.Status);
            var boundarySelection=(await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant,session))!.Snapshot.ContextSelection!;
            Assert.Equal(shared.SpaceId,boundarySelection.SpaceId);Assert.Equal(2,boundarySelection.Revision);
            if(laterRunId is {} idLater)
            {
                var later=Assert.Single(all,r=>r.AgentRunId==idLater);Assert.Equal(AgentRunStatus.Completed,later.Status);
                Assert.Equal(ActivationKind.UserTurn,later.Admission.Activation.Kind);Assert.Equal(shared.SpaceId,later.Admission.Configuration!.SpaceContext!.SpaceId);
                Assert.NotEqual(original.Admission.Activation.RootActivationId,later.Admission.Activation.RootActivationId);
            }
            Assert.Equal(1,model.SwitchCalls);Assert.Equal(0,factory.Outbound);return;
        }
        if (restart) Assert.Equal(1, await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(1));
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun[] observed = []; var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            var ids = await runs.ListTenantRunIdsAsync(tenant, session); var list = new List<AgentRun>(); foreach (var id in ids) list.Add((await runs.FindTenantRunAsync(tenant, id))!); observed = list.ToArray();
            if (observed.Length == (continueTask ? 2 : 1) && (restart ? observed.Single(r => r.AgentRunId == sourceId).IsTerminal && observed.Single(r => r.AgentRunId != sourceId).Status == AgentRunStatus.Queued : observed.All(r => r.IsTerminal))) break;
            await Task.Delay(20);
        } while (DateTimeOffset.UtcNow < deadline);
        if (restart)
        {
            Assert.Equal(2, observed.Length); Assert.Equal(AgentRunStatus.Completed, observed.Single(r => r.AgentRunId == sourceId).Status);
            var queued = observed.Single(r => r.AgentRunId != sourceId); Assert.Equal(AgentRunStatus.Queued, queued.Status);
            var recoveryModel = new ContextSuccessorModel { Continue = true, Destination = shared.SpaceId };
            var root = factory.Root; await factory.DisposeAsync();
            await using var reopened = new OAuthFactory(existingRoot: root, sqlite: true, execute: true, model: recoveryModel);
            using var recoveryClient = ConnectedClient(reopened); var recoveredRuns = reopened.Services.GetRequiredService<ITenantRunStore>();
            deadline = DateTimeOffset.UtcNow.AddSeconds(30); AgentRun? recovered;
            do { recovered = await recoveredRuns.FindTenantRunAsync(tenant, queued.AgentRunId); if (recovered!.IsTerminal) break; await Task.Delay(20); } while (DateTimeOffset.UtcNow < deadline);
            Assert.True(recovered!.Status == AgentRunStatus.Completed, $"Recovered successor: {recovered.Status} {recovered.Failure}");
            Assert.Equal(shared.SpaceId, recovered.Admission.Configuration!.SpaceContext!.SpaceId);
            Assert.Equal(sourceId, recovered.Admission.ExecutionContext!.ParentRunId); Assert.Equal(0, recoveryModel.SwitchCalls);
            Assert.Equal(observed.Select(r => r.AgentRunId).Order(), (await recoveredRuns.ListTenantRunIdsAsync(tenant, session)).Order());
            Assert.Equal(2, (await reopened.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.ContextSelection!.Revision);
            Assert.Equal(0, reopened.Outbound); return;
        }
        Assert.All(observed, r => Assert.True(r.Status == AgentRunStatus.Completed, $"Run {r.AgentRunId}: {r.Status} {r.Failure}"));
        var source = observed.Single(r => r.AgentRunId == sourceId);
        if (continueTask)
        {
            var pending = TenantContextContinuationRules.Pending(source);
            AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(source.Checkpoint, out var messages);
            Assert.True(pending is not null, $"Pending absent: {source.Status}; attention={source.Result?.AttentionRequired}; sideEffect={source.SideEffect.Disposition}; messages=" + string.Join(";", (messages ?? []).Select(m => m.Role + ":" + m.Name + ":" + string.Join(",", (m.ToolCalls ?? []).Select(c => c.Name)))));
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            var entries = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items;
            if (observed.Length == 2) Assert.False(TenantContextContinuationRules.SafeSession(source, source, saved, observed, entries));
        }
        Assert.True(observed.Length == (continueTask ? 2 : 1), $"Expected {(continueTask ? 2 : 1)} Runs; actual {observed.Length}; SQL errors: " + string.Join(";", diagnostics.Errors.Distinct()));
        Assert.Equal(originalSpace, source.Admission.Configuration!.SpaceContext!.SpaceId);
        Assert.Equal(1, model.SwitchCalls); Assert.Equal(0, factory.Outbound);
        var selection = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.ContextSelection!;
        Assert.Equal(shared.SpaceId, selection.SpaceId); Assert.Equal(2, selection.Revision);
        if (continueTask)
        {
            var successor = observed.Single(r => r.AgentRunId != sourceId);
            Assert.Equal(ActivationKind.ContextContinuation, successor.Admission.Activation.Kind); Assert.Equal(ExecutionOrigin.ContextContinuation, successor.Admission.ExecutionContext!.Origin);
            Assert.Equal(source.Admission.Activation.RootActivationId, successor.Admission.Activation.RootActivationId); Assert.Equal(source.Owner, successor.Owner);
            Assert.Equal(source.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, successor.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            Assert.Equal(sourceId, successor.Admission.ExecutionContext.ParentRunId); Assert.Equal(1, successor.MaxAttempts);
            Assert.Equal(shared.SpaceId, successor.Admission.Configuration!.SpaceContext!.SpaceId); Assert.Equal(session, successor.SessionId);
            Assert.Contains(model.Requests, r => r.Context!.Text(ContextSectionId.Space).Contains("Successor destination", StringComparison.Ordinal));
            _ = await TenantExecutionLineage.RootAsync(successor, runs, CancellationToken.None);
            if (schemaProof)
            {
                // A native SQLite backup gives migration fault injection a consistent,
                // disposable copy without running an old schema under active workers.
                await using var originalDb = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
                await originalDb.Database.OpenConnectionAsync();
                var proofPath = Path.Combine(factory.Root, "context-successor-schema-proof.db");
                await using (var copy = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + proofPath + ";Foreign Keys=True"))
                {
                    await copy.OpenAsync(); ((Microsoft.Data.Sqlite.SqliteConnection)originalDb.Database.GetDbConnection()).BackupDatabase(copy);
                }
                await using var proof = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + proofPath + ";Foreign Keys=True").Options);
                var successorKey = successor.AgentRunId.ToString("D");
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => proof.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.admission.configuration.spaceContext.spaceId',{originalSpace.ToString("D")}) WHERE AgentRunId={successorKey}"));
                var migrator = proof.GetService<IMigrator>();
                var refusal = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync("20261025050000_SpaceAutomationPins"));
                Assert.Contains("CHECK constraint failed", refusal.Message, StringComparison.Ordinal);
                await migrator.MigrateAsync(); Assert.False(proof.Database.HasPendingModelChanges());
                var row = await proof.Set<AgentRunRecord>().AsNoTracking().SingleAsync(r => r.AgentRunId == successorKey);
                Assert.Equal(shared.SpaceId, JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("admission").GetProperty("configuration").GetProperty("spaceContext").GetProperty("spaceId").GetGuid());
                await proof.Database.OpenConnectionAsync(); await using var check = proof.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
                await using var violations = await check.ExecuteReaderAsync(); Assert.False(await violations.ReadAsync());
            }
        }
    }
}
