using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_first_generation_provisions_pinned_workspace_initializes_and_executes_once(bool sqlite) => await ProvisioningScenario(sqlite, "completed");

    [Theory] [InlineData(false, "initializationFailed")] [InlineData(true, "initializationFailed")]
    [InlineData(false, "revokedDuringCreate")] [InlineData(true, "revokedDuringCreate")]
    [InlineData(false, "foreignCreated")] [InlineData(true, "foreignCreated")]
    [InlineData(false, "createUnknown")] [InlineData(true, "createUnknown")]
    [InlineData(false, "mountFailure")] [InlineData(true, "mountFailure")]
    [InlineData(false, "occupiedTemplate")] [InlineData(true, "occupiedTemplate")]
    [InlineData(false, "instanceInitializationFailed")] [InlineData(true, "instanceInitializationFailed")]
    [InlineData(false, "revokedDuringInitialization")] [InlineData(true, "revokedDuringInitialization")]
    public async Task Tenant_Sandbox_first_generation_retains_failures_without_command_or_initialization_replay(bool sqlite, string outcome) => await ProvisioningScenario(sqlite, outcome);

    [Theory] [InlineData(false, "privateCompleted")] [InlineData(true, "privateCompleted")]
    [InlineData(false, "privateRotated")] [InlineData(true, "privateRotated")]
    public async Task Tenant_Sandbox_private_source_is_rechecked_after_probe_before_SDK_secret_dispatch(bool sqlite, string outcome) => await ProvisioningScenario(sqlite, outcome);

    private static async Task ProvisioningScenario(bool sqlite, string outcome)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-provision-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite);
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            await using var host = builder.Build(); host.Urls.Add("http://127.0.0.1:0");
            var completed = outcome is "completed" or "privateCompleted";
            var privateSource = outcome.StartsWith("private", StringComparison.Ordinal);
            var protector = new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(root + "-keys");
            var credentials = new TenantCredentialService(f.Iam, (ITenantCredentialStore)f.Iam, f.Authorizer, protector, f.Ids, TimeProvider.System);
            var registryProbes = 0;
            RuntimeSandboxSetup s = null!; JsonElement creation = default; Dictionary<string,string> metadata = [];
            var creates = 0; var dispatches = 0; var initializations = 0; var deletes = 0; var mountProbes = 0; var scripts = new List<string>();
            host.Run(async http =>
            {
                var path = http.Request.Path.Value!;
                if (path.StartsWith("/v2/", StringComparison.Ordinal))
                {
                    registryProbes++; Assert.Equal("HEAD", http.Request.Method);
                    Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture:PRIVATE_REGISTRY_ORIGINAL")), http.Request.Headers.Authorization);
                    if (outcome == "privateRotated") await credentials.ReplaceAsync(s.Human.PrincipalId, s.Tenant, s.Lease.Profile.RegistryCredentialId!.Value,
                        "PRIVATE_REGISTRY_ROTATED", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
                    http.Response.StatusCode = 200; return;
                }
                Assert.Equal("synthetic-sdk-key", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                if (http.Request.Method == "POST" && path == "/v1/sandboxes")
                {
                    creates++; using var json = await JsonDocument.ParseAsync(http.Request.Body); creation = json.RootElement.Clone();
                    metadata = creation.GetProperty("metadata").Deserialize<Dictionary<string,string>>()!;
                    if (outcome == "revokedDuringCreate") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    if (outcome == "foreignCreated") metadata["agent-core-tenant"] = Guid.Empty.ToString("N");
                    if (outcome == "createUnknown") { http.Response.StatusCode = 500; await http.Response.WriteAsync("PRIVATE_CREATE_BODY"); return; }
                    await http.Response.WriteAsJsonAsync(new { id = "created-environment", status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, entrypoint = new[] { "/bin/sh" }, metadata });
                }
                else if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(host.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (http.Request.Method == "GET" && path == "/v1/sandboxes/created-environment")
                    await http.Response.WriteAsJsonAsync(new { id = "created-environment", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, metadata });
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
                else if (http.Request.Method == "DELETE") { deletes++; http.Response.StatusCode = 204; }
                else if (path.StartsWith("/agent-core/mounts/", StringComparison.Ordinal))
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body); var body = json.RootElement;
                    Assert.Equal(metadata["agent-core-generation"], body.GetProperty("ownerValue").GetString());
                    if (outcome == "mountFailure") { http.Response.StatusCode = 409; return; }
                    await http.Response.WriteAsJsonAsync(new { contractVersion = SandboxProfileVerification.CurrentContractVersion,
                        runtimeUser = body.GetProperty("uid").GetInt32() + ":" + body.GetProperty("gid").GetInt32(), topology = "linux", imageId = "synthetic-image", imageUser = "synthetic-user" });
                }
                else if (path == "/command")
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body);
                    var argv = json.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()!).ToArray();
                    var script = argv.Last(); var user = argv.Contains("agent-core-command");
                    if (user) scripts.Add(script);
                    if (user && script is "printf initialization" or "printf instance-initialization") initializations++;
                    else if (user) { dispatches++; Assert.Equal("printf approved-command", script); }
                    if (script.Contains("stage=process_isolation", StringComparison.Ordinal))
                    {
                        mountProbes++;
                        // Controlled SDK peer emulates the required bidirectional probe writes. Real Docker is a separate qualification.
                        foreach (var directory in Directory.EnumerateDirectories(root, ".core-probe-*", SearchOption.AllDirectories))
                        {
                            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core"));
                            foreach (var name in new[] { "renamed", "hard", "symbolic" })
                            { var file = Path.Combine(directory, name); await File.WriteAllBytesAsync(file, bytes); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                        }
                    }
                    if (script == "printf initialization" && outcome == "revokedDuringInitialization")
                        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                    if (user && (outcome == "initializationFailed" || script == "printf instance-initialization" && outcome == "instanceInitializationFailed")) { http.Response.StatusCode = 500; return; }
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"created-command\",\"timestamp\":0}\n\n");
                    if (user) await http.Response.WriteAsync("data: {\"type\":\"stdout\",\"text\":\"sdk-output\",\"timestamp\":1}\n\n");
                    await http.Response.WriteAsync("data: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":2}\n\n");
                }
                else http.Response.StatusCode = 404;
            });
            await host.StartAsync();
            var options = new OpenSandboxOptions { Enabled = true, Endpoint = host.Urls.Single(), ApiKey = "synthetic-sdk-key",
                CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root, AllowLocalImageIds = true };
            var registry = new Uri(host.Urls.Single()).Authority;
            if (privateSource) { options.AllowLocalImageIds = false; options.AllowedImagePrefixes = [registry]; options.InsecureRegistryHosts = [registry]; }
            s = await PrepareRuntimeSandbox(f, options: options, initialization: "printf initialization", template: true, instanceInitialization: "printf instance-initialization",
                imageReference: privateSource ? registry + "/acme/tool@sha256:" + new string('a', 64) : null,
                registry: privateSource ? async (tenant, human) => (await credentials.CreateAsync(human.PrincipalId, tenant,
                    new("Private registry", AgentCore.Domain.Credentials.CredentialKind.Token, AgentCore.Domain.Credentials.CredentialPurpose.ContainerRegistry,
                        null, null, "PRIVATE_REGISTRY_ORIGINAL", ContainerRegistry: new(registry, "fixture", [registry + "/acme"])),
                    (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision)).Credential.CredentialId : null);
            var services = SandboxApprovalServices(f, s, options: options); var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf approved-command", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            Assert.True(request.Snapshot!.Sandbox!.WillInitialize); Assert.Equal("printf initialization", request.Snapshot.Sandbox.ProfileInitializationScript);
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            var p = SetupProfiles(f, options); var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam,
                f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
            var target = new TenantSandboxOperationTarget((ITenantSessionStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, storage, s.Resolver, f.Authorizer, f.Iam, p.Images, p.Ready, TimeProvider.System);
            var authority = new TenantSandboxExecutionAuthority(operations, storage, (ITenantApprovalStore)f.Iam, (ITenantApprovedOperationStore)f.Iam,
                (ITenantRunStore)f.Iam, target, f.Authorizer, TimeProvider.System);
            var workspace = new FileSessionWorkspace(root, Path.Combine(root, "untrusted-live-templates"), publicationResources: new DefinitionPublicationResourceReader(f.Resources, f.Content),
                sessions: f.SessionStore, instances: f.Instances, sandboxes: f.Sandbox, tenantAuthority: authority);
            var working = AgentWorkspacePhysicalPaths.WorkingDirectory(root, s.Session.AgentInstanceId, s.Session.SessionId);
            if (outcome == "occupiedTemplate") { Directory.CreateDirectory(working); await File.WriteAllTextAsync(Path.Combine(working, "welcome.txt"), "retained user bytes"); }
            var sdkImages = new SandboxImageAuthority(options, f.Sandbox, TimeProvider.System, protector: protector,
                tenantCredentials: (ITenantCredentialStore)f.Iam, tenantVerifications: (ITenantSandboxVerificationStore)f.Iam, verificationHost: p.Host);
            var sdk = new OpenSandboxService(options, f.Sandbox, workspace, f.SessionStore, null!, TimeProvider.System, imageAuthority: sdkImages,
                tenantRuntime: storage, approvedSandbox: services.Execution, tenantExecutionAuthority: authority, tenantRuns: (ITenantRunStore)f.Iam);
            var result = await sdk.ExecuteApprovedAsync(frame, request.RequestId);
            var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.True(saved.Execution!.InitializesGeneration); Assert.Equal(saved.Execution.Generation, saved.Lease!.Generation);
            Assert.Equal(outcome is "occupiedTemplate" or "privateRotated" ? 0 : 1, creates);
            Assert.Equal(privateSource ? 1 : 0, registryProbes);
            Assert.True(dispatches == (completed ? 1 : 0), JsonSerializer.Serialize(new { creates, dispatches, initializations, mountProbes, saved.Lease, saved.Execution }));
            Assert.Equal(completed || outcome == "instanceInitializationFailed" ? 2 : outcome is "initializationFailed" or "revokedDuringInitialization" ? 1 : 0, initializations);
            Assert.Null(await f.Sandbox.GetLeaseAsync(frame.SessionId)); Assert.Null(await f.SessionStore.LoadMetadataAsync(frame.SessionId));
            var effect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
            Assert.Equal(completed ? AgentCore.Domain.Security.TenantOperationOutcome.Completed : AgentCore.Domain.Security.TenantOperationOutcome.Indeterminate, effect!.Outcome);
            Assert.Equal(outcome == "occupiedTemplate" ? "retained user bytes" : "immutable admitted template", await File.ReadAllTextAsync(Path.Combine(working, "welcome.txt")));
            if (completed)
            {
                Assert.Equal(new[] { "printf initialization", "printf instance-initialization", "printf approved-command" }, scripts);
                Assert.Equal("sdk-output", result.Stdout); Assert.Equal(SandboxLeaseState.Ready, saved.Lease.State); Assert.Equal("Succeeded", saved.Lease.Initialization!.State);
                Assert.NotNull(saved.Execution.CommandStartedAt); Assert.True(saved.Execution.CommandStartedAt > saved.Execution.StartedAt);
                Assert.Equal(saved.Execution.CommandStartedAt.Value.AddSeconds(30), saved.Execution.DeadlineAt);
                Assert.Single(creation.GetProperty("volumes").EnumerateArray(), v => v.GetProperty("mountPath").GetString() == "/working");
                Assert.Equal(s.Tenant.ToString("N"), metadata["agent-core-tenant"]); Assert.Equal(1, mountProbes); Assert.Equal(0, deletes);
            }
            else
            {
                Assert.Null(saved.Execution.CommandStartedAt); Assert.Null(saved.Execution.DeadlineAt);
                Assert.Equal(outcome is "createUnknown" or "foreignCreated" or "occupiedTemplate" or "privateRotated" ? SandboxLeaseState.NeedsReconciliation : SandboxLeaseState.Terminated, saved.Lease.State);
                Assert.Equal(outcome is "createUnknown" or "foreignCreated" or "occupiedTemplate" or "privateRotated" ? 0 : 1, deletes);
            }
            var retained = JsonSerializer.Serialize(new { result, saved, effect, Audit = await f.Iam.ReadAuditAsync(s.Tenant, 200) });
            foreach (var secret in new[] { "PRIVATE_CREATE_BODY", "PRIVATE_REGISTRY_ORIGINAL", "PRIVATE_REGISTRY_ROTATED" }) Assert.DoesNotContain(secret, retained);
            if (outcome == "privateCompleted") Assert.Equal("PRIVATE_REGISTRY_ORIGINAL", creation.GetProperty("image").GetProperty("auth").GetProperty("password").GetString());
            if (outcome is "revokedDuringCreate" or "revokedDuringInitialization" or "privateRotated") Assert.Equal("sourceAuthorizationChanged", saved.Execution.FailureCategory);
            if (outcome == "mountFailure") Assert.Equal("mountFailure", saved.Execution.FailureCategory);
            if (outcome is "initializationFailed" or "instanceInitializationFailed") Assert.Equal("initializationFailed", saved.Lease.CleanupReason);
            var counts = (creates, dispatches, initializations, deletes);
            try { await sdk.ExecuteApprovedAsync(frame, request.RequestId); }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
            Assert.Equal(counts, (creates, dispatches, initializations, deletes));
            if (sqlite)
            {
                var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, f.Content);
                Assert.Equal(saved.Execution, (await reopened.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!.Execution);
            }
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(root + "-keys")) Directory.Delete(root + "-keys", true);
        }
    }
    [Theory] [InlineData(false, "concurrent")] [InlineData(true, "concurrent")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "parentChanged")] [InlineData(true, "parentChanged")]
    public async Task Tenant_Sandbox_new_generation_and_command_start_are_distinct_atomic_one_time_fences(bool sqlite, string outcome)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-start-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
            var services = SandboxApprovalServices(f, s); var storage = (ITenantSandboxRuntimeStore)f.Iam;
            var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                new("printf approved-once", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
            var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
            await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
            var frame = await ResumeSandboxApproval(f, s, services.Approvals);
            var reservations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => services.Execution.ReserveAsync(frame, request.RequestId).AsTask()));
            var winner = Assert.Single(reservations, r => r.Reserved); Assert.All(reservations, r => Assert.Equal(winner.Execution.Id, r.Execution.Id));
            var saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.Equal(SandboxLeaseState.Provisioning, saved.Lease!.State); Assert.Equal(1, saved.Lease.Revision);
            Assert.Null(saved.Execution!.DeadlineAt); Assert.Null(saved.Execution.CommandStartedAt);
            await Assert.ThrowsAsync<SecurityConflictException>(() => services.Execution.StartCommandAsync(frame, winner.Execution.Id).AsTask());
            var now = DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<ArgumentException>(() => storage.TrySaveTenantSandboxExecutionFactsAsync(s.Tenant,
                saved.Execution with { Revision = 2, CommandStartedAt = now, DeadlineAt = now.AddSeconds(30) }, 1).AsTask());
            var options = new OpenSandboxOptions { CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            var mount = new CanonicalSandboxMountResolver(options).Resolve(frame.Owner.AgentInstanceId, frame.SessionId, request.Operation.SandboxCommand!.HomeAccess, create: true);
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved.Lease with { Revision = 2, MountAuthority = mount.Authority,
                InitializedAt = now, Initialization = new("Succeeded", saved.Lease.Profile.Id, saved.Lease.Profile.Revision, saved.Lease.Profile.InstanceRevision, now, now, 0) }, 1));
            await Assert.ThrowsAsync<SecurityConflictException>(() => services.Execution.StartCommandAsync(frame, winner.Execution.Id).AsTask()); // No linked provider yet.
            saved = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, saved.Lease! with { Revision = 3, ProviderId = "controlled-start-handle" }, 2));
            if (outcome == "revoked") await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            if (outcome == "parentChanged")
            {
                var agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant, frame.Owner.AgentInstanceId))!;
                await ConfigurationServices(f).Configuration.UpdatePersonaAsync(s.Human.PrincipalId, s.Tenant, frame.Owner.AgentInstanceId,
                    agent.Instance.Persona with { Tone = "Changed before command start" }, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, agent.Instance.Revision, agent.Instance.PersonaRevision);
            }
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => {
                try { return await services.Execution.StartCommandAsync(frame, winner.Execution.Id); }
                catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { return null; }
            }));
            Assert.Equal(outcome == "concurrent" ? 1 : 0, attempts.Count(e => e is not null));
            var after = (await storage.ReadTenantSandboxRuntimeAsync(s.Tenant, frame.SessionId))!;
            Assert.Equal(outcome == "concurrent" ? SandboxLeaseState.Executing : SandboxLeaseState.Provisioning, after.Lease!.State);
            Assert.Equal(outcome == "concurrent", after.Execution!.CommandStartedAt is not null);
            Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == "sandbox.command.reserve");
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
