using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static ModelToolCall BrowserModelCall(string url = "http://127.0.0.1:5091/browser-native.html") => new("exact-browser", ToolCatalog.RequestAuthorization,
        JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate, arguments = new { url } } }));
    private static TenantRunContinuationService BrowserContinuations(Fixture f, IBrowser browser, IIamStore? store = null)
    {
        store ??= f.Iam;
        var local = new TenantApprovedOperationService(store, (ITenantRunStore)store, (ITenantAgentStore)store, (ITenantApprovalStore)store,
            (ITenantApprovedOperationStore)store, Operations(f, store), f.RunConfiguration(), f.Ids, f.Clock, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default);
        return new((ITenantRunStore)store, (ITenantApprovalStore)store, local, f.Clock, (ITenantRuntimeStore)store, f.Ids,
            browserEffects: Effects(f, browser, store));
    }
    private static async Task<(Setup Setup, TenantApprovalRequest Request)> BrowserModelApproval(Fixture f, Setup s, IBrowser browser, bool password = false, Guid? upload = null, bool capture = false, bool downloads = false, bool drop = false)
    {
        var runtime = (ITenantRuntimeStore)f.Iam; var call = capture
            ? new ModelToolCall("exact-capture", ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.screenshot","arguments":{}}}""")
            : upload is { } file
            ? new ModelToolCall("exact-upload", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = FileTransferTool(drop), arguments = JsonSerializer.Deserialize<JsonElement>(FileTransferArguments(drop, file)) } }))
            : password
            ? new ModelToolCall("exact-password", ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.fill_credential","arguments":{"credentialRef":"account","target":{"by":"label","value":"Password"}}}}""")
            : BrowserModelCall();
        if (downloads)
        {
            var payload = System.Text.Json.Nodes.JsonNode.Parse(call.ArgumentsJson)!;
            payload["command"]!["downloadArtifacts"] = true;
            call = call with { ArgumentsJson = payload.ToJsonString() };
        }
        var run = await runtime.ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        s = s with { Run = run }; var approvals = Service(f, browser);
        var request = (await Request(approvals, s, capture ? ToolCatalog.BrowserScreenshot : upload is not null ? FileTransferTool(drop) : password ? ToolCatalog.BrowserFillCredential : ToolCatalog.BrowserNavigate, capture ? "{}" : upload is { } id ? FileTransferArguments(drop, id) : password ? PasswordArguments : Navigate, downloads)).Request!;
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Human);
        request = await approvals.DecideAsync(s.Tenant, request.RequestId, s.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await approvals.DispatchPendingAsync(); var source = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return (s with { Run = next }, request);
    }

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Browser_model_checkpoint_and_receipt_commit_together_and_reopen_never_reexecutes(bool sqlite, bool uncertain, bool password)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: password, credentialUseAllowed: !password); var probe = new EffectProbe(Probe(s)) { Uncertain = uncertain };
        (s, var request) = await BrowserModelApproval(f, s, probe, password); var service = BrowserContinuations(f, probe);
        await service.PrepareExternalEffectAsync(s.Run);
        // Before mailbox Resume, both the immutable fact and the exact completed transcript are already saved.
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(uncertain ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, receipt.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var messages)); Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
        var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        using var output = JsonDocument.Parse(text); Assert.False(output.RootElement.GetProperty("replayAllowed").GetBoolean());
        Assert.Equal(uncertain, output.RootElement.GetProperty("observationUnavailable").GetBoolean());
        if (!uncertain) Assert.Contains("owned observation", text);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        service = BrowserContinuations(f, probe, reopened); await service.PrepareExternalEffectAsync(saved);
        var resumed = await service.ResumeAsync(saved); Assert.Equal(receipt.Outcome, resumed.Outcome!.Outcome); Assert.True(saved.Checkpoint!.AwaitingRuntimeAdmission); Assert.Null(saved.Checkpoint.BudgetRecordedAtUtc);
        Assert.Equal(saved.Revision + 1, resumed.Run.Revision); Assert.False(resumed.Run.Checkpoint!.AwaitingRuntimeAdmission);
        Assert.Equal(saved.Checkpoint!.PayloadJson, resumed.Run.Checkpoint!.PayloadJson); Assert.Equal(1, probe.Invocations); Assert.Equal(1, probe.NativeEffects);
        var grant = await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(s.Tenant, request.RequestId);
        if (password) { Assert.NotNull(grant); Assert.Equal(uncertain ? OperationGrantStatus.Indeterminate : OperationGrantStatus.Consumed, grant.Status); }
        else Assert.Null(grant);
        // A spent exact grant permits delivery of its completed historical result, never another dispatch.
        await Effects(f, probe, reopened).RequireStoredObservationAsync(resumed.Run);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_model_output_requires_current_authority_at_settlement_and_later_delivery(bool sqlite, bool afterSettlement)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await BrowserModelApproval(f, s, probe);
        async Task Revoke() => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (!afterSettlement) probe.AfterEffect = Revoke;
        var service = BrowserContinuations(f, probe); await service.PrepareExternalEffectAsync(s.Run);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        if (afterSettlement)
        {
            await Revoke();
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.ResumeAsync(saved).AsTask());
            var authority = new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), Effects(f, probe));
            Assert.Null(await authority.CurrentDefinitionAsync(saved));
        }
        else
        {
            var resumed = await service.ResumeAsync(saved);
            Assert.DoesNotContain("owned observation", resumed.Run.Checkpoint!.PayloadJson);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages));
            using var output = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
            Assert.True(output.RootElement.GetProperty("observationUnavailable").GetBoolean());
        }
        Assert.Equal(TenantOperationOutcome.Completed, (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!.Outcome);
        Assert.Equal(1, probe.NativeEffects);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_model_mismatched_pending_call_cannot_dispatch_or_complete_another_command(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await BrowserModelApproval(f, s, probe); var wrong = BrowserModelCall("http://127.0.0.1:5091/other");
        var changed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(s.Run.Revision, f.Clock.GetUtcNow(), s.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [wrong])]), s.Run.Checkpoint!.StepCount, s.Run.Checkpoint.OutputBytes, s.Run.Checkpoint.RemainingOverallBudgetMs, s.Run.Checkpoint.ActiveExecutionMs, s.Run.Checkpoint.BudgetRecordedAtUtc), null), f.Ids.NewId());
        await Assert.ThrowsAsync<SecurityConflictException>(() => BrowserContinuations(f, probe).PrepareExternalEffectAsync(changed).AsTask());
        Assert.Equal(0, probe.Invocations); Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }

    [Fact]
    public void Tenant_Browser_model_projection_bounds_native_content_and_preserves_exact_facts_without_binary_data()
    {
        var result = new BrowserResult(null, new(new string('u', 100000), new string('t', 100000), new string('語', 100000), false, []),
            DataJson: "{\"body\":\"" + new string('x', 100000) + "\"}", Bytes: [1, 2, 3], Downloads: [new(null, "private.bin", "application/octet-stream", [4, 5])]);
        var observation = TenantBrowserContinuationProjection.Observation(result); Assert.True(Encoding.UTF8.GetByteCount(observation) <= 32768);
        Assert.DoesNotContain("private.bin", observation); Assert.DoesNotContain("bytes", observation, StringComparison.OrdinalIgnoreCase);
        using var payload = JsonDocument.Parse(observation); Assert.True(payload.RootElement.GetProperty("untrustedBrowserContent").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact]
    public void Tenant_Browser_model_contract_reuses_native_schema_and_rejects_ownership_or_unconverted_resources()
    {
        var schema = TenantBrowserAuthorizationContract.Schema([ToolCatalog.BrowserNavigate, ToolCatalog.BrowserFillCredential, "browser.drop"]).ToJsonString();
        Assert.Contains("browser.navigate", schema); Assert.Contains("browser.fill_credential", schema); Assert.Contains("artifactId", schema);
        Assert.NotNull(TenantApprovalToolContract.Parse(BrowserModelCall()).Browser);
        foreach (var bad in new[] {
            """{"operation":"run_browser_command","sessionId":"foreign","command":{"tool":"browser.close","arguments":{}}}""",
            """{"operation":"run_browser_command","command":{"tool":"browser.close","tenantId":"foreign","arguments":{}}}""",
            """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{"operationId":"foreign"}}}""",
            """{"operation":"run_browser_command","command":{"tool":"browser.navigate","arguments":{"url":"https://one.test","url":"https://two.test"}}}""",
            """{"operation":"run_browser_command","command":{"tool":"browser.fill_credential","arguments":{"target":{"by":"label","value":"Password"},"credentialRef":"private","value":"secret"}}}""",
            """{"operation":"run_browser_command","command":{"tool":"browser.drop","arguments":{"target":{"by":"label","value":"Drop"},"artifactId":"foreign"}}}""" })
        {
            var error = Record.Exception(() => TenantApprovalToolContract.Parse(new("invalid-browser", ToolCatalog.RequestAuthorization, bad)));
            Assert.True(error is ArgumentException or AccessDeniedException);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_model_atomic_checkpoint_charges_native_time_once(bool sqlite)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await BrowserModelApproval(f, s, probe);
        probe.AfterEffect = () => { clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; };
        var service = BrowserContinuations(f, probe); await service.PrepareExternalEffectAsync(s.Run); var resumed = await service.ResumeAsync(s.Run);
        Assert.Equal(8000, resumed.Run.Checkpoint!.RemainingOverallBudgetMs); Assert.Equal(2100, resumed.Run.Checkpoint.ActiveExecutionMs);
        await service.PrepareExternalEffectAsync(resumed.Run); var duplicate = await service.ResumeAsync(resumed.Run);
        Assert.Equal(resumed.Run.Checkpoint.PayloadJson, duplicate.Run.Checkpoint!.PayloadJson); Assert.Equal(resumed.Run.Revision, duplicate.Run.Revision);
        Assert.Equal(1, probe.NativeEffects);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Browser_sealed_result_waits_for_admission_without_budget_attempt_reset_or_replay(bool sqlite, bool uncertain, bool cancel)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s)) { Uncertain = uncertain };
        (s, var request) = await BrowserModelApproval(f, s, probe);
        var unsealed = s.Run.BeginApprovedExternalEffect(s.Run.Revision, s.Run.Claim!.Generation, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, clock.GetUtcNow());
        unsealed = unsealed.MarkSideEffect(unsealed.Revision, unsealed.Claim!.Generation, uncertain ? AgentRunSideEffectDisposition.Indeterminate : AgentRunSideEffectDisposition.Succeeded,
            "operation:" + request.Operation.OperationId.ToString("D"), request.Operation.ActionHash, clock.GetUtcNow());
        Assert.Throws<AgentRunTransitionException>(() => unsealed.DeferUnacceptedApprovalDispatch(unsealed.Revision, unsealed.Claim!.Generation, clock.GetUtcNow(), clock.GetUtcNow().AddSeconds(5)));
        probe.AfterEffect = () => { clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; };
        await BrowserContinuations(f, probe).PrepareExternalEffectAsync(s.Run);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.True(saved.Checkpoint!.AwaitingRuntimeAdmission); Assert.Null(saved.Checkpoint.BudgetRecordedAtUtc);
        Assert.Equal(8000, saved.Checkpoint.RemainingOverallBudgetMs); Assert.Equal(2100, saved.Checkpoint.ActiveExecutionMs);
        Assert.Throws<AgentRunTransitionException>(() => saved.AwaitApprovedExternalResultDelivery(saved.Revision, saved.Claim!.Generation,
            request.RequestId, Guid.NewGuid(), request.Operation.ActionHash, clock.GetUtcNow()));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: clock) : f.Iam;
        var runtime = (ITenantRuntimeStore)reopened;
        if (cancel) saved = await runtime.ApplyTenantRuntimeCommandAsync(saved.Owner, saved.AgentRunId,
            new AgentRunCommand.RequestCancellation(saved.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
        clock.Advance(TimeSpan.FromMinutes(6));
        var recovered = await runtime.ApplyTenantRuntimeCommandAsync(saved.Owner, saved.AgentRunId, new AgentRunCommand.Recover(saved.Revision, clock.GetUtcNow()), f.Ids.NewId());
        Assert.Equal(saved.AttemptCount, recovered.AttemptCount); Assert.Equal(saved.Checkpoint!.PayloadJson, recovered.Checkpoint!.PayloadJson);
        Assert.Equal(JsonSerializer.Serialize(saved.SideEffect), JsonSerializer.Serialize(recovered.SideEffect));
        if (cancel) { Assert.Equal(AgentRunStatus.Cancelled, recovered.Status); Assert.Equal(1, probe.NativeEffects); return; }
        Assert.Equal(AgentRunStatus.WaitingToRetry, recovered.Status);
        var claimed = await Claim(recovered);
        // Capacity refusal and another host loss may defer delivery, never authorize another effect or reset the original attempt.
        clock.Advance(TimeSpan.FromSeconds(30));
        var deferred = await runtime.ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId,
            new AgentRunCommand.DeferApprovalDispatch(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, clock.GetUtcNow().AddSeconds(5)), f.Ids.NewId());
        clock.Advance(TimeSpan.FromMinutes(6)); claimed = await Claim(deferred);
        clock.Advance(TimeSpan.FromMinutes(6));
        recovered = await runtime.ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId, new AgentRunCommand.Recover(claimed.Revision, clock.GetUtcNow()), f.Ids.NewId());
        claimed = await Claim(recovered);
        Assert.True(claimed.Checkpoint!.AwaitingRuntimeAdmission); Assert.Null(claimed.Checkpoint.BudgetRecordedAtUtc);
        Assert.Equal(saved.AttemptCount, claimed.AttemptCount); Assert.Equal(saved.Checkpoint.PayloadJson, claimed.Checkpoint.PayloadJson);
        var service = BrowserContinuations(f, probe, reopened); await service.PrepareExternalEffectAsync(claimed);
        var resumed = (await service.ResumeAsync(claimed)).Run;
        Assert.False(resumed.Checkpoint!.AwaitingRuntimeAdmission); Assert.Equal(clock.GetUtcNow(), resumed.Checkpoint.BudgetRecordedAtUtc);
        Assert.Equal(8000, resumed.Checkpoint.RemainingOverallBudgetMs); Assert.Equal(2100, resumed.Checkpoint.ActiveExecutionMs);
        clock.Advance(TimeSpan.FromSeconds(1)); var active = resumed.Checkpoint.PauseBudgetClock(clock.GetUtcNow());
        Assert.Equal(7000, active.RemainingOverallBudgetMs); Assert.Equal(3100, active.ActiveExecutionMs);
        Assert.Equal(1, probe.NativeEffects); Assert.Equal(1, probe.Invocations);
        async Task<AgentRun> Claim(AgentRun run) => await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
    }

    [Fact]
    public async Task Tenant_Browser_model_checkpoint_capacity_retains_exact_outcome_without_partial_observation()
    {
        await using var f = await Fixture.Create(false, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var request) = await BrowserModelApproval(f, s, probe); var call = BrowserModelCall();
        var padding = AgentRunLimits.MaxCheckpointBytes - AgentRunToolCallCheckpoint.CompletionReserve(TriggerKind.UserTurn) - 3000;
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.User, new string('p', padding)), new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000);
        var observation = JsonSerializer.Serialize(new { untrustedBrowserContent = true, content = new string('q', 16000) });
        var completed = TenantBrowserContinuationProjection.Complete(checkpoint, call, request, TenantOperationOutcome.Completed, TenantBrowserEffectRules.Completed, observation);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(completed, out var messages));
        using var result = JsonDocument.Parse(Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        Assert.Equal(request.Operation.OperationId, result.RootElement.GetProperty("operationId").GetGuid()); Assert.True(result.RootElement.GetProperty("commandCompleted").GetBoolean());
        Assert.Equal("result_budget_exhausted", result.RootElement.GetProperty("observationUnavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("browser").ValueKind);
        Assert.True(Encoding.UTF8.GetByteCount(completed.PayloadJson) + AgentRunToolCallCheckpoint.CompletionReserve(TriggerKind.UserTurn) <= AgentRunLimits.MaxCheckpointBytes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_model_prior_observation_keeps_its_delivery_scope_across_a_later_approval_hop(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var probe = new EffectProbe(Probe(s));
        (s, var first) = await BrowserModelApproval(f, s, probe); var service = BrowserContinuations(f, probe); await service.PrepareExternalEffectAsync(s.Run);
        var saved = (await service.ResumeAsync(s.Run)).Run; Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var messages));
        var secondCall = BrowserModelCall("http://127.0.0.1:5091/second");
        var nextCheckpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write(messages!.Append(new(ModelRole.Assistant, "", ToolCalls: [secondCall])).ToArray()),
            saved.Checkpoint!.StepCount + 1, saved.Checkpoint.OutputBytes, saved.Checkpoint.RemainingOverallBudgetMs, saved.Checkpoint.ActiveExecutionMs, saved.Checkpoint.BudgetRecordedAtUtc);
        saved = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(saved.Owner, saved.AgentRunId,
            new AgentRunCommand.Checkpoint(saved.Revision, f.Clock.GetUtcNow(), saved.Claim!.Generation, nextCheckpoint, null), f.Ids.NewId());
        var approvals = Service(f, probe); var second = (await approvals.PrepareBrowserAsync(new(s.Tenant, saved.AgentRunId), ToolCatalog.BrowserNavigate,
            """{"url":"http://127.0.0.1:5091/second"}""", saved.Revision, saved.Claim!.Generation)).Request!;
        Assert.NotEqual(first.RequestId, second.RequestId);
        var view = await approvals.ReadViewAsync(s.Tenant, second.RequestId, s.Human);
        await approvals.DecideAsync(s.Tenant, second.RequestId, s.Human, false, second.Operation.ActionHash, view.SecurityRevision, second.Revision);
        await approvals.DispatchPendingAsync();
        var source = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, saved.AgentRunId))!;
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, source.Result!.ContinuationRunId!.Value))!;
        next = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
            new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        Assert.Equal(second.RequestId, next.ApprovalRequestId);
        await Effects(f, probe).RequireStoredObservationAsync(next); // Original consent lineage remains valid while current scope remains.
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.Use.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ResumeAsync(next).AsTask());
        var authority = new TenantRunExecutionAuthority(Operations(f, f.Iam), f.Iam, f.Clock, f.RunConfiguration(), Effects(f, probe));
        Assert.Null(await authority.CurrentDefinitionAsync(next)); Assert.Equal(1, probe.NativeEffects);
        Assert.Equal(TenantOperationOutcome.Completed, (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, first.Operation.OperationId))!.Outcome);
    }

}
