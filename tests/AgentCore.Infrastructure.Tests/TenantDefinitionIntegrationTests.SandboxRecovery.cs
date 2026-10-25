using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_worker_reopens_and_retires_a_stranded_approved_command_without_replay(bool sqlite) => await SandboxRecoveryScenario(sqlite, "stranded");

    [Theory] [InlineData(false, "notDispatched")] [InlineData(true, "notDispatched")]
    [InlineData(false, "foregroundCompleted")] [InlineData(true, "foregroundCompleted")]
    [InlineData(false, "readyUnsettled")] [InlineData(true, "readyUnsettled")]
    [InlineData(false, "background")] [InlineData(true, "background")]
    [InlineData(false, "backgroundCompleted")] [InlineData(true, "backgroundCompleted")]
    [InlineData(false, "backgroundUnacknowledged")] [InlineData(true, "backgroundUnacknowledged")]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "deleted")] [InlineData(true, "deleted")]
    [InlineData(false, "backgroundRevoked")] [InlineData(true, "backgroundRevoked")]
    [InlineData(false, "backgroundForeign")] [InlineData(true, "backgroundForeign")]
    [InlineData(false, "backgroundDeadline")] [InlineData(true, "backgroundDeadline")]
    [InlineData(false, "backgroundSourceRevoked")] [InlineData(true, "backgroundSourceRevoked")]
    [InlineData(false, "backgroundRevokedDuringStatus")] [InlineData(true, "backgroundRevokedDuringStatus")]
    [InlineData(false, "hostChanged")] [InlineData(true, "hostChanged")]
    public async Task Tenant_Sandbox_worker_preserves_factual_outcomes_current_authority_and_background_launch_evidence(bool sqlite, string outcome) => await SandboxRecoveryScenario(sqlite, outcome);

    [Theory]
    [InlineData(false, "spaces.read")]
    [InlineData(true, "spaces.read")]
    [InlineData(false, "workspaces.write")]
    [InlineData(true, "workspaces.write")]
    public async Task Tenant_Sandbox_idle_mounts_retire_after_current_Space_or_workspace_authority_revocation(bool sqlite, string permission) =>
        await SandboxRecoveryScenario(sqlite, "readyUnsettled", permission);

    private static async Task SandboxRecoveryScenario(bool sqlite, string outcome, string? revokedContextPermission = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
            var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            await using var host = builder.Build(); host.Urls.Add("http://127.0.0.1:0");
            var deletes = 0; var dispatches = 0; var statuses = 0; var contacts = 0;
            host.Run(async http => {
                contacts++;
                Assert.Equal("synthetic-recovery-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var path = http.Request.Path.Value!;
                if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(host.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (http.Request.Method == "GET" && path == "/v1/sandboxes/recovery-provider")
                {
                    if (outcome == "hostChanged") { http.Response.StatusCode = 404; return; }
                    await http.Response.WriteAsJsonAsync(new { id = "recovery-provider", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                        metadata = new Dictionary<string,string> { ["agent-core-generation"] = s.Lease.Generation.ToString("N"),
                            ["agent-core-tenant"] = outcome is "foreign" or "backgroundForeign" or "hostChanged" ? Guid.Empty.ToString("N") : s.Tenant.ToString("N"),
                            ["agent-core-session"] = s.Session.SessionId.ToString("N"), ["agent-core-executor"] = s.Session.ExecutorPrincipalId.ToString("N") } });
                }
                else if (http.Request.Method == "DELETE") { deletes++; http.Response.StatusCode = 204; }
                else if (path == "/command/status/recovery-command")
                {
                    statuses++;
                    if (outcome == "backgroundRevokedDuringStatus") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    await http.Response.WriteAsJsonAsync(new { id = "recovery-command", running = outcome != "backgroundCompleted", exit_code = outcome == "backgroundCompleted" ? (int?)0 : null });
                }
                else if (path.EndsWith("/logs")) await http.Response.WriteAsync("bounded retained process output");
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
                else if (path == "/command")
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body);
                    var argv = json.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray();
                    if (argv.Contains("agent-core-command")) dispatches++;
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"internal-inspection\",\"timestamp\":0}\n\ndata: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":1}\n\n");
                }
                else http.Response.StatusCode = 404;
            });
            await host.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = host.Urls.Single(), ApiKey = "synthetic-recovery-key", CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            var mount = new CanonicalSandboxMountResolver(options).Resolve(s.Session.AgentInstanceId, s.Session.SessionId,
                await RuntimeWorkspaceAccess(f, s), create: true);
            s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, ProviderHostScope = new SandboxVerificationHost(options).Scope } };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "recovery-provider", BaselineProcesses = [] }, 1));
            var services = SandboxApprovalServices(f, s);
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf never-replay", TimeoutSeconds: 120, Background: outcome.StartsWith("background", StringComparison.Ordinal)), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            var reserved = await services.Execution.ReserveAsync(frame, request.RequestId); Assert.True(reserved.Reserved);
            var execution = reserved.Execution;
            if (outcome != "notDispatched")
            {
                execution = execution with { Revision = 2, FilesystemEffectsMayHaveOccurred = true,
                    State = outcome.StartsWith("background", StringComparison.Ordinal) ? SandboxExecutionState.Running
                        : outcome is "foregroundCompleted" or "readyUnsettled" ? SandboxExecutionState.Completed : SandboxExecutionState.Dispatching,
                    ProviderCommandId = outcome.StartsWith("background", StringComparison.Ordinal) ? "recovery-command" : null,
                    Phase = outcome == "backgroundUnacknowledged" ? "launchPending" : outcome.StartsWith("background", StringComparison.Ordinal) ? "execution"
                        : outcome == "foregroundCompleted" ? "processInspection" : outcome == "readyUnsettled" ? "completed" : "dispatch",
                    ExitCode = outcome is "foregroundCompleted" or "readyUnsettled" ? 0 : null,
                    FinishedAt = outcome is "foregroundCompleted" or "readyUnsettled" ? DateTimeOffset.UtcNow : null,
                    TerminationConfirmed = outcome == "readyUnsettled" };
                Assert.True(await storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant, execution, 1));
            }
            if (outcome == "readyUnsettled")
            {
                var lease = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Lease!;
                Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease with { Revision = 4, State = SandboxLeaseState.Ready, ActiveExecutionId = null }, 3));
            }
            if (outcome.StartsWith("background", StringComparison.Ordinal) && outcome != "backgroundUnacknowledged")
            {
                await services.Execution.SettleAsync(s.Tenant, frame.SessionId, execution.Id);
                var active = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
                var runtime = (ITenantRuntimeStore)f.Iam; var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!; var at = DateTimeOffset.UtcNow;
                var entry = new ConversationEntry(f.Ids.NewId(), snapshot.LastEntrySequence + 1, null, ConversationRole.Assistant, "Background launch acknowledged", active.ResponseId,
                    EntryStatus.Completed, SessionMode.Text, 0, 30, at, CompletedAt: at);
                await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Entries = [.. snapshot.Entries, entry], LastEntrySequence = entry.Sequence }, snapshot.Revision);
                await runtime.ApplyTenantRuntimeCommandAsync(active.Owner, active.AgentRunId,
                    new AgentRunCommand.Complete(active.Revision, at, frame.Generation, "Background launch acknowledged", AgentRunOutcomeKind.Response, entry.EntryId), f.Ids.NewId());
            }
            if (outcome is "revoked" or "backgroundRevoked") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            if (outcome == "backgroundSourceRevoked")
                await SetupProfiles(f).Profiles.SaveAsync(s.Tenant, s.Lease.Profile.Id, s.Human, SetupInput("Runtime source") with { Enabled = false }, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
            if (outcome == "deleted")
            {
                var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!; var runtime = (ITenantRuntimeStore)f.Iam;
                run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(run.Revision, DateTimeOffset.UtcNow, "Recovery cancellation"), f.Ids.NewId());
                await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.CommitCancellation(run.Revision, DateTimeOffset.UtcNow, frame.Generation, "Recovery cancellation"), f.Ids.NewId());
                var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant, frame.SessionId))!;
                var sessionService = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.Definitions,
                    new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, TimeProvider.System);
                await sessionService.ChangeAsync(s.Tenant, SandboxHuman(s), new(frame.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            }
            if (revokedContextPermission is not null)
            {
                var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
                var context = TenantWorkspaceRules.Context(s.Run);
                var scope = revokedContextPermission == "spaces.read" ? TenantSpaceService.Scope(s.Tenant, context.SpaceId) : TenantWorkspaceRules.Home(s.Tenant, context.SpaceId);
                var assignment = state.Assignments.Single(a => a.PrincipalId == s.Session.ExecutorPrincipalId && a.Scope == scope
                    && state.Roles.Single(r => r.RoleId == a.RoleId).Permissions.Contains(revokedContextPermission));
                await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, state.Tenant.Revision);
            }
            var recovered = sqlite ? new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, f.Content) : f.Iam;
            storage = (ITenantSandboxRuntimeStore)recovered; services = SandboxApprovalServices(f, s, recovered);
            var p = SetupProfiles(f); var target = new TenantSandboxOperationTarget((ITenantSessionStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, storage, s.Resolver, f.Authorizer, f.Iam, p.Images, p.Ready, TimeProvider.System);
            TimeProvider recoveryTime = outcome == "backgroundDeadline" ? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(execution.DeadlineAt!.Value.AddSeconds(1)) : TimeProvider.System;
            var generation = new TenantSandboxGenerationAuthority(f.Iam, (ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam, target, f.Authorizer, recoveryTime);
            if (outcome == "hostChanged")
            {
                options.WorkspaceDockerHostRoot += "-changed";
                var pinned = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Lease!;
                await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant,
                    pinned with { Revision = pinned.Revision + 1, ProviderHostScope = new SandboxVerificationHost(options).Scope }, pinned.Revision).AsTask());
            }
            var worker = new OpenSandboxService(options,f.Sandbox,null!,f.SessionStore,recoveryTime,tenantRuntime: storage,                approvedSandbox: services.Execution,tenantRuns: (ITenantRunStore)f.Iam,tenantGenerationAuthority: generation);
            await worker.ReconcileAsync();
            var facts = (await storage.ReadTenantSandboxExecutionFactsAsync(s.Tenant, frame.SessionId, execution.Id))!;
            var kept = revokedContextPermission is null && outcome is ("background" or "backgroundCompleted" or "readyUnsettled");
            Assert.Equal(outcome is "foreign" or "backgroundForeign" or "hostChanged" ? SandboxLeaseState.NeedsReconciliation : kept ? outcome == "background" ? SandboxLeaseState.Executing : SandboxLeaseState.Ready : SandboxLeaseState.Terminated, facts.Lease!.State);
            Assert.Equal(0, dispatches); Assert.Equal(kept || outcome is "foreign" or "backgroundForeign" or "hostChanged" ? 0 : 1, deletes);
            Assert.Equal(outcome is "background" or "backgroundCompleted" or "backgroundRevokedDuringStatus" ? 1 : 0, statuses);
            var effect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
            Assert.Equal(outcome == "notDispatched" ? TenantOperationOutcome.Denied : kept || outcome is "foregroundCompleted" or "readyUnsettled" || outcome.StartsWith("background", StringComparison.Ordinal) && outcome != "backgroundUnacknowledged" ? TenantOperationOutcome.Completed : TenantOperationOutcome.Indeterminate, effect!.Outcome);
            Assert.Equal(outcome is not ("foreign" or "backgroundForeign" or "background" or "hostChanged"), facts.Execution.TerminationConfirmed);
            if (outcome == "backgroundCompleted") { Assert.Equal(SandboxExecutionState.Completed, facts.Execution.State); Assert.Contains("bounded retained", facts.Execution.Stdout); }
            if (outcome == "backgroundRevokedDuringStatus") Assert.DoesNotContain("bounded retained", facts.Execution.Stdout);
            if (outcome is "background" or "backgroundCompleted") Assert.Equal(AgentRunStatus.Completed, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!.Status);
            await worker.ReconcileAsync();
            if (outcome == "hostChanged") { Assert.Equal(0, contacts); Assert.Contains("original server", facts.Lease.SafeMessage); }
            Assert.Equal(0, dispatches); Assert.Equal(kept || outcome is "foreign" or "backgroundForeign" or "hostChanged" ? 0 : 1, deletes);
            Assert.Equal(effect, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
            Assert.Null(await f.Sandbox.GetLeaseAsync(frame.SessionId)); Assert.Empty(await f.Sandbox.ListLeasesAsync());
            if (outcome == "deleted") Assert.Null(await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId));
            await host.StopAsync();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_technical_recovery_cursor_is_bounded_stable_and_does_not_expose_owned_leases_globally(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var storage = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
            f.Definitions, new SessionNoBuiltinFallback(), f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, TimeProvider.System);
        var runs = new TenantRunService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, s.Resolver, f.Identity, f.Authorizer,
            new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), f.Ids, TimeProvider.System);
        var expected = new List<Guid> { s.Session.SessionId };
        for (var i = 0; i < 2; i++)
        {
            var session = await sessions.CreateAsync(s.Tenant, s.Session.AgentInstanceId, SandboxHuman(s), SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
            var admission = await runs.SubmitTextAsync(s.Tenant, session.Owner.SessionId, SandboxHuman(s), f.Ids.NewId(), "Recovery inventory", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
            var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, admission.RunId))!; var at = DateTimeOffset.UtcNow;
            run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
            var lease = s.Lease with { SessionId = run.SessionId, Generation = f.Ids.NewId(), CreatedAt = at, ExpiresAt = at.AddMinutes(10), AdmissionRunId = run.AgentRunId,
                Profile = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!) };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(new(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, run.Revision), lease, 0, at));
            expected.Add(run.SessionId);
        }
        var found = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await storage.ReadTenantSandboxRecoveryPageAsync(cursor, 1);
            var item = Assert.Single(page.Items); Assert.Equal(s.Tenant, item.TenantId);
            found.Add(item.SessionId); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(expected.OrderBy(id => id.ToString("D"), StringComparer.Ordinal), found);
        Assert.Empty(await f.Sandbox.ListLeasesAsync());
        Assert.Empty((await storage.ReadTenantSandboxRecoveryPageAsync(found.Last().ToString("D"), 1)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadTenantSandboxRecoveryPageAsync("not-a-session-cursor", 1).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ReadTenantSandboxRecoveryPageAsync(null, 101).AsTask());
    }

}
