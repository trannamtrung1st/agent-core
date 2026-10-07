using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Work;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class WorkItemApiTests
{
    private const string Evidence = "{\"secret\":\"SECRET_EVIDENCE\"}";
    private const string Checkpoint = "{\"phase\":\"SECRET_CHECKPOINT\"}";
    private const string Prepared = "{\"body\":\"SECRET_BODY\"}";
    private const string ResultText = "Oven timer finished.";
    private const string Preview = "POST https://example.com/items";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(WorkSourceKind.Schedule, "Recorded task", "Recorded task")]
    [InlineData(WorkSourceKind.ManualInvocation, "Recorded automation", "Recorded automation")]
    [InlineData(WorkSourceKind.ApplicationEvent, "Review the configured order", "Review the configured order")]
    [InlineData(WorkSourceKind.Schedule, "overlong", null)]
    [InlineData(WorkSourceKind.ManualInvocation, "malformed", null)]
    [InlineData(WorkSourceKind.ManualInvocation, "nonstring", null)]
    public async Task Intent_projection_exposes_only_bounded_authored_tasks_and_never_occurrence_evidence(
        WorkSourceKind kind, string value, string? expected)
    {
        var db = Path.Combine(Path.GetTempPath(), $"work-intent-{Guid.NewGuid():N}.db");
        try
        {
            await using var host = new DurableSqliteHostFactory(db, runScheduler: false);
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var session = await CreateAsync(client, "examiner", 1);
            var sessionId = Guid.Parse(session.SessionId);
            var evidence = value switch
            {
                "malformed" => "{",
                "nonstring" => "{\"instructions\": {\"secret\": \"SECRET_EVIDENCE\"}}",
                _ => JsonSerializer.Serialize(new { instructions = value == "overlong" ? new string('x', 2001) : value, secret = "SECRET_EVIDENCE" })
            };
            var item = await SeedAsync(host.Services.GetRequiredService<IWorkItemStore>(),
                await OwnerAsync(host.Services, sessionId), sessionId, host.Services.GetRequiredService<TimeProvider>().GetUtcNow(), kind, evidence);
            var response = await client.GetAsync($"/api/v2/sessions/{sessionId}/work-items/{item.WorkItemId}");
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<WorkItemResponse>())!;
            Assert.Equal(expected, result.Instructions);
            Assert.DoesNotContain("SECRET_EVIDENCE", await response.Content.ReadAsStringAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
    }

    [Fact]
    public async Task Owner_can_list_inspect_cancel_decide_and_read_result_without_private_payloads()
    {
        // Seeded queued WorkItems must stay queued through stale-revision cancel assertions;
        // disable DurableWorkHostedService so the scheduler cannot claim and complete them first.
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-work-api-{Guid.NewGuid():N}.db");
        try
        {
            await using var host = new DurableSqliteHostFactory(db, runScheduler: false);
            var client = TestOwnerCapability.CreateOwnerClient(host);
            var anonymous = host.CreateClient();
            var session = await CreateAsync(client, "examiner", 1);
            var other = await CreateAsync(client, "general-assistant", null);
            var sessionId = Guid.Parse(session.SessionId);
            var paused = await client.PostAsJsonAsync(
                $"/api/v2/sessions/{session.SessionId}/lifecycle",
                new TransitionLifecycleRequest("paused"));
            paused.EnsureSuccessStatusCode();

            var now = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var store = host.Services.GetRequiredService<IWorkItemStore>();
            var owner = await OwnerAsync(host.Services, sessionId);
        var completed = await CompleteAsync(store, owner, sessionId, now.AddMinutes(-1), ResultText);
        var queued = await SeedAsync(store, owner, sessionId, now.AddMinutes(-2));
        var approval = await WaitForApprovalAsync(store, owner, sessionId, now.AddMinutes(-3));
        var rejection = await WaitForApprovalAsync(store, owner, sessionId, now.AddMinutes(-4));
        var failed = await FailAsync(store, owner, sessionId, now.AddMinutes(-5));
        var foreign = await SeedAsync(
            store,
            new WorkOwner(owner.AgentInstanceId, Guid.NewGuid()),
            sessionId,
            now);

        var unauthorized = await anonymous.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var listed = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items");
        listed.EnsureSuccessStatusCode();
        var listBody = await listed.Content.ReadAsStringAsync();
        AssertPrivatePayloadsHidden(listBody);
        Assert.DoesNotContain(ResultText, listBody, StringComparison.Ordinal);
        using var list = JsonDocument.Parse(listBody);
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        Assert.Equal(completed.WorkItemId.ToString(), items[0].GetProperty("workItemId").GetString());
        Assert.Equal("completed", items[0].GetProperty("status").GetString());
        Assert.Equal("Automation · Schedule", items[0].GetProperty("origin").GetString());
        Assert.Equal("Checking the oven", items[0].GetProperty("progress").GetString());
        Assert.DoesNotContain(items, item => item.GetProperty("workItemId").GetString() == foreign.WorkItemId.ToString());

        var newest = await client.GetFromJsonAsync<WorkItemListResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items?limit=1");
        Assert.Equal(completed.WorkItemId.ToString(), newest!.Items.Single().WorkItemId);
        var nextPage = await client.GetFromJsonAsync<WorkItemListResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items?limit=2&before={completed.WorkItemId}");
        Assert.Equal(new[] { queued.WorkItemId.ToString(), approval.WorkItemId.ToString() }, nextPage!.Items.Select(item => item.WorkItemId));
        var foreignCursor = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items?before={foreign.WorkItemId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignCursor.StatusCode);


        var hiddenList = await client.GetFromJsonAsync<WorkItemListResponse>(
            $"/api/v2/sessions/{other.SessionId}/work-items");
        Assert.Empty(hiddenList!.Items);
        var crossDetail = await client.GetAsync($"/api/v2/sessions/{other.SessionId}/work-items/{completed.WorkItemId}");
        Assert.Equal(HttpStatusCode.NotFound, crossDetail.StatusCode);
        var crossCancel = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{other.SessionId}/work-items/{queued.WorkItemId}/cancel",
            new CancelWorkItemRequest(queued.Revision));
        Assert.Equal(HttpStatusCode.NotFound, crossCancel.StatusCode);
        var foreignDetail = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items/{foreign.WorkItemId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);

        var detail = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items/{completed.WorkItemId}");
        detail.EnsureSuccessStatusCode();
        var detailBody = await detail.Content.ReadAsStringAsync();
        AssertPrivatePayloadsHidden(detailBody);
        Assert.DoesNotContain(ResultText, detailBody, StringComparison.Ordinal);
        using var detailJson = JsonDocument.Parse(detailBody);
        Assert.Equal("completed", detailJson.RootElement.GetProperty("status").GetString());
        Assert.False(detailJson.RootElement.GetProperty("cancellationAvailable").GetBoolean());

        var missingResult = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items/{queued.WorkItemId}/result");
        Assert.Equal(HttpStatusCode.NotFound, missingResult.StatusCode);
        var result = await client.GetAsync($"/api/v2/sessions/{session.SessionId}/work-items/{completed.WorkItemId}/result");
        result.EnsureSuccessStatusCode();
        var resultBody = await result.Content.ReadAsStringAsync();
        AssertPrivatePayloadsHidden(resultBody);
        Assert.Contains(ResultText, resultBody, StringComparison.Ordinal);
        var history = await client.GetAsync($"/api/v1/sessions/{session.SessionId}/messages");
        history.EnsureSuccessStatusCode();
        Assert.DoesNotContain(ResultText, await history.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var failedDetail = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items/{failed.WorkItemId}");
        Assert.Equal("failed", failedDetail!.Status);
        Assert.Equal("model-failed", failedDetail.FailureCode);
        Assert.Equal("The model failed.", failedDetail.FailureSummary);

        var waiting = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items/{approval.WorkItemId}");
        Assert.Equal("needsApproval", waiting!.Status);
        Assert.Equal(Preview, waiting.ApprovalPreview);
        Assert.Equal(Hash, waiting.ActionHash);
        Assert.NotNull(waiting.ApprovalId);
        var decision = new DecideWorkApprovalRequest(waiting.Revision, waiting.ApprovalRevision!.Value, waiting.ActionHash!);
        var approved = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{approval.WorkItemId}/approvals/{waiting.ApprovalId}/approve",
            decision);
        approved.EnsureSuccessStatusCode();
        var approvedBody = await approved.Content.ReadAsStringAsync();
        AssertPrivatePayloadsHidden(approvedBody);
        var approvedItem = await approved.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("queued", approvedItem!.Status);
        Assert.False(approvedItem.NeedsApproval);
        Assert.Null(approvedItem.ApprovalId);
        var repeated = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{approval.WorkItemId}/approvals/{waiting.ApprovalId}/approve",
            decision);
        repeated.EnsureSuccessStatusCode();
        var repeatedItem = await repeated.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal(approvedItem.Revision, repeatedItem!.Revision);

        var rejectView = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items/{rejection.WorkItemId}");
        var altered = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{rejection.WorkItemId}/approvals/{rejectView!.ApprovalId}/reject",
            new DecideWorkApprovalRequest(rejectView.Revision, rejectView.ApprovalRevision!.Value, new string('b', 64)));
        Assert.Equal(HttpStatusCode.Conflict, altered.StatusCode);
        Assert.DoesNotContain("diagnosticId", await altered.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var staleDecision = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{rejection.WorkItemId}/approvals/{rejectView!.ApprovalId}/reject",
            new DecideWorkApprovalRequest(rejectView.Revision + 5, rejectView.ApprovalRevision!.Value, rejectView.ActionHash!));
        Assert.Equal(HttpStatusCode.Conflict, staleDecision.StatusCode);
        var rejected = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{rejection.WorkItemId}/approvals/{rejectView.ApprovalId}/reject",
            new DecideWorkApprovalRequest(rejectView.Revision, rejectView.ApprovalRevision!.Value, rejectView.ActionHash!));
        rejected.EnsureSuccessStatusCode();
        Assert.Equal("queued", (await rejected.Content.ReadFromJsonAsync<WorkItemResponse>())!.Status);

        var queuedView = await client.GetFromJsonAsync<WorkItemResponse>(
            $"/api/v2/sessions/{session.SessionId}/work-items/{queued.WorkItemId}");
        var staleCancel = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{queued.WorkItemId}/cancel",
            new CancelWorkItemRequest(queuedView!.Revision + 5));
        Assert.Equal(HttpStatusCode.Conflict, staleCancel.StatusCode);
        var cancelled = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{queued.WorkItemId}/cancel",
            new CancelWorkItemRequest(queuedView.Revision));
        cancelled.EnsureSuccessStatusCode();
        var cancelledItem = await cancelled.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.Equal("cancelled", cancelledItem!.Status);
        var cancelAgain = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{session.SessionId}/work-items/{queued.WorkItemId}/cancel",
            new CancelWorkItemRequest(queuedView.Revision));
        cancelAgain.EnsureSuccessStatusCode();
        Assert.Equal(cancelledItem.Revision, (await cancelAgain.Content.ReadFromJsonAsync<WorkItemResponse>())!.Revision);
            var terminalCancel = await client.PostAsJsonAsync(
                $"/api/v2/sessions/{session.SessionId}/work-items/{completed.WorkItemId}/cancel",
                new CancelWorkItemRequest(completed.Revision));
            Assert.Equal(HttpStatusCode.BadRequest, terminalCancel.StatusCode);
        }
        finally
        {
            using var primary = new SqliteConnection($"Data Source={db}");
            SqliteConnection.ClearPool(primary);
            File.Delete(db);
        }
    }

    [Fact]
    public async Task Work_result_and_pending_approval_survive_sqlite_restart()
    {
        // This test drives WorkItem transitions directly on IWorkItemStore; disable the background
        // scheduler so DurableWorkHostedService cannot recover expired claims and race revisions.
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-work-{Guid.NewGuid():N}.db");
        try
        {
            string sessionId;
            string completedId;
            string approvalId;
            await using (var first = new DurableSqliteHostFactory(db, runScheduler: false))
            {
                var client = TestOwnerCapability.CreateOwnerClient(first);
                var session = await CreateAsync(client, "examiner", 1);
                sessionId = session.SessionId;
                var services = first.Services;
                var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                var store = services.GetRequiredService<IWorkItemStore>();
                var owner = await OwnerAsync(services, Guid.Parse(sessionId));
                var completed = await CompleteAsync(store, owner, Guid.Parse(sessionId), now.AddMinutes(-1), ResultText);
                var waiting = await WaitForApprovalAsync(store, owner, Guid.Parse(sessionId), now.AddMinutes(-2));
                completedId = completed.WorkItemId.ToString();
                approvalId = waiting.WorkItemId.ToString();
                var before = await client.GetAsync($"/api/v2/sessions/{sessionId}/work-items/{completedId}/result");
                before.EnsureSuccessStatusCode();
                Assert.Contains(ResultText, await before.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            SqliteConnection.ClearAllPools();
            await using var second = new DurableSqliteHostFactory(db, runScheduler: false);
            var reopened = TestOwnerCapability.CreateOwnerClient(second);
            var result = await reopened.GetAsync($"/api/v2/sessions/{sessionId}/work-items/{completedId}/result");
            result.EnsureSuccessStatusCode();
            var resultBody = await result.Content.ReadAsStringAsync();
            Assert.Contains(ResultText, resultBody, StringComparison.Ordinal);
            AssertPrivatePayloadsHidden(resultBody);
            var detail = await reopened.GetFromJsonAsync<WorkItemResponse>(
                $"/api/v2/sessions/{sessionId}/work-items/{approvalId}");
            Assert.Equal("needsApproval", detail!.Status);
            Assert.Equal(Preview, detail.ApprovalPreview);
            var approved = await reopened.PostAsJsonAsync(
                $"/api/v2/sessions/{sessionId}/work-items/{approvalId}/approvals/{detail.ApprovalId}/approve",
                new DecideWorkApprovalRequest(detail.Revision, detail.ApprovalRevision!.Value, detail.ActionHash!));
            approved.EnsureSuccessStatusCode();
            Assert.Equal("queued", (await approved.Content.ReadFromJsonAsync<WorkItemResponse>())!.Status);
            var history = await reopened.GetAsync($"/api/v1/sessions/{sessionId}/messages");
            history.EnsureSuccessStatusCode();
            Assert.DoesNotContain(ResultText, await history.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            using var primary = new SqliteConnection($"Data Source={db}");
            SqliteConnection.ClearPool(primary);
            File.Delete(db);
        }
    }

    private static void AssertPrivatePayloadsHidden(string body)
    {
        Assert.DoesNotContain("SECRET_EVIDENCE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_CHECKPOINT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_BODY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("preparedAction", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidenceJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkpoint", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<WorkOwner> OwnerAsync(IServiceProvider services, Guid sessionId)
    {
        var snapshot = await services.GetRequiredService<SessionManager>().GetAsync(sessionId);
        return new WorkOwner(snapshot.AgentInstanceId, snapshot.ProfileId!.Value);
    }

    private static async Task<SessionViewResponse> CreateAsync(HttpClient client, string agentId, int? version)
    {
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, agentId, version), "text"));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
    }

    private static async Task<WorkItem> SeedAsync(IWorkItemStore store, WorkOwner owner, Guid sessionId, DateTimeOffset createdAt, WorkSourceKind kind = WorkSourceKind.Schedule, string evidence = Evidence)
    {
        var item = WorkItem.Create(
            Guid.NewGuid(),
            owner,
            new WorkProvenance(
                Guid.NewGuid(),
                kind,
                automationId: null,
                sessionId,
                sourceEventId: null,
                $"work-{Guid.NewGuid():N}",
                createdAt,
                createdAt,
                evidence,
                "examiner",
                1,
                "Examiner"),
            new WorkModelPin("synthetic", "synthetic", "synthetic", "medium"),
            maxAttempts: 3,
            createdAt);
        return (await store.CreateAsync(item)).Item;
    }

    private static async Task<WorkItem> ClaimAsync(IWorkItemStore store, WorkItem item, DateTimeOffset now)
    {
        var claimed = await store.TryClaimAsync(item.WorkItemId, Guid.NewGuid(), now, now.AddMinutes(1));
        Assert.NotNull(claimed);
        return claimed!;
    }

    private static async Task<WorkItem> CompleteAsync(
        IWorkItemStore store,
        WorkOwner owner,
        Guid sessionId,
        DateTimeOffset createdAt,
        string result)
    {
        var seeded = await SeedAsync(store, owner, sessionId, createdAt);
        var claimed = await ClaimAsync(store, seeded, createdAt);
        var generation = claimed.Claim!.Generation;
        var checkpointed = await store.CheckpointAsync(
            claimed.WorkItemId,
            claimed.Revision,
            generation,
            new WorkCheckpoint(Checkpoint, 0, 0, 120_000),
            "Checking the oven",
            createdAt);
        return await store.CompleteAsync(checkpointed.WorkItemId, checkpointed.Revision, generation, result, createdAt);
    }

    private static async Task<WorkItem> WaitForApprovalAsync(
        IWorkItemStore store,
        WorkOwner owner,
        Guid sessionId,
        DateTimeOffset createdAt)
    {
        var seeded = await SeedAsync(store, owner, sessionId, createdAt);
        var claimed = await ClaimAsync(store, seeded, createdAt);
        return await store.BeginApprovalAsync(
            claimed.WorkItemId,
            claimed.Revision,
            claimed.Claim!.Generation,
            Guid.NewGuid(),
            "http.request",
            Prepared,
            Hash,
            Preview,
            createdAt.AddMinutes(10),
            createdAt);
    }

    private static async Task<WorkItem> FailAsync(IWorkItemStore store, WorkOwner owner, Guid sessionId, DateTimeOffset createdAt)
    {
        var seeded = await SeedAsync(store, owner, sessionId, createdAt);
        var claimed = await ClaimAsync(store, seeded, createdAt);
        return await store.FailAsync(
            claimed.WorkItemId,
            claimed.Revision,
            claimed.Claim!.Generation,
            "model-failed",
            "The model failed.",
            replaySafe: false,
            createdAt,
            nextRetryAtUtc: null);
    }
}
