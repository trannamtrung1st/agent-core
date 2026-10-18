using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
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
    [InlineData(false, "deleted")] [InlineData(true, "deleted")]
    [InlineData(false, "callerCanceled")] [InlineData(true, "callerCanceled")]
    [InlineData(false, "duringCommandRevoked")] [InlineData(true, "duringCommandRevoked")]
    [InlineData(false, "cleanupForeign")] [InlineData(true, "cleanupForeign")]
    [InlineData(false, "absent")] [InlineData(true, "absent")]
    [InlineData(false, "instanceChanged")] [InlineData(true, "instanceChanged")]
    [InlineData(false, "backgroundIncomplete")] [InlineData(true, "backgroundIncomplete")]
    [InlineData(false, "backgroundError")] [InlineData(true, "backgroundError")]
    public async Task Tenant_Sandbox_SDK_keeps_launch_uncertainty_and_authority_boundaries_without_replay(bool sqlite, string outcome)
    {
        await SandboxSdkScenario(sqlite, outcome);
    }
    private static async Task SandboxSdkScenario(bool sqlite, string outcome)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-command-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            using var callerCancellation = new CancellationTokenSource();
            var s = await PrepareRuntimeSandbox(f);
            var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            await using var host = builder.Build(); host.Urls.Add("http://127.0.0.1:0");
            var dispatches = 0; var deletes = 0; var contacts = 0; JsonElement commandBody = default;
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
                    if (userCommand && outcome == "callerCanceled") callerCancellation.Cancel();
                    if (userCommand && outcome == "duringCommandRevoked")
                        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    if (userCommand && outcome == "deleted")
                    {
                        var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
                        var runtime = (ITenantRuntimeStore)f.Iam;
                        active = await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                            new AgentRunCommand.RequestCancellation(active.Revision, DateTimeOffset.UtcNow, "SDK boundary cancellation"), f.Ids.NewId());
                        await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                            new AgentRunCommand.CommitCancellation(active.Revision, DateTimeOffset.UtcNow, active.Claim!.Generation, "SDK boundary cancellation"), f.Ids.NewId());
                        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
                            f.Definitions, new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, AgentCore.Infrastructure.Providers.ModelCatalogFactory.Synthetic(), new(), f.Ids, TimeProvider.System);
                        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, s.Session.SessionId))!;
                        await sessions.ChangeAsync(s.Tenant, SandboxHuman(s), new(s.Session.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete),
                            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    }
                    if (userCommand && outcome is "uncertain" or "deleted" or "cleanupForeign" or "absent") { http.Response.StatusCode = 500; await http.Response.WriteAsync("PRIVATE_SDK_BODY"); return; }
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"owned-command\",\"timestamp\":0}\n\n");
                    if (userCommand) await http.Response.WriteAsync("data: {\"type\":\"stdout\",\"text\":\"actual SDK output\",\"timestamp\":1}\n\n");
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
                s.Run.Admission.Configuration!.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite, create: true);
            s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, ProviderHostScope = new SandboxVerificationHost(options).Scope } };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2,
                State = SandboxLeaseState.Ready, ProviderId = "owned-environment", BaselineProcesses = [] }, 1));
            var services = SandboxApprovalServices(f, s);
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf 'exact tenant command'", TimeoutSeconds: 30, Background: outcome.StartsWith("background", StringComparison.Ordinal)), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            var p = SetupProfiles(f); var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam,
                f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
            var target = new TenantSandboxOperationTarget((ITenantSessionStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, storage,
                s.Resolver, f.Authorizer, f.Iam, p.Images, p.Ready, TimeProvider.System);
            var authority = new TenantSandboxExecutionAuthority(operations, storage, (ITenantApprovalStore)f.Iam,
                (ITenantApprovedOperationStore)f.Iam, (ITenantRunStore)f.Iam, target, f.Authorizer, TimeProvider.System);
            var sdk = new OpenSandboxService(options, f.Sandbox, null!, f.SessionStore, null!, TimeProvider.System,
                tenantRuntime: storage, approvedSandbox: services.Execution, tenantExecutionAuthority: authority, tenantRuns: (ITenantRunStore)f.Iam);
            var local = new TenantApprovedOperationService(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantApprovalStore)f.Iam,
                (ITenantApprovedOperationStore)f.Iam, operations, s.Resolver, f.Ids, TimeProvider.System, ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default);
            var continuation = new TenantRunContinuationService((ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, local, TimeProvider.System,
                sandboxCommands: sdk);
            if (outcome == "completed") await continuation.PrepareExternalEffectAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!);
            SandboxCommandResult result;
            if (outcome == "callerCanceled")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sdk.ExecuteApprovedAsync(frame, request.RequestId, callerCancellation.Token).AsTask());
                var retained = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Execution!;
                result = new(retained.Id, retained.State.ToString(), retained.ExitCode, retained.Stdout, retained.Stderr, retained.Truncated,
                    retained.DurationMs ?? 0, retained.FilesystemEffectsMayHaveOccurred, TerminationConfirmed: retained.TerminationConfirmed);
            }
            else result = await sdk.ExecuteApprovedAsync(frame, request.RequestId);
            var denied = outcome is "foreign" or "revoked" or "instanceChanged";
            var uncertain = outcome is "uncertain" or "deleted" or "callerCanceled" or "duringCommandRevoked" or "cleanupForeign" or "absent" or "backgroundIncomplete" or "backgroundError";
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
            if (outcome == "deleted") { Assert.Null(run); Assert.True((await storage.ReadTenantSandboxExecutionFactsAsync(s.Tenant, frame.SessionId, result.ExecutionId))!.RunCancellationRequested); }
            else
            {
                Assert.Null(run!.Owner.ProfileIdOrNull);
                Assert.Equal(denied ? AgentRunSideEffectDisposition.DefinitelyFailed : uncertain ? AgentRunSideEffectDisposition.Indeterminate : AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition);
                var resumed = await continuation.ResumeAsync(run);
                Assert.Equal(effect.Outcome, resumed.Outcome!.Outcome);
                Assert.Equal(effect.ReasonCode, resumed.Outcome.ReasonCode);
            }
            if (outcome == "completed") { Assert.Equal(0, result.ExitCode); Assert.Equal("actual SDK output", result.Stdout); Assert.True(result.TerminationConfirmed); }
            else if (outcome == "background") { Assert.Equal("Running", result.Status); Assert.Equal("sandbox_command_started", effect.ReasonCode); }
            else if (!denied)
            {
                Assert.Equal(outcome is "deleted" or "callerCanceled" ? "Canceled" : "Indeterminate", result.Status);
                Assert.Equal(outcome is "cleanupForeign" or "absent" ? 0 : 1, deletes);
                Assert.DoesNotContain("PRIVATE_SDK_BODY", result.Stderr);
                if (outcome == "cleanupForeign") { Assert.False(result.TerminationConfirmed); Assert.Equal("NeedsReconciliation", result.EnvironmentState); }
                if (outcome == "absent") Assert.True(result.TerminationConfirmed);
            }
            else { Assert.Equal("sandbox_command_not_dispatched", effect.ReasonCode); Assert.False(result.FilesystemEffectsMayHaveOccurred); Assert.Equal(0, deletes); }
            var before = contacts;
            if (outcome is "revoked" or "deleted" or "duringCommandRevoked") await Assert.ThrowsAsync<AccessDeniedException>(() => sdk.ExecuteApprovedAsync(frame, request.RequestId).AsTask());
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
