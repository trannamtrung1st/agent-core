using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false, "quiescent")] [InlineData(true, "quiescent")]
    [InlineData(false, "residual")] [InlineData(true, "residual")]
    [InlineData(false, "lostInterrupt")] [InlineData(true, "lostInterrupt")]
    [InlineData(false, "claimedCrash")] [InlineData(true, "claimedCrash")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    [InlineData(false, "hostChanged")] [InlineData(true, "hostChanged")]
    [InlineData(false, "resetDuringStatus")] [InlineData(true, "resetDuringStatus")]
    public async Task Tenant_Sandbox_human_Stop_interrupts_once_or_retires_without_replaying_original_effect(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite); RuntimeSandboxSetup s = null!;
            var storage = (ITenantSandboxRuntimeStore)f.Iam; var interrupts = 0; var kills = 0; var userCommands = 0; var repaired = false;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
            server.Run(async http =>
            {
                Assert.Equal("synthetic-stop-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]); var path = http.Request.Path.Value!;
                if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(server.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (path == "/v1/sandboxes/stop-provider" && http.Request.Method == "GET")
                    await http.Response.WriteAsJsonAsync(new { id = "stop-provider", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                        metadata = new Dictionary<string,string> { ["agent-core-generation"] = s.Lease.Generation.ToString("N"), ["agent-core-tenant"] = mode == "foreign" && !repaired ? Guid.Empty.ToString("N") : s.Tenant.ToString("N"),
                            ["agent-core-session"] = s.Session.SessionId.ToString("N"), ["agent-core-executor"] = s.Session.ExecutorPrincipalId.ToString("N") } });
                else if (path == "/v1/sandboxes/stop-provider" && http.Request.Method == "DELETE") { kills++; http.Response.StatusCode = 204; }
                else if (path == "/command" && http.Request.Method == "DELETE")
                {
                    interrupts++; var body = await new StreamReader(http.Request.Body).ReadToEndAsync(); Assert.Contains("stop-command", http.Request.QueryString + body);
                    http.Response.StatusCode = mode == "lostInterrupt" ? 500 : 204;
                    if (mode == "lostInterrupt") await http.Response.WriteAsync("PRIVATE_INTERRUPT_RESPONSE");
                }
                else if (path == "/command/status/stop-command")
                {
                    if (mode == "resetDuringStatus")
                    {
                        var current = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
                        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
                        await controls.RequestResetAsync(s.Tenant, s.Human, new(s.Session.SessionId, current.SessionRevision, current.Lease!.Generation,
                            current.Lease.Revision, current.Execution!.Id), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    }
                    await http.Response.WriteAsJsonAsync(new { id = "stop-command", running = false, exit_code = -1 });
                }
                else if (path.EndsWith("/logs")) await http.Response.WriteAsync("bounded stopped output");
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
                else if (path == "/command" && http.Request.Method == "POST")
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body); var argv = json.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray();
                    if (argv.Contains("agent-core-command")) userCommands++;
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"internal-inspection\",\"timestamp\":0}\n\n");
                    if (mode == "residual") await http.Response.WriteAsync("data: {\"type\":\"stdout\",\"text\":\"999:1\\n\",\"timestamp\":1}\n\n");
                    await http.Response.WriteAsync("data: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":2}\n\n");
                }
                else { http.Response.StatusCode = 404; await http.Response.WriteAsync("Unrecognized controlled route: " + http.Request.Method + " " + path); }
            });
            await server.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = server.Urls.Single(), ApiKey = "synthetic-stop-key", CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            s = await PrepareRuntimeSandbox(f, options: options);
            var mount = new CanonicalSandboxMountResolver(options).Resolve(s.Session.AgentInstanceId, s.Session.SessionId,
                s.Run.Admission.Configuration!.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite, create: true);
            await mount.Working.WriteAsync("/working/keep.txt", "retained bytes"u8.ToArray(), 1024, long.MaxValue, default);
            s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, BaselineProcesses = [], SelectedProfileId = s.Lease.Profile.Id } };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "stop-provider" }, 1));
            var services = SandboxApprovalServices(f, s, options: options);
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf never-replay", TimeoutSeconds: 120, Background: true), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals); var reserved = await services.Execution.ReserveAsync(frame, request.RequestId);
            var running = reserved.Execution with { Revision = 2, State = SandboxExecutionState.Running, ProviderCommandId = "stop-command", Phase = "execution", FilesystemEffectsMayHaveOccurred = true };
            Assert.True(await storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, running, 1)); await services.Execution.SettleAsync(s.Tenant, frame.SessionId, running.Id);
            var effect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId); Assert.Equal(TenantOperationOutcome.Completed, effect!.Outcome);
            var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
            var runtime = (ITenantRuntimeStore)f.Iam; var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!; var at = DateTimeOffset.UtcNow;
            var entry = new ConversationEntry(f.Ids.NewId(), snapshot.LastEntrySequence + 1, null, ConversationRole.Assistant, "Background launch acknowledged", active.ResponseId,
                EntryStatus.Completed, SessionMode.Text, 0, 30, at, CompletedAt: at);
            await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Entries = [.. snapshot.Entries, entry], LastEntrySequence = entry.Sequence }, snapshot.Revision);
            await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                new AgentRunCommand.Complete(active.Revision, at, frame.Generation, "Background launch acknowledged", AgentRunOutcomeKind.Response, entry.EntryId), f.Ids.NewId());
            var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            var stop = new TenantSandboxProcessStop(frame.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, running.Id, running.Revision);
            var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
            var accepted = await controls.RequestProcessStopAsync(s.Tenant, s.Human, stop, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            Assert.False(accepted.TerminationConfirmed); Assert.Equal(0, interrupts); Assert.Equal(0, kills);
            var pending = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!; Assert.NotNull(pending.Execution!.ProcessStop);
            Assert.Null(pending.Execution.ProcessStop.DispatchStartedAt); Assert.Equal(s.Lease.SelectedProfileId, pending.Lease!.SelectedProfileId);
            var safe = await new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer).ReadAsync(s.Tenant, frame.SessionId, s.Human);
            Assert.True(safe.Execution!.StopRequested); Assert.False(safe.Execution.StopDispatchStarted); Assert.True(safe.Environment!.CleanupPending);
            if (mode == "revoked") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            if (mode != "resetDuringStatus") await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            var current = sqlite ? new SqliteIamStore(f.Contexts!, f.Content) : f.Iam; storage = (ITenantSandboxRuntimeStore)current;
            services = SandboxApprovalServices(f, s, current, options); var authorizer = new IamAuthorizer(current); var p = SetupProfiles(f, options);
            var target = new TenantSandboxOperationTarget((ITenantSessionStore)current, (ITenantSandboxProfileStore)current, storage, s.Resolver, authorizer, current, p.Images, p.Ready, TimeProvider.System);
            var generation = new TenantSandboxGenerationAuthority(current, (ITenantRunStore)current, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, target, authorizer, TimeProvider.System);
            if (mode == "claimedCrash")
            {
                Assert.NotNull(await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, frame.SessionId, running.Id, pending.Execution.ProcessStop.RequestId,
                    pending.Execution.Revision, pending.Lease.Revision, DateTimeOffset.UtcNow));
                Assert.Null(await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, frame.SessionId, running.Id, pending.Execution.ProcessStop.RequestId,
                    pending.Execution.Revision, pending.Lease.Revision, DateTimeOffset.UtcNow));
            }
            var sdk = new OpenSandboxService(options, f.Sandbox, null!, f.SessionStore, null!, TimeProvider.System,
                tenantRuntime: storage, approvedSandbox: services.Execution, tenantGenerationAuthority: generation, tenantRuns: (ITenantRunStore)current);
            var originalMapping = options.WorkspaceDockerHostRoot;
            if (mode == "hostChanged") options.WorkspaceDockerHostRoot += "-changed";
            await sdk.ReconcileAsync();
            if (mode is "foreign" or "hostChanged")
            {
                var blocked = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
                Assert.Equal(SandboxLeaseState.NeedsReconciliation, blocked.Lease!.State); Assert.False(blocked.Execution!.TerminationConfirmed); Assert.Equal(0, kills); Assert.Equal(0, interrupts);
                repaired = true; options.WorkspaceDockerHostRoot = originalMapping; await sdk.ReconcileAsync();
            }
            var final = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.Equal(mode == "quiescent" ? SandboxLeaseState.Ready : SandboxLeaseState.Terminated, final.Lease!.State);
            Assert.Equal(mode == "foreign" ? SandboxExecutionState.Indeterminate : SandboxExecutionState.Canceled, final.Execution!.State); Assert.True(final.Execution.TerminationConfirmed); Assert.Equal(mode == "resetDuringStatus" ? "humanReset" : "humanStop", final.Execution.CancellationReason);
            Assert.Equal(mode != "quiescent", final.Execution.RemoteTerminationConfirmed); if (mode is "revoked" or "hostChanged") Assert.Null(final.Execution.ProcessStop!.DispatchStartedAt);
            else Assert.NotNull(final.Execution.ProcessStop!.DispatchStartedAt);
            if (mode == "resetDuringStatus") { Assert.Equal("reset", final.Lease.CleanupReason); Assert.Null(final.Lease.SelectedProfileId); }
            Assert.Equal(mode is "claimedCrash" or "revoked" or "foreign" or "hostChanged" ? 0 : 1, interrupts); Assert.Equal(mode == "quiescent" ? 0 : 1, kills); Assert.Equal(0, userCommands);
            Assert.Equal(effect, await ((ITenantApprovedOperationStore)current).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
            Assert.Equal("retained bytes", System.Text.Encoding.UTF8.GetString(await mount.Working.ReadAsync("/working/keep.txt", 1024, default)));
            Assert.DoesNotContain("PRIVATE_INTERRUPT_RESPONSE", JsonSerializer.Serialize(final));
            await sdk.ReconcileAsync(); Assert.Equal(mode is "claimedCrash" or "revoked" or "foreign" or "hostChanged" ? 0 : 1, interrupts); Assert.Equal(mode == "quiescent" ? 0 : 1, kills);
            Assert.Null(await f.Sandbox.GetExecutionAsync(frame.SessionId, running.Id)); await server.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "relogin")] [InlineData(true, "relogin")]
    [InlineData(false, "password")] [InlineData(true, "password")]
    [InlineData(false, "wrong-principal")] [InlineData(true, "wrong-principal")]
    public async Task Tenant_Sandbox_Stop_transaction_fences_original_human_and_one_dispatch_across_reopen(bool sqlite, string retirement)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); await ReadySandbox(f, s);
        var storage = (ITenantSandboxRuntimeStore)f.Iam; var execution = RuntimeExecution(f, s) with { Background = true };
        Assert.True(await storage.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        execution = execution with { Revision = 2, State = SandboxExecutionState.Running, ProviderCommandId = "owned-stop-command", Phase = "execution" };
        Assert.True(await storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 1));
        var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
        var stop = new TenantSandboxProcessStop(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, execution.Id, execution.Revision);
        var before = (await f.Iam.ReadTenantAsync(s.Tenant))!; var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var audits = (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count; var human = s.Human;
        await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant,
            execution with { Revision = 3, ProcessStop = new(f.Ids.NewId(), human.PrincipalId, DateTimeOffset.UtcNow) }, 2).AsTask());
        if (retirement == "password") await f.Identity.ChangePasswordAsync(human.AccountId, "strong-owner-password", "replacement-owner-password");
        else if (retirement == "wrong-principal") human = human with { PrincipalId = f.Ids.NewId() };
        else { await f.Identity.SignOutAsync(human.AuthenticationSessionId); if (retirement == "relogin") Assert.NotNull(await f.Identity.SignInAsync("owner@example.test", "strong-owner-password")); }
        AuthorizationAudit Audit(HumanSessionReference actor) => new(f.Ids.NewId(), s.Tenant, actor.PrincipalId, "sandbox.process.stop.request", execution.Id.ToString("D"), "allowed", "human_stop", DateTimeOffset.UtcNow);
        Assert.Equal("initiator_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => storage.TryRequestTenantSandboxProcessStopAsync(next, before.Tenant.Revision, stop, human, Audit(human)).AsTask())).ReasonCode);
        Assert.Equal(saved, await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId));
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision); Assert.Equal(audits, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
        var login = (await f.Identity.SignInAsync("owner@example.test", retirement == "password" ? "replacement-owner-password" : "strong-owner-password"))!.Session;
        human = new(s.Human.AccountId, login.SessionId, s.Human.PrincipalId);
        var audit = Audit(human); var pending = (await storage.TryRequestTenantSandboxProcessStopAsync(next, before.Tenant.Revision, stop, human, audit))!;
        Assert.Equal(audit.AuditId, pending.Execution!.ProcessStop!.RequestId); Assert.Equal(human.PrincipalId, pending.Execution.ProcessStop.PrincipalId);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant,
            pending.Execution with { Revision = pending.Execution.Revision + 1, ProcessStop = null }, pending.Execution.Revision).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant,
            pending.Execution with { Revision = pending.Execution.Revision + 1, ProcessStop = pending.Execution.ProcessStop with { DispatchStartedAt = DateTimeOffset.UtcNow } }, pending.Execution.Revision).AsTask());
        if (sqlite) storage = new SqliteIamStore(f.Contexts!, f.Content);
        Assert.Null(await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, stop.SessionId, execution.Id, Guid.NewGuid(), pending.Execution.Revision, pending.Lease!.Revision, DateTimeOffset.UtcNow));
        Assert.Null(await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, stop.SessionId, execution.Id, audit.AuditId, pending.Execution.Revision, pending.Lease!.Revision + 1, DateTimeOffset.UtcNow));
        var winners = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, stop.SessionId, execution.Id, audit.AuditId, pending.Execution.Revision, pending.Lease!.Revision, DateTimeOffset.UtcNow))));
        var started = Assert.Single(winners, value => value is not null)!;
        Assert.NotNull(started); Assert.NotNull(started.ProcessStop!.DispatchStartedAt);
        if (sqlite) storage = new SqliteIamStore(f.Contexts!, f.Content);
        Assert.Equal(started, (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, stop.SessionId))!.Execution);
        Assert.Null(await storage.TryStartTenantSandboxProcessStopAsync(s.Tenant, stop.SessionId, execution.Id, audit.AuditId, started.Revision, pending.Lease.Revision, DateTimeOffset.UtcNow));
        Assert.Equal(audits + 1, (await f.Iam.ReadAuditAsync(s.Tenant, 200)).Count);
    }

}
