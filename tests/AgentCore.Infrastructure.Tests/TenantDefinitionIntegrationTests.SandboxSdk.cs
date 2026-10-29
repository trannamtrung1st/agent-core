using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_terminated_expired_generation_can_prepare_exact_initialization_review_without_dispatch(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2,
            State = SandboxLeaseState.Terminated, ExpiresAt = s.Lease.CreatedAt.AddTicks(1) }, 1));
        var request = (await SandboxApprovalServices(f, s).Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
            new("printf next-generation", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        Assert.True(request.Snapshot!.Sandbox!.WillInitialize);
        Assert.Equal(2, request.Operation.SandboxCommand!.ExpectedLeaseRevision);
        Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_approved_command_crosses_real_SDK_once_and_atomically_records_its_effect(bool sqlite)
    {
        await SandboxSdkScenario(sqlite, "completed");
    }
    [Theory] [InlineData(false, "uncertain")] [InlineData(true, "uncertain")]
    [InlineData(false, "background")] [InlineData(true, "background")]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "deletionRequested")] [InlineData(true, "deletionRequested")]
    [InlineData(false, "callerCanceled")] [InlineData(true, "callerCanceled")]
    [InlineData(false, "duringCommandRevoked")] [InlineData(true, "duringCommandRevoked")]
    [InlineData(false, "cleanupForeign")] [InlineData(true, "cleanupForeign")]
    [InlineData(false, "absent")] [InlineData(true, "absent")]
    [InlineData(false, "instanceChanged")] [InlineData(true, "instanceChanged")]
    [InlineData(false, "backgroundIncomplete")] [InlineData(true, "backgroundIncomplete")]
    [InlineData(false, "backgroundError")] [InlineData(true, "backgroundError")]
    [InlineData(false, "humanStop")] [InlineData(true, "humanStop")]
    [InlineData(false, "humanReset")] [InlineData(true, "humanReset")]
    public async Task Tenant_Sandbox_SDK_keeps_launch_uncertainty_and_authority_boundaries_without_replay(bool sqlite, string outcome)
    {
        await SandboxSdkScenario(sqlite, outcome);
    }
    [Theory] [InlineData(false, "outputLarge")] [InlineData(true, "outputLarge")]
    [InlineData(false, "outputRevoked")] [InlineData(true, "outputRevoked")]
    [InlineData(false, "checkpointMismatch")] [InlineData(true, "checkpointMismatch")]
    public async Task Tenant_Sandbox_model_continuation_bounds_output_rechecks_scope_and_rejects_changed_command_before_dispatch(bool sqlite, string outcome)
    {
        await SandboxSdkScenario(sqlite, outcome);
    }
    private static async Task SandboxSdkScenario(bool sqlite, string outcome)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-command-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            var modelContinuation = outcome is "completed" or "outputLarge" or "outputRevoked" or "checkpointMismatch";
            using var callerCancellation = new CancellationTokenSource();
            var s = await PrepareRuntimeSandbox(f);
            var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            await using var host = builder.Build(); host.Urls.Add("http://127.0.0.1:0");
            var dispatches = 0; var deletes = 0; var contacts = 0; JsonElement commandBody = default;
            var deletionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Run(async http =>
            {
                contacts++;
                Assert.Equal("synthetic-sdk-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var path = http.Request.Path.Value!;
                if (path.Contains("/endpoints/"))
                    await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(host.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (http.Request.Method == "GET" && path == "/v1/sandboxes/owned-environment")
                {
                    if (outcome == "absent" && dispatches > 0) { http.Response.StatusCode = 404; return; }
                    if (outcome == "instanceChanged")
                    {
                        var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, s.Session.AgentInstanceId))!;
                        await ConfigurationServices(f).Configuration.UpdatePersonaAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId,
                            agent.Instance.Persona with { Tone = "Changed while connecting" }, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision,
                            agent.Instance.Revision, agent.Instance.PersonaRevision);
                    }
                    if (outcome == "revoked")
                        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    await http.Response.WriteAsJsonAsync(new { id = "owned-environment", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" },
                        createdAt = DateTimeOffset.UtcNow, metadata = new Dictionary<string, string> {
                            ["agent-core-generation"] = s.Lease.Generation.ToString("N"), ["agent-core-tenant"] = outcome == "foreign" || outcome == "cleanupForeign" && dispatches > 0 ? Guid.Empty.ToString("N") : s.Tenant.ToString("N"),
                            ["agent-core-session"] = s.Session.SessionId.ToString("N"), ["agent-core-executor"] = s.Session.ExecutorPrincipalId.ToString("N") } });
                }
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
                else if (http.Request.Method == "DELETE") { deletes++; http.Response.StatusCode = 204; }
                else if (path == "/command")
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body);
                    var argv = json.RootElement.GetProperty("argv").EnumerateArray().Select(p => p.GetString()).ToArray();
                    var userCommand = argv.Contains("agent-core-command");
                    if (userCommand) { dispatches++; commandBody = json.RootElement.Clone(); }
                    if (userCommand && outcome is "humanStop" or "humanReset")
                    {
                        var current = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
                        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
                        var securityRevision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
                        if (outcome == "humanStop") await controls.RequestProcessStopAsync(s.Tenant, s.Human, new(s.Session.SessionId, current.SessionRevision,
                            current.Lease!.Generation, current.Lease.Revision, current.Execution!.Id, current.Execution.Revision), securityRevision);
                        else await controls.RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, current.SessionRevision,
                            current.Lease!.Generation, current.Lease.Revision, current.Execution!.Id), securityRevision);
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); } catch (OperationCanceledException) { }
                        return;
                    }
                    if (userCommand && outcome == "callerCanceled") callerCancellation.Cancel();
                    if (userCommand && outcome == "duringCommandRevoked")
                        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    if (userCommand && outcome == "deletionRequested")
                    {
                        var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
                        var runtime = (ITenantRuntimeStore)f.Iam;
                        active = await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                            new AgentRunCommand.RequestCancellation(active.Revision, DateTimeOffset.UtcNow, "SDK boundary cancellation"), f.Ids.NewId());
                        await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                            new AgentRunCommand.CommitCancellation(active.Revision, DateTimeOffset.UtcNow, active.Claim!.Generation, "SDK boundary cancellation"), f.Ids.NewId());
                        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
                            f.Definitions, new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, TimeProvider.System);
                        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
                        Assert.Equal("session_effect_reconciliation_pending", (await Assert.ThrowsAsync<SecurityConflictException>(async () => { await sessions.ChangeAsync(s.Tenant, SandboxHuman(s),
                            new(s.Session.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); })).ReasonCode);
                        deletionCompleted.TrySetResult();
                    }
                    if (userCommand && outcome is "uncertain" or "deletionRequested" or "cleanupForeign" or "absent") { http.Response.StatusCode = 500; await http.Response.WriteAsync("PRIVATE_SDK_BODY"); return; }
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"owned-command\",\"timestamp\":0}\n\n");
                    if (userCommand) await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { type = "stdout", text = outcome == "outputLarge" ? new string('界', 10000) : "actual SDK output", timestamp = 1 }) + "\n\n");
                    if (userCommand && outcome == "backgroundIncomplete") return;
                    if (userCommand && outcome == "backgroundError") await http.Response.WriteAsync("data: {\"type\":\"error\",\"error\":{\"name\":\"LaunchFailed\",\"value\":\"1\"}}\n\n");
                    await http.Response.WriteAsync("data: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":2}\n\n");
                }
                else { http.Response.StatusCode = 404; }
            });
            await host.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = host.Urls.Single(), ApiKey = "synthetic-sdk-key",
                CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            var mount = new CanonicalSandboxMountResolver(options).Resolve(s.Session.AgentInstanceId, s.Session.SessionId,
                await RuntimeWorkspaceAccess(f, s), create: true);
            s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, ProviderHostScope = new SandboxVerificationHost(options).Scope } };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2,
                State = SandboxLeaseState.Ready, ProviderId = "owned-environment", BaselineProcesses = [] }, 1));
            var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam,
                f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
            ModelToolCall? modelCall = null;
            if (modelContinuation)
            {
                modelCall = new("exact-sandbox-request", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "run_sandbox_command", profileId = s.Lease.Profile.Id,
                    command = new { command = "printf 'exact tenant command'", cwd = "/working", timeoutSeconds = 30, background = false } }));
                var checkpointed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(s.Run.Owner, s.Run.AgentRunId,
                    new AgentRunCommand.Checkpoint(s.Run.Revision, DateTimeOffset.UtcNow, s.Run.Claim!.Generation,
                        new(AgentRunToolCallCheckpoint.Write(outcome == "outputLarge"
                            ? [new(ModelRole.User, new string('x', 205000)), new(ModelRole.Assistant, "", ToolCalls: [modelCall])]
                            : [new(ModelRole.Assistant, "", ToolCalls: [modelCall])]), 1, 0, 180000, 0), null), f.Ids.NewId());
                s = s with { Run = checkpointed };
            }
            var services = SandboxApprovalServices(f, s);
            var request = (modelCall is not null
                ? await new TenantToolAuthorization((ITenantRunStore)f.Iam, operations, TimeProvider.System, services.Approvals, s.Resolver).PrepareAsync(s.Session.SessionId, modelCall,
                    new(false, TriggerKind.UserTurn, AgentInstanceId: s.Session.AgentInstanceId, AgentRunId: s.Run.AgentRunId,
                        OwnedSessionId: s.Session.SessionId, TenantId: s.Tenant, AgentRunGeneration: s.Run.Claim!.Generation), default)
                : await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                    new("printf 'exact tenant command'", TimeoutSeconds: 30, Background: outcome.StartsWith("background", StringComparison.Ordinal)), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            var p = SetupProfiles(f);
            var target = new TenantSandboxOperationTarget((ITenantSessionStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, storage,
                s.Resolver, f.Authorizer, f.Iam, p.Images, p.Ready, TimeProvider.System);
            var authority = new TenantSandboxExecutionAuthority(operations, storage, (ITenantApprovalStore)f.Iam,
                (ITenantApprovedOperationStore)f.Iam, (ITenantRunStore)f.Iam, target, f.Authorizer, TimeProvider.System);
            var sdk = new OpenSandboxService(options,f.Sandbox,null!,f.SessionStore,TimeProvider.System,                tenantRuntime: storage,approvedSandbox: services.Execution,tenantExecutionAuthority: authority,tenantRuns: (ITenantRunStore)f.Iam);
            var local = new TenantApprovedOperationService(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam,
                (ITenantApprovedOperationStore)f.Iam, operations, s.Resolver, f.Ids, TimeProvider.System, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), SyntheticProviderAliases.Default);
            var continuation = new TenantRunContinuationService((ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, local, TimeProvider.System,
                runtimeStore: (ITenantRuntimeStore)f.Iam, ids: f.Ids, sandboxCommands: sdk, sandboxResults: services.Execution);
            if (outcome == "checkpointMismatch")
            {
                var claimed = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
                var wrong = modelCall! with { ArgumentsJson = modelCall.ArgumentsJson.Replace("exact tenant command", "different command", StringComparison.Ordinal) };
                var savedCheckpoint = claimed.Checkpoint!;
                var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [wrong])]), savedCheckpoint.StepCount,
                    savedCheckpoint.OutputBytes, savedCheckpoint.RemainingOverallBudgetMs, savedCheckpoint.ActiveExecutionMs, savedCheckpoint.BudgetRecordedAtUtc);
                claimed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId,
                    new AgentRunCommand.Checkpoint(claimed.Revision, DateTimeOffset.UtcNow, frame.Generation, checkpoint, null), f.Ids.NewId());
                Assert.Equal("approval_continuation_mismatch", (await Assert.ThrowsAsync<SecurityConflictException>(() => continuation.PrepareExternalEffectAsync(claimed).AsTask())).ReasonCode);
                Assert.Equal(0, contacts); Assert.Null((await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Execution); return;
            }
            if (modelContinuation) await continuation.PrepareExternalEffectAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!);
            SandboxCommandResult result;
            if (outcome == "callerCanceled")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sdk.ExecuteApprovedAsync(frame, request.RequestId, callerCancellation.Token).AsTask());
                var retained = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Execution!;
                result = new(retained.Id, retained.State.ToString(), retained.ExitCode, retained.Stdout, retained.Stderr, retained.Truncated,
                    retained.DurationMs ?? 0, retained.FilesystemEffectsMayHaveOccurred, TerminationConfirmed: retained.TerminationConfirmed);
            }
            else result = await sdk.ExecuteApprovedAsync(frame, request.RequestId);
            if (outcome == "deletionRequested")
            {
                await deletionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
                    f.Definitions, new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, TimeProvider.System);
                var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
                Assert.Equal("session_effect_reconciliation_pending", (await Assert.ThrowsAsync<SecurityConflictException>(() => sessions.ChangeAsync(s.Tenant, SandboxHuman(s),
                    new(s.Session.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision).AsTask())).ReasonCode);
            }
            var denied = outcome is "foreign" or "revoked" or "instanceChanged";
            var uncertain = outcome is "uncertain" or "deletionRequested" or "callerCanceled" or "duringCommandRevoked" or "cleanupForeign" or "absent" or "backgroundIncomplete" or "backgroundError" or "humanStop" or "humanReset";
            Assert.Equal(denied ? 0 : 1, dispatches);
            if (!denied)
            {
                Assert.Equal(mount.Authority.Uid, commandBody.GetProperty("uid").GetInt32());
                Assert.Equal(mount.Authority.Gid, commandBody.GetProperty("gid").GetInt32());
                Assert.Equal("/working", commandBody.GetProperty("cwd").GetString());
                Assert.InRange(commandBody.GetProperty("timeout").GetInt64(), 1, 30_000);
                Assert.Equal("printf 'exact tenant command'", commandBody.GetProperty("argv").EnumerateArray().Last().GetString());
            }
            var effect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
            Assert.NotNull(effect);
            Assert.Equal(denied ? TenantOperationOutcome.Denied : uncertain ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, effect.Outcome);
            var run = await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId);
            if (outcome == "deletionRequested") { Assert.NotNull(run); Assert.Equal(AgentRunStatus.Cancelled, run.Status); Assert.True(run.RequiresEffectReconciliation); Assert.True((await storage.ReadTenantSandboxExecutionFactsAsync(s.Tenant, frame.SessionId, result.ExecutionId))!.RunCancellationRequested); }
            else
            {
                Assert.Null(run!.Owner.ProfileIdOrNull);
                Assert.Equal(denied ? AgentRunSideEffectDisposition.DefinitelyFailed : uncertain ? AgentRunSideEffectDisposition.Indeterminate : AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition);
                if (outcome == "outputRevoked") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                var resumed = await continuation.ResumeAsync(run);
                Assert.Equal(effect.Outcome, resumed.Outcome!.Outcome);
                Assert.Equal(effect.ReasonCode, resumed.Outcome.ReasonCode);
                if (modelContinuation)
                {
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages));
                    var text = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == modelCall!.Id).Text;
                    using var observed = JsonDocument.Parse(text);
                    if (outcome == "outputRevoked")
                    {
                        Assert.Equal(JsonValueKind.Null, observed.RootElement.GetProperty("sandbox").ValueKind);
                        Assert.True(observed.RootElement.GetProperty("resultUnavailable").GetBoolean()); Assert.DoesNotContain("actual SDK output", text);
                    }
                    else
                    {
                        var output = observed.RootElement.GetProperty("sandbox"); Assert.Equal("Completed", output.GetProperty("status").GetString());
                        if (outcome == "outputLarge")
                        {
                            Assert.True(output.GetProperty("truncated").GetBoolean()); Assert.NotEmpty(output.GetProperty("stdout").GetString()!);
                            Assert.True(output.GetProperty("stdout").GetString()!.Length < result.Stdout.Length); Assert.DoesNotContain("�", text);
                            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(resumed.Run.Checkpoint!.PayloadJson), 1, AgentRunLimits.MaxCheckpointBytes);
                        }
                        else Assert.Equal("actual SDK output", output.GetProperty("stdout").GetString());
                        Assert.Null((await services.Execution.ReadResultAsync(frame with { SessionId = Guid.NewGuid() }, request, effect)).Result);
                        Assert.Null((await services.Execution.ReadResultAsync(frame, request, effect with { OperationId = Guid.NewGuid() })).Result);
                    }
                    Assert.Equal(JsonValueKind.Null, observed.RootElement.GetProperty("applied").ValueKind);
                    var total = contacts; Assert.Equal(resumed.Run.Revision, (await continuation.ResumeAsync(resumed.Run)).Run.Revision); Assert.Equal(total, contacts);
                }
            }
            if (modelContinuation) { Assert.Equal(0, result.ExitCode); if (outcome != "outputLarge") Assert.Equal("actual SDK output", result.Stdout); Assert.True(result.TerminationConfirmed); }
            else if (outcome == "background") { Assert.Equal("Running", result.Status); Assert.Equal("sandbox_command_started", effect.ReasonCode); }
            else if (!denied)
            {
                Assert.Equal(outcome is "deletionRequested" or "callerCanceled" or "humanStop" or "humanReset" ? "Canceled" : "Indeterminate", result.Status);
                Assert.Equal(outcome is "cleanupForeign" or "absent" ? 0 : 1, deletes);
                Assert.DoesNotContain("PRIVATE_SDK_BODY", result.Stderr);
                if (outcome == "cleanupForeign") { Assert.False(result.TerminationConfirmed); Assert.Equal("NeedsReconciliation", result.EnvironmentState); }
                if (outcome == "absent") Assert.True(result.TerminationConfirmed);
            }
            else { Assert.Equal("sandbox_command_not_dispatched", effect.ReasonCode); Assert.False(result.FilesystemEffectsMayHaveOccurred); Assert.Equal(0, deletes); }
            if (outcome is "humanStop" or "humanReset")
            {
                var stopped = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
                Assert.Equal(SandboxLeaseState.Terminated, stopped.Lease!.State);
                Assert.Equal(outcome == "humanReset" ? "reset" : "processStop", stopped.Lease.CleanupReason);
                Assert.Equal(outcome, stopped.Execution!.CancellationReason);
                if (outcome == "humanStop") { Assert.NotNull(stopped.Execution.ProcessStop); Assert.Null(stopped.Execution.ProcessStop.DispatchStartedAt); }
            }
            var before = contacts;
            if (outcome is "revoked" or "deletionRequested" or "duringCommandRevoked" or "outputRevoked") await Assert.ThrowsAsync<AccessDeniedException>(() => sdk.ExecuteApprovedAsync(frame, request.RequestId).AsTask());
            else Assert.Equal(result.ExecutionId, (await sdk.ExecuteApprovedAsync(frame, request.RequestId)).ExecutionId);
            Assert.Equal(before, contacts); Assert.Equal(denied ? 0 : 1, dispatches);
            Assert.Null(await f.Sandbox.GetExecutionAsync(frame.SessionId, result.ExecutionId));
            var audit = JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(s.Tenant, 200));
            Assert.DoesNotContain("actual SDK output", audit); Assert.DoesNotContain("PRIVATE_SDK_BODY", audit); Assert.DoesNotContain("exact tenant command", audit);
            if (sqlite)
            {
                var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, f.Content);
                Assert.Equal(effect, await reopened.FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
