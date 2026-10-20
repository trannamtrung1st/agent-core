using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Api.Http;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class CredentialModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Func<Task>? AfterAccountRead;
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (request.Messages.Any(m => m.Role == ModelRole.Tool && m.Text.Contains("tenantAccountRead", StringComparison.Ordinal))
                && Interlocked.Exchange(ref AfterAccountRead, null) is { } after) await after();
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    private sealed class HostedCredentialVerifier(bool uncertain, bool hold = false) : ITenantCredentialOAuthVerifier
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CredentialOAuthDescriptor Describe(CredentialPurpose purpose) => new("agent-core.tenant.synthetic", "http://localhost/callback", true, true);
        public async ValueTask<CredentialOAuthCheck> CheckAsync(Credential current, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(CredentialPurpose.GoogleDriveAccount, current.Purpose); Assert.True(current.DriveAccount!.Synthetic);
            Entered.TrySetResult(); if (hold) await Release.Task.WaitAsync(ct);
            if (uncertain) throw new IOException("private-provider-body");
            return new CredentialOAuthCheck();
        }
    }
    private static async Task<AgentRun> AwaitCredentialRun(ITenantRunStore runs, Guid tenant, Guid id, bool waiting)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!;
            if (run.Result?.ContinuationRunId is { } next) { id = next; continue; }
            if (waiting ? run.Status == AgentRunStatus.WaitingForApproval || run.IsTerminal : run.IsTerminal) return run;
            await Task.Delay(25, deadline.Token);
        }
    }

    [Theory]
    [InlineData(false, "approve", false)] [InlineData(true, "approve", false)]
    [InlineData(false, "approve", true)] [InlineData(true, "approve", true)]
    [InlineData(false, "reject", false)] [InlineData(true, "reject", false)]
    [InlineData(false, "cancel", false)] [InlineData(true, "cancel", false)]
    [InlineData(false, "uncertain", false)] [InlineData(true, "uncertain", false)]
    [InlineData(false, "read-revoked", false)] [InlineData(true, "read-revoked", false)]
    [InlineData(true, "restart", false)]
    [InlineData(false, "checkpoint-mismatch", false)] [InlineData(true, "checkpoint-mismatch", false)]
    [InlineData(false, "budget-exhausted", false)] [InlineData(true, "budget-exhausted", false)]
    [InlineData(false, "cancel-inflight", false)] [InlineData(true, "cancel-inflight", false)]
    public async Task Model_credential_check_waits_for_exact_human_consent_and_consumes_one_successor_Run_receipt(bool sqlite, string choice, bool use)
    {
        var model = new CredentialModel(); var verifier = new HostedCredentialVerifier(choice == "uncertain", choice == "cancel-inflight");
        await using var factory = new OAuthFactory(sqlite: sqlite, preserve: choice == "restart", checkOverride: verifier, execute: true, model: model); using var owner = Client(factory);
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentCore.Api.Realtime.AgentCoreOptions>>().Value.MaxActiveSessions = 1;
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var credential = (await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Model Drive", "Token", "GoogleDriveAccount", await Revision(owner, tenant)))))
            .GetProperty("credential").GetProperty("credentialId").GetGuid();
        var start = await owner.PostAsJsonAsync(api + $"/credentials/{credential}/oauth/start", new TenantCredentialOAuthRequest(await Revision(owner, tenant), 1));
        var url = (await Json(start)).GetProperty("authorizationUrl").GetString()!;
        using var consent = Client(factory); consent.DefaultRequestHeaders.Add("Cookie", Assert.Single(start.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.SeeOther, (await consent.PostAsync("/api/v2/integrations/tenant-google/oauth/synthetic",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = Uri.UnescapeDataString(url.Split("?state=")[1]), ["decision"] = "authorize" }))).StatusCode);
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) })))
            .GetProperty("instance").GetProperty("instanceId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/credential-bindings",
            new TenantBindCredentialRequest(credential, "work-drive", await Revision(owner, tenant), 1, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, overrides = new { mode = new { value = "Disabled" } } })).StatusCode);
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(principal, tenant, null, "Model credential", use ? ["credentials.read", "credentials.use"] : ["credentials.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(principal, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "credential", credential.ToString("D")), await Revision(owner, tenant));
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision })))
            .GetProperty("sessionId").GetGuid();
        var sourceId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "synthetic credential check work-drive", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })))
            .GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitCredentialRun(runs, tenant, sourceId, true);
        Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
        Volatile.Write(ref factory.TenantExecutionEnabled, false);
        // A durable wait precedes supervisor cleanup. Prove the sole Runtime slot was released
        // before manually dispatching a successor in the negative-path cases below.
        var capacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.SessionHost>();
        using (var drained = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while (!capacity.TryReserveTenantRuntime()) await Task.Delay(25, drained.Token);
            capacity.ReleaseTenantRuntime();
        }
        Assert.Equal(0, verifier.Calls); Assert.Null(waiting.Claim);
        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
        Assert.Equal(TenantOperationKind.CredentialConnectionCheck, request.Operation.Kind); Assert.Equal(use ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        Assert.Equal(credential, request.Operation.CredentialCheck!.CredentialId);
        var initial = model.Requests.First(); var schema = Assert.Single(initial.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
        Assert.Contains("check_credential_connection", schema); Assert.DoesNotContain("update_persona", schema); Assert.DoesNotContain("update_preferences", schema);
        if (choice == "restart") await factory.DisposeAsync();
        await using var restarted = choice == "restart" ? new OAuthFactory(factory.Root, sqlite: true, checkOverride: verifier, execute: true, model: model) : null;
        using var restartedOwner = restarted is not null ? Client(restarted) : null;
        if (restartedOwner is not null) await Login(restartedOwner, "owner@example.test", "explicit-synthetic-test-password");
        var executionOwner = restartedOwner ?? owner; var executionFactory = restarted ?? factory;
        Volatile.Write(ref executionFactory.TenantExecutionEnabled, false);
        runs = executionFactory.Services.GetRequiredService<ITenantRunStore>();
        var route = api + $"/approvals/{request.RequestId}";
        var detail = await Json(await executionOwner.GetAsync(route)); Safe(detail);
        Assert.Equal(HttpStatusCode.OK, (await executionOwner.PostAsJsonAsync(route + (choice == "cancel" ? "/cancel" : "/decision"), new {
            decision = choice == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
        if (choice == "read-revoked") await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["agents.read"], await Revision(owner, tenant));
        if (choice is "checkpoint-mismatch" or "budget-exhausted")
        {
            await executionFactory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            var source = (await runs.FindTenantRunAsync(tenant, sourceId))!;
            var queued = (await runs.FindTenantRunAsync(tenant, source.Result!.ContinuationRunId!.Value))!;
            var time = executionFactory.Services.GetRequiredService<TimeProvider>(); var ids = executionFactory.Services.GetRequiredService<IIdGenerator>();
            var storage = executionFactory.Services.GetRequiredService<ITenantRuntimeStore>();
            var claimed = await storage.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
                new AgentRunCommand.Claim(queued.Revision, time.GetUtcNow(), ids.NewId(), time.GetUtcNow().AddMinutes(5)), ids.NewId());
            var checkpoint = claimed.Checkpoint!;
            claimed = await storage.ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId,
                new AgentRunCommand.Checkpoint(claimed.Revision, time.GetUtcNow(), claimed.Claim!.Generation,
                    new(choice == "checkpoint-mismatch" ? checkpoint.PayloadJson.Replace("work-drive", "other-drive") : checkpoint.PayloadJson,
                        checkpoint.StepCount, checkpoint.OutputBytes, choice == "budget-exhausted" ? 0 : checkpoint.RemainingOverallBudgetMs,
                        checkpoint.ActiveExecutionMs), null), ids.NewId());
            Assert.True(await executionFactory.Services.GetRequiredService<ITenantRunDispatcher>().DispatchAsync(claimed));
            var failed = (await runs.FindTenantRunAsync(tenant, claimed.AgentRunId))!;
            Assert.Equal(AgentRunStatus.Failed, failed.Status); Assert.Equal(choice == "budget-exhausted" ? "execution-budget-exhausted" : "approval-effect-unavailable", failed.Failure!.Code);
            Assert.Equal(0, verifier.Calls); Assert.Single(model.Requests);
            Assert.Null(await executionFactory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
            Assert.Equal(OperationGrantStatus.Available, (await executionFactory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId))!.Status);
            return;
        }
        Volatile.Write(ref executionFactory.TenantExecutionEnabled, true);
        if (choice == "cancel-inflight")
        {
            await verifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var source = (await runs.FindTenantRunAsync(tenant, sourceId))!;
            var active = (await runs.FindTenantRunAsync(tenant, source.Result!.ContinuationRunId!.Value))!;
            var approved = executionFactory.Services.GetRequiredService<ITenantApprovalStore>();
            Assert.Equal(OperationGrantStatus.Claimed, (await approved.FindOperationGrantAsync(tenant, request.RequestId))!.Status);
            Assert.Equal(HttpStatusCode.OK, (await executionOwner.PostAsJsonAsync(api + $"/agent-runs/{active.AgentRunId}/cancel", new {
                expectedRevision = await Revision(executionOwner, tenant), expectedRunRevision = active.Revision })).StatusCode);
            TenantOperationReceipt? settled;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var receipts = executionFactory.Services.GetRequiredService<ITenantApprovedOperationStore>();
            while ((settled = await receipts.FindOperationReceiptAsync(tenant, request.Operation.OperationId, deadline.Token)) is null) await Task.Delay(25, deadline.Token);
            Assert.Equal(TenantOperationOutcome.Indeterminate, settled.Outcome); Assert.Equal(active.AgentRunId, settled.RunId);
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(tenant, active.AgentRunId))!.Status);
            Assert.Equal(OperationGrantStatus.Indeterminate, (await approved.FindOperationGrantAsync(tenant, request.RequestId))!.Status);
            Assert.Equal(settled, await executionFactory.Services.GetRequiredService<TenantCredentialCheckService>().ExecuteAsync(new(tenant, active.AgentRunId),
                request.RequestId, request.Operation.OperationId, active.Claim!.Generation, request.Operation.ActionHash));
            Assert.Equal(1, verifier.Calls); Assert.Single(model.Requests);
            Safe(await Json(await executionOwner.GetAsync(route))); Assert.Equal(0, executionFactory.Outbound);
            return;
        }
        var finished = await AwaitCredentialRun(runs, tenant, sourceId, false);
        Assert.NotEqual(sourceId, finished.AgentRunId); Assert.Equal(ActivationKind.ApprovalContinuation, finished.Admission.Activation.Kind);
        Assert.Equal(waiting.ResponseId, finished.ResponseId); Assert.Equal(waiting.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, finished.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.True(finished.Status == (choice == "uncertain" ? AgentRunStatus.Failed : AgentRunStatus.Completed), $"{finished.Status}: {finished.Failure?.Code}: {finished.Failure?.Summary}");
        Assert.Equal(choice is "approve" or "restart" or "uncertain" ? 1 : 0, verifier.Calls);
        var receipt = await executionFactory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
        if (choice is "reject" or "cancel") Assert.Null(receipt);
        else { Assert.NotNull(receipt); Assert.Equal(finished.AgentRunId, receipt.RunId); Assert.Equal(choice == "uncertain" ? TenantOperationOutcome.Indeterminate : choice == "read-revoked" ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, receipt.Outcome); }
        Assert.True(AgentRunToolCallCheckpoint.TryRead(finished.Checkpoint, out var transcript)); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(transcript!));
        var result = Assert.Single(transcript!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        using var outcome = JsonDocument.Parse(result.Text);
        Assert.Equal(choice == "uncertain" ? JsonValueKind.Null : choice is "approve" or "restart" ? JsonValueKind.True : JsonValueKind.False, outcome.RootElement.GetProperty("applied").ValueKind);
        Assert.Contains(model.Requests.Skip(1), r => r.Context!.Sections.Any(s => s.Id == ContextSectionId.ApprovalOutcome));
        var expectedText = choice == "uncertain" ? "outcome is uncertain" : choice is "approve" or "restart" ? "check completed" : "was not applied";
        if (finished.Result is { } recorded) Assert.Contains(expectedText, recorded.Text);
        if (choice == "uncertain") { Assert.Equal("side-effect-indeterminate", finished.Failure!.Code); Assert.All(model.Requests.Skip(1), r => Assert.All(r.Tools ?? [], t => Assert.Equal("agent_core_respond", t.Name))); }
        Volatile.Write(ref executionFactory.TenantExecutionEnabled, false);
        var snapshot = (await executionFactory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
        var followup = (await Json(await executionOwner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new {
            messageId = Guid.NewGuid(), text = "Tell me about the recorded result", expectedRevision = await Revision(executionOwner, tenant), expectedSessionRevision = snapshot.Revision })))
            .GetProperty("runId").GetGuid();
        var next = (await runs.FindTenantRunAsync(tenant, followup))!;
        var runtime = executionFactory.Services.GetRequiredService<ITenantRuntimeStore>(); var clock = executionFactory.Services.GetRequiredService<TimeProvider>();
        var identifiers = executionFactory.Services.GetRequiredService<IIdGenerator>();
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, clock.GetUtcNow(), identifiers.NewId(), clock.GetUtcNow().AddMinutes(5)), identifiers.NewId());
        Assert.NotEqual(finished.Admission.ExecutionContext.RootActivationId, next.Admission.ExecutionContext!.RootActivationId);
        // Private execution has no delivery acknowledgement. Inspect retained bytes through a current trusted frame;
        // do not manufacture received prefixes or claim that the next model may use an unseen reply.
        var retained = (await runtime.LoadTenantRuntimeAsync(new(next.Owner, session, next.AgentRunId, next.Claim!.Generation)))!;
        var answer = Assert.Single(retained.Entries, e => e.Role == ConversationRole.Assistant && e.ResponseId == finished.ResponseId);
        Assert.Equal(EntryStatus.Completed, answer.Status); Assert.Contains(expectedText, answer.Text);
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.RequestCancellation(next.Revision, clock.GetUtcNow(), null), identifiers.NewId());
        await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.CommitCancellation(next.Revision, clock.GetUtcNow(), next.Claim!.Generation, null), identifiers.NewId());
        Assert.Equal(choice is "approve" or "restart" or "uncertain" ? 1 : 0, verifier.Calls);
        Assert.Equal(0, executionFactory.Outbound); Assert.Equal(0, executionFactory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        Assert.DoesNotContain("private-provider", JsonSerializer.Serialize(model.Requests));
        Safe(await Json(await executionOwner.GetAsync(route)));
    }
}
