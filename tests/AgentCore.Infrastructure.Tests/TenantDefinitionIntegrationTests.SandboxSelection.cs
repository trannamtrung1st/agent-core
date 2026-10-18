using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_profile_switch_retires_before_fresh_exact_review_and_never_falls_back(bool sqlite, bool execute)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-switch-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite); RuntimeSandboxSetup s = null!;
            var storage = (ITenantSandboxRuntimeStore)f.Iam; var repaired = execute; var contacts = 0; var kills = 0; var creates = 0; var commands = 0; var initializations = 0; Dictionary<string,string> metadata = [];
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var server = builder.Build(); server.Urls.Add("http://127.0.0.1:0");
            server.Run(async http =>
            {
                contacts++; Assert.Equal("synthetic-selection-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var path = http.Request.Path.Value!;
                if (path == "/v1/sandboxes/old-environment")
                {
                    if (http.Request.Method == "DELETE") { kills++; http.Response.StatusCode = 204; }
                    else
                    {
                        Assert.Equal("GET", http.Request.Method);
                        await http.Response.WriteAsJsonAsync(new { id = "old-environment", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                            metadata = new Dictionary<string,string> { ["agent-core-generation"] = s.Lease.Generation.ToString("N"), ["agent-core-tenant"] = repaired ? s.Tenant.ToString("N") : Guid.Empty.ToString("N"),
                                ["agent-core-session"] = s.Session.SessionId.ToString("N"), ["agent-core-executor"] = s.Session.ExecutorPrincipalId.ToString("N") } });
                    }
                }
                else if (path == "/agent-core/generations/capabilities") await http.Response.WriteAsJsonAsync(new { contractVersion = 1, retirementFencing = true });
                else if (path == "/v1/sandboxes" && http.Request.Method == "POST")
                {
                    creates++; using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    Assert.Equal(s.Alternate!.ImageReference, body.RootElement.GetProperty("image").GetProperty("uri").GetString());
                    metadata = body.RootElement.GetProperty("metadata").Deserialize<Dictionary<string,string>>()!;
                    await http.Response.WriteAsJsonAsync(new { id = "new-environment", status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, entrypoint = new[] { "/bin/sh" }, metadata });
                }
                else if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(server.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (path == "/v1/sandboxes/new-environment" && http.Request.Method == "GET")
                    await http.Response.WriteAsJsonAsync(new { id = "new-environment", status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, entrypoint = new[] { "/bin/sh" }, metadata });
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
                else if (path.StartsWith("/agent-core/mounts/", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                    Assert.Equal(metadata["agent-core-generation"], value.GetProperty("ownerValue").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 4, runtimeUser = value.GetProperty("uid").GetInt32() + ":" + value.GetProperty("gid").GetInt32(), topology = "linux", imageId = "fixture", imageUser = "fixture" });
                }
                else if (path == "/command")
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var argv = body.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray(); var script = argv.Last()!;
                    var user = argv.Contains("agent-core-command");
                    if (user) { if (script == "printf alternate-init") initializations++; else { Assert.Equal("printf selected-command", script); commands++; } }
                    if (script.Contains("stage=process_isolation", StringComparison.Ordinal))
                        foreach (var directory in Directory.EnumerateDirectories(root, ".core-probe-*", SearchOption.AllDirectories))
                        {
                            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core"));
                            foreach (var name in new[] { "renamed", "hard", "symbolic" }) { var file = Path.Combine(directory, name); await File.WriteAllBytesAsync(file, bytes); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                        }
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"selected-command\",\"timestamp\":0}\n\n");
                    if (user) await http.Response.WriteAsync("data: {\"type\":\"stdout\",\"text\":\"selected output\",\"timestamp\":1}\n\n");
                    await http.Response.WriteAsync("data: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":2}\n\n");
                }
                else http.Response.StatusCode = 404;
            });
            await server.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true, Endpoint = server.Urls.Single(), ApiKey = "synthetic-selection-key", CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            s = await PrepareRuntimeSandbox(f, options: options, alternate: true); Assert.NotNull(s.Alternate);
            var mount = new CanonicalSandboxMountResolver(options).Resolve(s.Session.AgentInstanceId, s.Session.SessionId,
                s.Run.Admission.Configuration!.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite, create: true);
            await mount.Working.WriteAsync("/working/keep.txt", "retained canonical bytes"u8.ToArray(), 1024, long.MaxValue, default);
            s = s with { Lease = s.Lease with { MountAuthority = mount.Authority, SelectedProfileId = s.Lease.Profile.Id } };
            Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "old-environment" }, 1));
            var p = SetupProfiles(f, options);
            var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer, f.Ids, TimeProvider.System,
                SetupResolver(f), p.Images, p.Ready, p.Host);
            var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
            var change = new TenantSandboxProfileSwitch(s.Session.SessionId, saved.SessionRevision, saved.Lease!.Generation, saved.Lease.Revision, null, s.Alternate.Id, 1, 1);
            var receipt = await controls.RequestProfileSwitchAsync(s.Tenant, s.Human, change, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            Assert.False(receipt.RetirementConfirmed); Assert.Equal(0, contacts);
            var pending = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
            Assert.Equal(SandboxLeaseState.Terminating, pending.Lease!.State); Assert.Equal(s.Lease.Profile.Id, pending.Lease.Profile.Id);
            Assert.Equal(s.Alternate.Id, pending.Lease.SelectedProfileId); Assert.NotNull(pending.Lease.SelectionWitness);
            var safe = await new TenantSandboxRuntimeReadService(f.Iam, (ITenantSessionStore)f.Iam, storage, f.Identity, f.Authorizer).ReadAsync(s.Tenant, s.Session.SessionId, s.Human);
            Assert.Equal(s.Alternate.Id, safe.Environment!.Selection!.ProfileId); Assert.Equal("retirement", safe.Environment.Selection.StoredPhase);
            var services = SandboxApprovalServices(f, s, options: options);
            await Assert.ThrowsAsync<AccessDeniedException>(() => services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Alternate.Id,
                new("printf must-wait", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation).AsTask());
            Assert.Equal(0, contacts); await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            var current = sqlite ? new SqliteIamStore(f.Contexts!, f.Content) : f.Iam; storage = (ITenantSandboxRuntimeStore)current;
            var worker = new OpenSandboxService(options, f.Sandbox, null!, f.SessionStore, null!, TimeProvider.System, tenantRuntime: storage);
            await worker.ReconcileAsync();
            if (!execute)
            {
                var blocked = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!;
                Assert.Equal(SandboxLeaseState.NeedsReconciliation, blocked.State); Assert.Equal("retirement", blocked.TransitionPhase);
                Assert.Equal(pending.Lease.SelectionWitness, blocked.SelectionWitness); Assert.Equal(0, kills); Assert.Equal(0, creates);
                repaired = true; await worker.ReconcileAsync();
            }
            var retired = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!;
            Assert.Equal(SandboxLeaseState.Terminated, retired.Lease!.State); Assert.Equal("awaitingReview", retired.Lease.TransitionPhase);
            Assert.Equal(pending.Lease.SelectionWitness, retired.Lease.SelectionWitness); Assert.Equal(1, kills);
            Assert.Equal("retained canonical bytes", System.Text.Encoding.UTF8.GetString(await mount.Working.ReadAsync("/working/keep.txt", 1024, default)));
            var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
            s = s with { Human = new(s.Human.AccountId, login.SessionId, s.Human.PrincipalId) };
            services = SandboxApprovalServices(f, s, current, options);
            await Assert.ThrowsAsync<SecurityConflictException>(() => services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf no-fallback", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation).AsTask());
            var review = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Alternate.Id,
                new("printf selected-command", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            Assert.True(review.Snapshot!.Sandbox!.WillInitialize);
            var view = await services.Approvals.ReadViewAsync(s.Tenant, review.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, review.RequestId, SandboxHuman(s), true, review.Operation.ActionHash, view.SecurityRevision, review.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            if (execute)
            {
                var operations = new TenantOperationAuthorizer(current, (ITenantRunStore)current, (ITenantAgentStore)current, new IamAuthorizer(current), new((ITenantAuthorizationAuditStore)current, f.Ids, TimeProvider.System));
                var target = new TenantSandboxOperationTarget((ITenantSessionStore)current, (ITenantSandboxProfileStore)current, storage, s.Resolver, new IamAuthorizer(current), current, p.Images, p.Ready, TimeProvider.System, p.Host);
                var authority = new TenantSandboxExecutionAuthority(operations, storage, (ITenantApprovalStore)current, (ITenantApprovedOperationStore)current, (ITenantRunStore)current, target, new IamAuthorizer(current), TimeProvider.System);
                var files = new FileSessionWorkspace(root, Path.Combine(root, "templates"), publicationResources: new DefinitionPublicationResourceReader(f.Resources, f.Content), sessions: f.SessionStore, instances: f.Instances, sandboxes: f.Sandbox, tenantAuthority: authority);
                var sdk = new OpenSandboxService(options, f.Sandbox, files, f.SessionStore, null!, TimeProvider.System, imageAuthority: p.Images,
                    tenantRuntime: storage, approvedSandbox: services.Execution, tenantExecutionAuthority: authority, tenantRuns: (ITenantRunStore)current);
                var result = await sdk.ExecuteApprovedAsync(frame, review.RequestId); Assert.Equal("Completed", result.Status); Assert.Equal("selected output", result.Stdout);
                var total = contacts; Assert.Equal(result.ExecutionId, (await sdk.ExecuteApprovedAsync(frame, review.RequestId)).ExecutionId); Assert.Equal(total, contacts);
            }
            else
            {
                var reserved = await services.Execution.ReserveAsync(frame, review.RequestId);
                Assert.True(reserved.Reserved); Assert.True(reserved.Execution.InitializesGeneration); Assert.NotEqual(s.Lease.Generation, reserved.Execution.Generation);
            }
            var selected = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Lease!;
            Assert.Equal(s.Alternate.Id, selected.Profile.Id); Assert.Null(selected.SelectionWitness); Assert.Null(selected.TransitionProfileId);
            Assert.False((await services.Execution.ReserveAsync(frame, review.RequestId)).Reserved);
            Assert.Equal(1, kills); Assert.Equal(execute ? 1 : 0, creates); Assert.Equal(execute ? 1 : 0, initializations); Assert.Equal(execute ? 1 : 0, commands);
            if (!execute) Assert.Equal(3, contacts);
            Assert.Equal("retained canonical bytes", System.Text.Encoding.UTF8.GetString(await mount.Working.ReadAsync("/working/keep.txt", 1024, default)));
            Assert.Null(await f.Sandbox.GetLeaseAsync(frame.SessionId)); await server.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }
}
