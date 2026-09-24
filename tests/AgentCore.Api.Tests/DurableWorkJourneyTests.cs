using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class DurableWorkJourneyTests
{
    [Fact(Timeout = 30_000)]
    public async Task Detached_reminder_completes_once_without_writing_chat_history()
    {
        var db = TempDb();
        try
        {
            string sessionId;
            string resultText;
            Guid workItemId;
            await using (var host = new DurableSqliteHostFactory(db, runScheduler: false))
            {
                var client = OwnerClient(host);
                var session = await CreateAsync(client, "general-assistant", null);
                sessionId = session.SessionId;
                var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                var due = now.AddHours(2);
                var owner = await OwnerAsync(host.Services, Guid.Parse(sessionId));
                await host.Services.GetRequiredService<ITriggerRegistrationService>().CreateAsync(
                    new TriggerRegistrationDraft(
                        new TriggerOwner(owner.AgentInstanceId, owner.ProfileId),
                        "Call John",
                        new OneShotSchedule(due, "UTC"),
                        due,
                        null,
                        TriggerAuthorizationOrigin.CurrentUserTurn,
                        Guid.Parse(sessionId),
                        null));

                var asOf = due.AddMinutes(1);
                var pass = await host.Services.GetRequiredService<TriggerScheduler>().RunOnceAsync(asOf);
                Assert.Equal(1, pass.Admitted);
                await host.Services.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
                var admitted = await host.Services.GetRequiredService<DurableWorkIntake>().AcceptAwaitingAsync();
                Assert.Equal(1, admitted.Accepted);
                Assert.Equal(1, await host.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(asOf, 10));

                var listed = await client.GetFromJsonAsync<WorkItemListResponse>($"/api/v2/sessions/{sessionId}/work-items");
                var item = Assert.Single(listed!.Items);
                Assert.Equal("completed", item.Status);
                Assert.Equal("Scheduled reminder", item.Origin);
                workItemId = Guid.Parse(item.WorkItemId);
                var result = await client.GetFromJsonAsync<WorkItemResultResponse>(
                    $"/api/v2/sessions/{sessionId}/work-items/{item.WorkItemId}/result");
                resultText = result!.Text;
                Assert.Equal("Hello from synthetic.", resultText);
                var history = await host.Services.GetRequiredService<IMemoryStore>()
                    .ReadHistoryAsync(Guid.Parse(sessionId), 0, 50);
                Assert.DoesNotContain(history, entry => entry.Text.Contains(resultText, StringComparison.Ordinal));
            }

            await using var reopened = new DurableSqliteHostFactory(db, runScheduler: false);
            var again = OwnerClient(reopened);
            var survived = await again.GetFromJsonAsync<WorkItemListResponse>($"/api/v2/sessions/{sessionId}/work-items");
            Assert.Equal(workItemId.ToString(), Assert.Single(survived!.Items).WorkItemId);
            var survivedResult = await again.GetFromJsonAsync<WorkItemResultResponse>(
                $"/api/v2/sessions/{sessionId}/work-items/{workItemId}/result");
            Assert.Equal(resultText, survivedResult!.Text);
            Assert.Equal(0, await reopened.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(
                reopened.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
                10));
        }
        finally
        {
            DeleteDb(db);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Approval_survives_restart_and_runs_the_prepared_action_once()
    {
        var db = TempDb();
        try
        {
            string sessionId;
            string workItemId;
            long revision;
            long approvalRevision;
            string approvalId;
            string actionHash;
            await using (var host = new DurableSqliteHostFactory(db, runScheduler: false))
            {
                var client = OwnerClient(host);
                var session = await CreateAsync(client, "approval-demo", null);
                sessionId = session.SessionId;
                var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                var owner = await OwnerAsync(host.Services, Guid.Parse(sessionId));
                await AdmitApplicationEventAsync(
                    host.Services.GetRequiredService<ITriggerStore>(),
                    new TriggerOwner(owner.AgentInstanceId, owner.ProfileId),
                    now,
                    "Please run the sensitive approval harness.");
                var admitted = await host.Services.GetRequiredService<DurableWorkIntake>().AcceptAwaitingAsync();
                Assert.Equal(1, admitted.Accepted);
                var asOf = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                Assert.Equal(1, await host.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(asOf, 10));

                var listed = await client.GetFromJsonAsync<WorkItemListResponse>($"/api/v2/sessions/{sessionId}/work-items");
                var item = Assert.Single(listed!.Items);
                Assert.Equal("needsApproval", item.Status);
                Assert.Equal("Run sensitive demo action: Synthetic sensitive approval", item.ApprovalPreview);
                workItemId = item.WorkItemId;
                revision = item.Revision;
                approvalRevision = item.ApprovalRevision!.Value;
                approvalId = item.ApprovalId!;
                actionHash = item.ActionHash!;
                var hidden = await client.GetAsync($"/api/v2/sessions/{sessionId}/work-items/{workItemId}");
                var body = await hidden.Content.ReadAsStringAsync();
                Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
                Assert.DoesNotContain("prepared", body, StringComparison.OrdinalIgnoreCase);
            }

            await using var reopened = new DurableSqliteHostFactory(db, runScheduler: false);
            var again = OwnerClient(reopened);
            var waiting = await again.GetFromJsonAsync<WorkItemResponse>(
                $"/api/v2/sessions/{sessionId}/work-items/{workItemId}");
            Assert.Equal("needsApproval", waiting!.Status);
            Assert.Equal(actionHash, waiting.ActionHash);
            var approved = await again.PostAsJsonAsync(
                $"/api/v2/sessions/{sessionId}/work-items/{workItemId}/approvals/{approvalId}/approve",
                new DecideWorkApprovalRequest(revision, approvalRevision, actionHash));
            approved.EnsureSuccessStatusCode();
            var resumedAt = reopened.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            Assert.Equal(1, await reopened.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(resumedAt, 10));
            Assert.Equal(0, await reopened.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(resumedAt, 10));
            var result = await again.GetFromJsonAsync<WorkItemResultResponse>(
                $"/api/v2/sessions/{sessionId}/work-items/{workItemId}/result");
            Assert.Equal("Sensitive action completed after approval.", result!.Text);
            var finished = await reopened.Services.GetRequiredService<IWorkItemStore>().GetAsync(
                await OwnerAsync(reopened.Services, Guid.Parse(sessionId)),
                Guid.Parse(workItemId));
            Assert.Equal(WorkItemStatus.Completed, finished!.Status);
            Assert.Equal(WorkSideEffectDisposition.Succeeded, finished.SideEffect.Disposition);
        }
        finally
        {
            DeleteDb(db);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancelled_work_stays_cancelled_and_a_stale_worker_cannot_complete_it()
    {
        var db = TempDb();
        try
        {
            string sessionId;
            Guid cancelledId;
            Guid staleId;
            Guid staleGeneration;
            long staleRevision;
            await using (var host = new DurableSqliteHostFactory(db, runScheduler: false))
            {
                var client = OwnerClient(host);
                var session = await CreateAsync(client, "general-assistant", null);
                sessionId = session.SessionId;
                var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                var owner = await OwnerAsync(host.Services, Guid.Parse(sessionId));
                var store = host.Services.GetRequiredService<IWorkItemStore>();
                var queued = await SeedAsync(store, owner, Guid.Parse(sessionId), now);
                var cancel = await client.PostAsJsonAsync(
                    $"/api/v2/sessions/{sessionId}/work-items/{queued.WorkItemId}/cancel",
                    new CancelWorkItemRequest(queued.Revision));
                cancel.EnsureSuccessStatusCode();
                cancelledId = queued.WorkItemId;

                var running = await SeedAsync(store, owner, Guid.Parse(sessionId), now.AddMinutes(-5));
                staleGeneration = Guid.NewGuid();
                var claimedAt = now.AddMinutes(-2);
                var claimed = await store.TryClaimAsync(
                    running.WorkItemId,
                    staleGeneration,
                    claimedAt,
                    claimedAt.AddMinutes(1));
                Assert.NotNull(claimed);
                var cancelling = await store.RequestCancellationAsync(
                    owner,
                    running.WorkItemId,
                    claimed!.Revision,
                    null,
                    now);
                Assert.Equal(WorkItemStatus.Running, cancelling.Status);
                Assert.True(cancelling.CancellationRequested);
                staleRevision = cancelling.Revision;
                staleId = running.WorkItemId;
                await host.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(now, 10);
                var recovered = await store.GetAsync(owner, staleId);
                Assert.Equal(WorkItemStatus.Cancelled, recovered!.Status);
                var stale = await Assert.ThrowsAsync<AgentCoreException>(() => store.CompleteAsync(
                    staleId,
                    staleRevision,
                    staleGeneration,
                    "late result",
                    now).AsTask());
                Assert.Equal("Conflict", stale.Code);
            }

            await using var reopened = new DurableSqliteHostFactory(db, runScheduler: false);
            var again = OwnerClient(reopened);
            var listed = await again.GetFromJsonAsync<WorkItemListResponse>($"/api/v2/sessions/{sessionId}/work-items");
            Assert.Equal(2, listed!.Items.Count);
            Assert.All(listed.Items, item => Assert.Equal("cancelled", item.Status));
            var ownerAgain = await OwnerAsync(reopened.Services, Guid.Parse(sessionId));
            var storeAgain = reopened.Services.GetRequiredService<IWorkItemStore>();
            var stillCancelled = await storeAgain.GetAsync(ownerAgain, cancelledId);
            Assert.Equal(WorkItemStatus.Cancelled, stillCancelled!.Status);
            var nowAgain = reopened.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            Assert.Equal(0, await reopened.Services.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(nowAgain, 10));
            var stillStale = await Assert.ThrowsAsync<AgentCoreException>(() => storeAgain.CompleteAsync(
                staleId,
                staleRevision,
                staleGeneration,
                "late result",
                nowAgain).AsTask());
            Assert.Equal("Conflict", stillStale.Code);
        }
        finally
        {
            DeleteDb(db);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Host_pass_completes_a_due_reminder()
    {
        var db = TempDb();
        try
        {
            await using var host = new DurableSqliteHostFactory(db);
            var hosted = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<TriggerSchedulerHostedService>()
                .Single();
            Assert.NotNull(hosted);
            var client = OwnerClient(host);
            var session = await CreateAsync(client, "general-assistant", null);
            var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var due = now.AddMinutes(-1);
            var owner = await OwnerAsync(host.Services, Guid.Parse(session.SessionId));
            await host.Services.GetRequiredService<ITriggerRegistrationService>().CreateAsync(
                new TriggerRegistrationDraft(
                    new TriggerOwner(owner.AgentInstanceId, owner.ProfileId),
                    "Call John",
                    new OneShotSchedule(due, "UTC"),
                    due,
                    null,
                    TriggerAuthorizationOrigin.CurrentUserTurn,
                    Guid.Parse(session.SessionId),
                    null));

            WorkItemResponse? completed = null;
            for (var attempt = 0; attempt < 40 && completed is null; attempt++)
            {
                var listed = await client.GetFromJsonAsync<WorkItemListResponse>(
                    $"/api/v2/sessions/{session.SessionId}/work-items");
                completed = listed!.Items.FirstOrDefault(item => item.Status == "completed");
                if (completed is null)
                {
                    await Task.Delay(250);
                }
            }

            Assert.NotNull(completed);
            var result = await client.GetFromJsonAsync<WorkItemResultResponse>(
                $"/api/v2/sessions/{session.SessionId}/work-items/{completed!.WorkItemId}/result");
            Assert.Equal("Hello from synthetic.", result!.Text);
        }
        finally
        {
            DeleteDb(db);
        }
    }

    private static HttpClient OwnerClient(DurableSqliteHostFactory host)
    {
        var client = host.CreateClient();
        TestOwnerCapability.Apply(client, host.Services);
        return client;
    }

    private static async Task<SessionViewResponse> CreateAsync(HttpClient client, string agentId, int? version)
    {
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(agentId, version, "text"));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
    }

    private static async Task<WorkOwner> OwnerAsync(IServiceProvider services, Guid sessionId)
    {
        var snapshot = await services.GetRequiredService<SessionManager>().GetAsync(sessionId);
        return new WorkOwner(snapshot.AgentInstanceId!.Value, snapshot.ProfileId!.Value);
    }

    private static async Task AdmitApplicationEventAsync(
        ITriggerStore store,
        TriggerOwner owner,
        DateTimeOffset now,
        string evidence)
    {
        var occurrence = new TriggerOccurrence(
            Guid.NewGuid(),
            $"event:{Guid.NewGuid():N}",
            null,
            owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            now,
            now,
            evidence,
            Guid.NewGuid(),
            null,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null);
        await store.AdmitOccurrenceAsync(occurrence);
        var claim = Guid.NewGuid();
        Assert.NotNull(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, now.AddMinutes(1), now));
        Assert.NotNull(await store.MarkAwaitingDurableWorkAsync(occurrence.OccurrenceId, claim, "No compatible runtime", now));
    }

    private static async Task<WorkItem> SeedAsync(IWorkItemStore store, WorkOwner owner, Guid sessionId, DateTimeOffset createdAt)
    {
        var item = WorkItem.Create(
            Guid.NewGuid(),
            owner,
            new WorkProvenance(
                Guid.NewGuid(),
                WorkSourceKind.Schedule,
                null,
                sessionId,
                null,
                $"work-{Guid.NewGuid():N}",
                createdAt,
                createdAt,
                "{}",
                "general-assistant",
                10,
                "Riley"),
            new WorkModelPin("scripted-alpha", "primary-llm", "scripted-alpha", "medium"),
            WorkLimits.DefaultMaxAttempts,
            createdAt);
        var created = await store.CreateAsync(item);
        return created.Item;
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"agent-core-journey-{Guid.NewGuid():N}.db");

    private static void DeleteDb(string db)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        foreach (var path in new[] { db, db + "-wal", db + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The host may still be releasing the file.
            }
        }
    }
}
