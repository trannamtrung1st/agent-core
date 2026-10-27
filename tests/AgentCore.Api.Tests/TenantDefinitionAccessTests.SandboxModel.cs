using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    private sealed class SandboxModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public string Arguments = "";
        public Func<ModelRequest, Task>? Seed;
        public bool Inspect;
        public bool BackgroundTask;
        public Guid TargetInstanceId;
        public Func<Task>? BeforeProcessInspection;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ConcurrentQueue<string> Failures { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (BackgroundTask && request.Context?.Sections.All(s => s.Id != AgentCore.Application.Context.ContextSectionId.CoreBackground) == true)
            {
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == "tasks.delegate"))
                {
                    Assert.Contains(request.Tools!, t => t.Name == "tasks.delegate");
                    yield return new ModelToolCallEvent(new("model-background-start", "tasks.delegate",
                        JsonSerializer.Serialize(new { objective = "Perform the exact native Sandbox task", title = "Native Sandbox task", reportOnCompletion = false, target = new { instanceId = TargetInstanceId }, requestKey = "native-sandbox-task-once" })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                yield return new ModelSemanticResponseReady(new("Native Sandbox task started.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            bool HasRead(string name) => request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == name && !m.Text.Contains("\"error\":", StringComparison.Ordinal));
            if (Inspect && request.Context?.Sections.All(s => s.Id != AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true)
            {
                var name = !HasRead(ToolCatalog.SandboxProfiles) ? ToolCatalog.SandboxProfiles : !HasRead(ToolCatalog.SandboxStatus) ? ToolCatalog.SandboxStatus : null;
                if (name is not null)
                {
                    Assert.Contains(request.Tools ?? [], t => t.Name == name);
                    if (Seed is { } seed) { Seed = null; try { await seed(request); } catch (Exception error) { Failures.Enqueue(error.ToString()); throw; } }
                    yield return new ModelToolCallEvent(new("model-" + name, name, "{}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
            }
            if (Inspect && request.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true && !HasRead(ToolCatalog.SandboxProcessStatus))
            {
                Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.SandboxProcessStatus);
                var recorded = request.Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                using var result = JsonDocument.Parse(recorded.Text);
                var executionId = result.RootElement.GetProperty("sandbox").GetProperty("executionId").GetGuid();
                if (BeforeProcessInspection is { } before) { BeforeProcessInspection = null; await before(); }
                yield return new ModelToolCallEvent(new("model-process-inspection", ToolCatalog.SandboxProcessStatus, JsonSerializer.Serialize(new { executionId })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (request.Context?.Sections.All(s => s.Id != AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true
                && request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("run_sandbox_command", StringComparison.Ordinal)) == true)
            {
                if (Seed is { } seed) { Seed = null; try { await seed(request); } catch (Exception error) { Failures.Enqueue(error.ToString()); throw; } }
                yield return new ModelToolCallEvent(new("model-sandbox-request", ToolCatalog.RequestAuthorization, Arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (BackgroundTask && request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true)
            {
                yield return new ModelToolCallEvent(new("model-background-complete", ToolCatalog.WorkComplete,
                    """{"outcome":"Response","summary":"Native Sandbox background task finished.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    private sealed class SandboxModelFactory(string root, string endpoint, TimeProvider? clock = null) : AgentCoreApiFactory
    {
        public SandboxModel Model { get; } = new();
        protected override bool EnableTenantRunExecution => true;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> {
            ["OpenSandbox:Enabled"] = "true", ["OpenSandbox:Endpoint"] = endpoint, ["OpenSandbox:ApiKey"] = "synthetic-model-command",
            ["OpenSandbox:AllowLocalImageIds"] = "true", ["OpenSandbox:WorkspaceDockerHostRoot"] = root, ["Persistence:WorkspaceRoot"] = root };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }
                services.AddSingleton<SetupVerification>();
                services.Replace(ServiceDescriptor.Singleton<ITenantSandboxVerifier>(p => p.GetRequiredService<SetupVerification>()));
                services.Replace(ServiceDescriptor.Singleton<ISandboxReadiness, SetupReady>());
                services.Replace(ServiceDescriptor.Singleton<ILanguageModel>(Model));
                // This journey owns foreground SDK work; avoid an unrelated periodic cleanup racing its seeded idle lease.
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                    && d.ImplementationType == typeof(AgentCore.Api.OpenSandboxHostedService)).ToArray()) services.Remove(hosted);
            });
        }
    }

    [Theory] [InlineData("approve")] [InlineData("reject")] [InlineData("cancel")] [InlineData("uncertain")] [InlineData("provision")] [InlineData("background")] [InlineData("inspect")] [InlineData("stop")] [InlineData("reset")] [InlineData("reviewProfiles")] [InlineData("reviewStatus")] [InlineData("reviewProcess")] [InlineData("reviewProcessReject")]
    public Task Tenant_Sandbox_model_request_waits_for_exact_Review_then_shared_worker_delivers_bounded_result_once(string choice) => SandboxModelFlow(choice);

    [Theory] [InlineData("approve")] [InlineData("reject")] [InlineData("cancel")] [InlineData("uncertain")] [InlineData("provision")]
    [InlineData("inspect")] [InlineData("stop")] [InlineData("reset")] [InlineData("reviewProfiles")] [InlineData("reviewStatus")]
    [InlineData("reviewProcess")] [InlineData("reviewProcessReject")]
    public Task Tenant_background_native_Sandbox_preserves_command_read_control_and_uncertain_receipts(string choice) => SandboxModelFlow(choice, backgroundTask: true);

    [Theory] [InlineData("scheduled", "approve")] [InlineData("manual", "approve")] [InlineData("scheduled", "uncertain")]
    [InlineData("scheduled", "stop")] [InlineData("scheduled", "reset")] [InlineData("scheduled", "reviewProfiles")]
    [InlineData("scheduled", "reviewProcess")] [InlineData("manual", "reviewProcessReject")]
    public Task Independent_Automation_Sandbox_preserves_actual_command_observation_control_and_uncertain_receipts(string origin, string choice)
        => SandboxModelFlow(choice, independentOrigin: origin);

    private async Task SandboxModelFlow(string choice, bool backgroundTask = false, string? independentOrigin = null)
    {
        backgroundTask |= independentOrigin is not null;
        TimeProvider proofClock = independentOrigin is null ? TimeProvider.System : new FakeTimeProvider(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-sandbox-" + Guid.NewGuid().ToString("N"));
        var control = choice is "stop" or "reset";
        SandboxReadKind? readKind = choice switch { "reviewProfiles" => SandboxReadKind.Profiles, "reviewStatus" => SandboxReadKind.Status,
            "reviewProcess" or "reviewProcessReject" => SandboxReadKind.Process, _ => null };
        try
        {
            SandboxLease lease = null!; Guid tenant = default; var commands = 0; var contacts = 0; var creates = 0; var initializations = 0; Dictionary<string,string> metadata = []; var paths = new ConcurrentQueue<string>(); var contactsAtInspection = -1;
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var peer = builder.Build(); peer.Urls.Add("http://127.0.0.1:0");
            peer.Run(async http => {
                contacts++; Assert.Equal("synthetic-model-command", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var path = http.Request.Path.Value!; paths.Enqueue(http.Request.Method + " " + path);
                if (path == "/agent-core/generations/capabilities") await http.Response.WriteAsJsonAsync(new { contractVersion = 1, retirementFencing = true });
                else if (path == "/agent-core/generations/retire")
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                    Assert.Equal(tenant.ToString("N"), value.GetProperty("tenant").GetString()); Assert.Equal(lease.Generation.ToString("N"), value.GetProperty("generation").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 1, tenant = tenant.ToString("N"), session = lease.SessionId.ToString("N"),
                        executor = lease.ExecutorPrincipalId!.Value.ToString("N"), generation = lease.Generation.ToString("N"), retired = true });
                }
                else if (path == "/v1/sandboxes" && http.Request.Method == "POST")
                {
                    creates++; using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    Assert.Equal("sha256:" + new string('a', 64), body.RootElement.GetProperty("image").GetProperty("uri").GetString());
                    metadata = body.RootElement.GetProperty("metadata").Deserialize<Dictionary<string,string>>()!;
                    await http.Response.WriteAsJsonAsync(new { id = "model-owned", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = proofClock.GetUtcNow(), metadata });
                }
                else if (path.StartsWith("/agent-core/mounts/", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                    Assert.Equal(metadata["agent-core-generation"], value.GetProperty("ownerValue").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = AgentCore.Domain.Conversation.SandboxProfileVerification.CurrentContractVersion, runtimeUser = value.GetProperty("uid").GetInt32() + ":" + value.GetProperty("gid").GetInt32(), topology = "linux", imageId = "fixture", imageUser = "fixture" });
                }
                else if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(peer.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (path == "/v1/sandboxes/model-owned" && http.Request.Method == "GET")
                    await http.Response.WriteAsJsonAsync(new { id = "model-owned", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = proofClock.GetUtcNow(),
                        metadata = metadata.Count > 0 ? metadata : new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N"), ["agent-core-tenant"] = tenant.ToString("N"),
                            ["agent-core-session"] = lease.SessionId.ToString("N"), ["agent-core-executor"] = lease.ExecutorPrincipalId!.Value.ToString("N") } });
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = proofClock.GetUtcNow().AddMinutes(10) });
                else if (http.Request.Method == "DELETE" && path == "/v1/sandboxes/model-owned") http.Response.StatusCode = 204;
                else if (path == "/command")
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var argv = body.RootElement.GetProperty("argv").EnumerateArray().Select(v => v.GetString()).ToArray();
                    var user = argv.Contains("agent-core-command");
                    if (user)
                    {
                        if (argv.Last() == "printf profile-init") initializations++;
                        else { if (choice == "provision") Assert.Equal(1, initializations); commands++; Assert.Equal("printf model-command", argv.Last()); }
                    }
                    if (argv.Last()!.Contains("stage=process_isolation", StringComparison.Ordinal))
                        foreach (var directory in Directory.EnumerateDirectories(root, ".core-probe-*", SearchOption.AllDirectories))
                        {
                            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core"));
                            foreach (var name in new[] { "renamed", "hard", "symbolic" }) { var file = Path.Combine(directory, name); await File.WriteAllBytesAsync(file, bytes); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                        }
                    if (user && choice == "uncertain") { http.Response.StatusCode = 500; await http.Response.WriteAsync("PRIVATE_PROVIDER_BODY"); return; }
                    http.Response.ContentType = "text/event-stream";
                    await http.Response.WriteAsync("data: {\"type\":\"init\",\"text\":\"model-command\",\"timestamp\":0}\n\n");
                    if (user) await http.Response.WriteAsync("data: {\"type\":\"stdout\",\"text\":\"controlled model output\",\"timestamp\":1}\n\n");
                    await http.Response.WriteAsync("data: {\"type\":\"execution_complete\",\"execution_time\":5,\"timestamp\":2}\n\n");
                }
                else http.Response.StatusCode = 404;
            });
            await peer.StartAsync(); await using var factory = new SandboxModelFactory(root, peer.Urls.Single(), independentOrigin is null ? null : proofClock);
            factory.Model.BackgroundTask = backgroundTask;
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentCore.Api.Realtime.AgentCoreOptions>>().Value.MaxActiveSessions = 1;
            tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var profile = await Json(await owner.PostAsJsonAsync(api + "/sandbox-profiles", new AgentCore.Contracts.Http.TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0,
                "Model source", "Controlled SDK peer", true, (choice == "provision" ? "sha256:" : "ghcr.io/acme/tool@sha256:") + new string('a', 64), InitializationScript: choice == "provision" ? "printf profile-init" : null)));
            var profileId = profile.GetProperty("profile").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sandbox-profiles/{profileId}/verify", new AgentCore.Contracts.Http.TenantSandboxVerificationRequest(await Revision(owner, tenant), 1))).StatusCode);
            var definitions = factory.Services.GetRequiredService<TenantDefinitionService>();
            var draft = await definitions.CreateAsync(actor, tenant, "Model Sandbox command", await Revision(owner, tenant));
            draft = await definitions.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with { SandboxPolicy = new(profileId, [profileId]),
                TriggerPolicy = independentOrigin is null ? draft.Candidate.TriggerPolicy : (await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 35))!.TriggerPolicy,
                ExecutionPolicy = new(Projection: backgroundTask && independentOrigin is null ? new(["tasks.delegate"]) : null, Capabilities: new("Selected", (backgroundTask && independentOrigin is null ? new[] { "tasks.delegate" } : []).Concat(control ? [ToolCatalog.SandboxExec, ToolCatalog.SandboxProcessStop, ToolCatalog.SandboxReset] : choice == "inspect" || readKind is not null ? [ToolCatalog.SandboxExec, ToolCatalog.SandboxProfiles, ToolCatalog.SandboxStatus, ToolCatalog.SandboxProcessStatus] : new[] { ToolCatalog.SandboxExec }).ToArray())) }, await Revision(owner, tenant), draft.Revision);
            await definitions.PublishAsync(actor, tenant, draft.DraftId, await Revision(owner, tenant), draft.Revision);
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = Guid.Parse(draft.DefinitionId), version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = created.GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            foreach (var grant in new[] { ("sandbox.execute", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))), ("sandbox.read", TenantSandboxProfileRules.Scope(tenant, profileId)) })
            {
                var role = await access.SaveRoleAsync(actor, tenant, null, grant.Item1, [grant.Item1], await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, grant.Item2, await Revision(owner, tenant));
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var parentSession = session;
            factory.Model.TargetInstanceId = instance;
            if (backgroundTask && independentOrigin is null)
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/cooperation", new {
                    cooperation = new { acceptTasks = true, acceptDirectMessages = false, allowDirectMessageWake = false, maxConcurrentTasks = 2 },
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);

            if (backgroundTask)
            {
                var backgroundRole = await access.SaveRoleAsync(actor, tenant, null, "Background native execution", (new[] { "workspaces.read", "workspaces.write", "workspaces.delete", "tasks.delegate", "tasks.read", "agents.read", "spaces.read", "sessions.create", "agent-runs.create", "sessions.read", "agent-runs.read" }).Concat(independentOrigin is null ? [] : new[] { "automations.run" }).ToArray(), await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, executor, backgroundRole.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            }
            if (choice == "inspect" || control || readKind is not null)
            {
                foreach (var grant in new[] { ("sandbox.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))), ("sessions.read", backgroundTask ? AccessScope.ForTenant(tenant) : TenantSessionRules.Scope(tenant, session)) })
                {
                    var role = await access.SaveRoleAsync(actor, tenant, null, "Inspect " + grant.Item1, [grant.Item1], await Revision(owner, tenant));
                    await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, grant.Item2, await Revision(owner, tenant));
                }
                if (control)
                    foreach (var grant in new[] { ("sandbox.manage", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))), ("sessions.manage", backgroundTask ? AccessScope.ForTenant(tenant) : TenantSessionRules.Scope(tenant, session)) })
                    {
                        var role = await access.SaveRoleAsync(actor, tenant, null, "Control " + grant.Item1, [grant.Item1], await Revision(owner, tenant));
                        await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, grant.Item2, await Revision(owner, tenant));
                    }
                factory.Model.Inspect = choice == "inspect";
                factory.Model.BeforeProcessInspection = async () => { contactsAtInspection = contacts; await peer.StopAsync(); };
            }
            if (readKind is not null)
            {
                var policy = await owner.PutAsJsonAsync(api + "/operation-policies/sandbox.read", new { expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0,
                    mode = "always", independentReview = false });
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
            }
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var storage = factory.Services.GetRequiredService<ITenantSandboxRuntimeStore>();
            factory.Model.Arguments = JsonSerializer.Serialize(new { operation = "run_sandbox_command", profileId, command = new { command = "printf model-command", cwd = "/working", timeoutSeconds = 30, background = choice == "background" } });
            if (choice != "provision") factory.Model.Seed = async request => {
                var candidates = new List<AgentRun>();
                var owned = backgroundTask ? (await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Select(s => s.Owner.SessionId) : new[] { session };
                foreach (var ownedSession in owned)
                    foreach (var id in await runs.ListTenantRunIdsAsync(tenant, ownedSession))
                        if (await runs.FindTenantRunAsync(tenant, id) is { } candidate) candidates.Add(candidate);
                var run = Assert.Single(candidates, r => r.ResponseId == request.ResponseId && r.Status == AgentRunStatus.Running);
                session = run.SessionId;
                var pinned = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!);
                var options = factory.Services.GetRequiredService<OpenSandboxOptions>();
                var workspaceAccess = await TenantSandboxWorkspaceRules.ResolveAsync((await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!, run,
                    run.Admission.Configuration.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite,
                    factory.Services.GetRequiredService<IamAuthorizer>(), default);
                var mounts = new CanonicalSandboxMountResolver(options).Resolve(instance, session, workspaceAccess, create: true);
                var at = proofClock.GetUtcNow();
                lease = new(session, instance, Guid.NewGuid(), 1, pinned, SandboxLeaseState.Provisioning, null, at, at.AddMinutes(10), MountAuthority: mounts.Authority,
                    AdmissionRunId: run.AgentRunId, AdmissionToolCallId: "seed-existing", TenantId: tenant, ExecutorPrincipalId: executor,
                    ProviderHostScope: factory.Services.GetRequiredService<ISandboxVerificationHost>().Scope);
                Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(new(new(run.Owner, session, run.AgentRunId, run.Claim!.Generation), await Revision(owner, tenant), run.Revision), lease, 0, at));
                Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(tenant, lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "model-owned", BaselineProcesses = [] }, 1));
                if (readKind is { } kind)
                {
                    Guid? id = null;
                    if (kind == SandboxReadKind.Process)
                    {
                        id = Guid.NewGuid();
                        var execution = new SandboxExecution(id.Value, session, instance, lease.Generation, run.AgentRunId, run.Claim!.Generation, "seed-readable-process", new string('d', 64),
                            SandboxExecutionState.Dispatching, at, CommandText: "PRIVATE_ORIGINAL_COMMAND", DeadlineAt: at.AddMinutes(1), ProfileId: pinned.Id, ProfileRevision: pinned.Revision,
                            TenantId: tenant, ExecutorPrincipalId: executor, Revision: 1, Background: true);
                        Assert.True(await storage.TryReserveTenantSandboxExecutionAsync(new(new(run.Owner, session, run.AgentRunId, run.Claim!.Generation), await Revision(owner, tenant), run.Revision), execution, at));
                        Assert.True(await storage.TrySaveTenantSandboxExecutionFactsAsync(tenant, execution with { Revision = 2, State = SandboxExecutionState.Running, Stdout = "controlled stored observation", Phase = "execution" }, 1));
                    }
                    factory.Model.Arguments = kind == SandboxReadKind.Process ? JsonSerializer.Serialize(new { operation = TenantSandboxReadAuthorizationContract.Operation(kind), executionId = id })
                        : JsonSerializer.Serialize(new { operation = TenantSandboxReadAuthorizationContract.Operation(kind) });
                }
                if (control)
                {
                    Guid? executionId = null;
                    if (choice == "stop")
                    {
                        executionId = Guid.NewGuid();
                        var execution = new SandboxExecution(executionId.Value, session, instance, lease.Generation, run.AgentRunId, run.Claim!.Generation, "seed-process", new string('c', 64),
                            SandboxExecutionState.Dispatching, at, CommandText: "PRIVATE_ORIGINAL_COMMAND", DeadlineAt: at.AddMinutes(1), ProfileId: pinned.Id, ProfileRevision: pinned.Revision,
                            TenantId: tenant, ExecutorPrincipalId: executor, Revision: 1);
                        Assert.True(await storage.TryReserveTenantSandboxExecutionAsync(new(new(run.Owner, session, run.AgentRunId, run.Claim!.Generation), await Revision(owner, tenant), run.Revision), execution, at));
                    }
                    factory.Model.Arguments = choice == "stop" ? JsonSerializer.Serialize(new { operation = "stop_sandbox_process", executionId }) : "{\"operation\":\"reset_sandbox_environment\"}";
                }
            };
            Guid runId;
            if (independentOrigin is not null)
                runId = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, tenant, instance, "Perform the exact owned native Sandbox task.", (FakeTimeProvider)proofClock, independentOrigin);
            else
            {
                var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = readKind is null ? "Run the exact sandbox command after Review" : "Inspect the exact stored Sandbox observation after Review", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                runId = submitted.GetProperty("runId").GetGuid();
            }
            var independentRoot = independentOrigin is null ? (AgentRun?)null : await runs.FindTenantRunAsync(tenant, runId);
            if (backgroundTask && independentOrigin is null)
            {
                var parentId = runId; using var childDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    var child = (await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items.SingleOrDefault(s => s.Snapshot.Origin.OriginatingAgentRunId == parentId);
                    if (child?.Snapshot.Origin.InitialBackgroundAgentRunId is { } childId) { session = child.Owner.SessionId; runId = childId; break; }
                    var parentRun = (await runs.FindTenantRunAsync(tenant, parentId))!;
                    Assert.True(!parentRun.IsTerminal || parentRun.Status == AgentRunStatus.Completed, JsonSerializer.Serialize(new { parentRun.Status, parentRun.Failure, Requests = factory.Model.Requests.Select(r => new { tools = r.Tools?.Select(t => t.Name), messages = r.Messages.Select(m => new { m.Role, m.Name, m.Text }) }) }));
                    await Task.Delay(25, childDeadline.Token);
                }
            }
            async Task<AgentRun> Await(Func<AgentRun, bool> done)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    if (independentOrigin is not null)
                    {
                        await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    }
                    var current = (await runs.FindTenantRunAsync(tenant, runId))!;
                    if (current.Result?.ContinuationRunId is { } next) { runId = next; continue; }
                    if (done(current) || current.IsTerminal) return current;
                    await Task.Delay(25, deadline.Token);
                }
            }
            var waiting = await Await(r => r.Status == AgentRunStatus.WaitingForApproval);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}; {string.Join('\n', factory.Model.Failures)}"); Assert.Null(waiting.Claim);
            Assert.Equal(0, commands); Assert.Equal(0, contacts);
            var capacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantRuntimeCapacity>(); var released = false;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (!deadline.IsCancellationRequested)
                {
                    if (capacity.TryReserveTenantRuntime()) { released = true; capacity.ReleaseTenantRuntime(); break; }
                    await Task.Delay(25, deadline.Token);
                }
            Assert.True(released, "The approval wait must release the sole Runtime slot.");
            var approval = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, approval.Kind); Assert.Equal(readKind is not null ? TenantOperationKind.SandboxRead : control ? TenantOperationKind.SandboxControl : TenantOperationKind.SandboxCommand, approval.Operation.Kind);
            var route = api + $"/approvals/{approval.RequestId}";
            var detail = await Json(await owner.GetAsync(route)); Assert.Equal(readKind is { } reviewedKind ? TenantSandboxReadAuthorizationContract.Operation(reviewedKind) : control ? choice == "stop" ? "stop_sandbox_process" : "reset_sandbox_environment" : "run_sandbox_command", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            if (readKind is not null) await peer.StopAsync();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + (choice == "cancel" ? "/cancel" : "/decision"), new { decision = choice is "reject" or "reviewProcessReject" ? "reject" : "approve", actionHash = approval.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = approval.Revision })).StatusCode);
            var completed = await Await(r => r.IsTerminal);
            var facts = await storage.ReadTenantSandboxRuntimeAsync(tenant, session);
            Assert.True(completed.Status == (choice == "uncertain" ? AgentRunStatus.Failed : AgentRunStatus.Completed), JsonSerializer.Serialize(new { completed.Status, completed.Failure,
                phase = facts?.Execution?.Phase, category = facts?.Execution?.FailureCategory, environment = facts?.Lease?.State, message = facts?.Lease?.SafeMessage,
                initialization = facts?.Lease?.Initialization, commands, creates, initializations, paths }));
            if (choice == "uncertain") Assert.Equal("side-effect-indeterminate", completed.Failure!.Code);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
            var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == "model-sandbox-request");
            using var payload = JsonDocument.Parse(result.Text);
            Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", result.Text); Assert.DoesNotContain("model-owned", result.Text);
            Assert.Equal(choice == "provision" ? 1 : 0, creates); Assert.Equal(choice == "provision" ? 1 : 0, initializations);
            if (choice is "approve" or "uncertain" or "provision" or "background" or "inspect")
            {
                Assert.Equal(1, commands); var observed = payload.RootElement.GetProperty("sandbox");
                Assert.Equal(choice == "uncertain" ? "Indeterminate" : choice == "background" ? "Running" : "Completed", observed.GetProperty("status").GetString());
                if (choice is "approve" or "provision" or "inspect") Assert.Equal("controlled model output", observed.GetProperty("stdout").GetString());
                Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("applied").ValueKind);
                Assert.Equal(choice == "background", observed.GetProperty("combinedLogs").GetBoolean());
                if (choice == "background") Assert.Equal("sandbox_command_started", payload.RootElement.GetProperty("reasonCode").GetString());
                if (choice == "uncertain") Assert.All(factory.Model.Requests.Where(r => r.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true), r => Assert.All(r.Tools ?? [], t => Assert.Equal("agent_core_respond", t.Name)));
                Assert.Contains(factory.Model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result.Text));
            }
            else if (readKind is not null)
            {
                Assert.Equal(0, commands); Assert.Equal(0, contacts); Assert.False(payload.RootElement.GetProperty("applied").GetBoolean());
                Assert.Equal(choice != "reviewProcessReject", payload.RootElement.GetProperty("readAuthorized").GetBoolean());
                Assert.DoesNotContain("PRIVATE_ORIGINAL_COMMAND", result.Text);
                if (choice != "reviewProcessReject")
                {
                    Assert.False(payload.RootElement.GetProperty("inspectionUnavailable").GetBoolean()); var observed = payload.RootElement.GetProperty("inspection");
                    Assert.True(observed.GetProperty("observationOnly").GetBoolean()); Assert.False(observed.GetProperty("liveAuthorityConfirmed").GetBoolean());
                    if (readKind == SandboxReadKind.Profiles) Assert.Single(observed.GetProperty("profiles").EnumerateArray());
                    if (readKind == SandboxReadKind.Process) Assert.Equal("controlled stored observation", observed.GetProperty("execution").GetProperty("stdout").GetString());
                    if (readKind == SandboxReadKind.Status) Assert.Equal("Ready", observed.GetProperty("environment").GetProperty("storedState").GetString());
                }
                else { Assert.True(payload.RootElement.GetProperty("inspectionUnavailable").GetBoolean()); Assert.DoesNotContain("controlled stored observation", result.Text); }
                Assert.Contains(factory.Model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result.Text));
            }
            else if (control)
            {
                Assert.Equal(0, commands); Assert.Equal(0, contacts); Assert.True(payload.RootElement.GetProperty("controlRequestAccepted").GetBoolean());
                Assert.False(payload.RootElement.GetProperty("terminationConfirmed").GetBoolean()); Assert.True(payload.RootElement.GetProperty("filesRetained").GetBoolean());
                Assert.Equal(choice == "stop" ? "sandbox_stop_requested" : "sandbox_reset_requested", payload.RootElement.GetProperty("reasonCode").GetString());
                Assert.DoesNotContain("PRIVATE_ORIGINAL_COMMAND", result.Text);
                if (choice == "stop") { Assert.NotNull(facts!.Execution!.ProcessStop); Assert.False(facts.Execution.TerminationConfirmed); }
                else { Assert.Equal(SandboxLeaseState.Terminating, facts!.Lease!.State); Assert.Equal("reset", facts.Lease.CleanupReason); }
                Assert.Contains(factory.Model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result.Text));
                var receipts = factory.Services.GetRequiredService<ITenantApprovedOperationStore>();
                var receipt = await receipts.FindOperationReceiptAsync(tenant, approval.Operation.OperationId);
                // Accepted cleanup runs through the existing technical owner after the model Run has completed.
                await factory.Services.GetRequiredService<ISandboxService>().ReconcileAsync();
                var retired = (await storage.ReadTenantSandboxRuntimeAsync(tenant, session))!;
                Assert.Equal(SandboxLeaseState.Terminated, retired.Lease!.State);
                if (choice == "stop") Assert.True(retired.Execution!.TerminationConfirmed);
                Assert.Equal(receipt, await receipts.FindOperationReceiptAsync(tenant, approval.Operation.OperationId));
                Assert.Equal(0, commands); Assert.Equal(0, creates); Assert.Equal(0, initializations);
                var contactsAfterCleanup = contacts; await factory.Services.GetRequiredService<ISandboxService>().ReconcileAsync(); Assert.Equal(contactsAfterCleanup, contacts);
            }
            else { Assert.Equal(0, commands); Assert.Equal(0, contacts); Assert.False(payload.RootElement.GetProperty("applied").GetBoolean()); }
            if (choice == "inspect")
            {
                Assert.Equal(contactsAtInspection, contacts);
                foreach (var name in new[] { ToolCatalog.SandboxProfiles, ToolCatalog.SandboxStatus, ToolCatalog.SandboxProcessStatus })
                {
                    var observation = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.Name == name);
                    using var observed = JsonDocument.Parse(observation.Text);
                    Assert.True(observed.RootElement.GetProperty("observationOnly").GetBoolean()); Assert.False(observed.RootElement.GetProperty("liveAuthorityConfirmed").GetBoolean());
                    Assert.DoesNotContain("model-owned", observation.Text);
                    Assert.Contains(factory.Model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == observation.Text));
                    if (name == ToolCatalog.SandboxProcessStatus) Assert.Equal("controlled model output", observed.RootElement.GetProperty("execution").GetProperty("stdout").GetString());
                }
            }
            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "approvals.request");
            Assert.DoesNotContain(AgentRunToolCallCheckpoint.PendingCalls(messages!), c => c.Name == ToolCatalog.RequestAuthorization);
            if (independentRoot is not null)
            {
                Assert.NotEqual(parentSession, completed.SessionId); Assert.Equal(independentRoot.ActivationId, completed.Admission.ExecutionContext!.RootActivationId);
                Assert.Equal(independentOrigin == "scheduled" ? (Guid?)null : actor, completed.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
                Assert.Null(completed.Owner.ProfileIdOrNull); Assert.Null(await runs.FindTenantCompletionAsync(completed.Owner, completed.AgentRunId));
            }
            else if (backgroundTask)
            {
                Assert.NotEqual(parentSession, completed.SessionId); Assert.Equal(session, completed.SessionId);
                Assert.Equal(AgentRunOutputContract.BackgroundOutcome, completed.Admission.OutputContract);
                Assert.Null(await runs.FindTenantCompletionAsync(completed.Owner, completed.AgentRunId));
                await factory.Services.GetRequiredService<AgentCore.Application.Tasks.TenantTaskCoordinationService>().ReconcileAsync(16, CancellationToken.None);
                var tasks = await Json(await owner.GetAsync(api + $"/sessions/{parentSession}/tasks"));
                var task = Assert.Single(tasks.GetProperty("items").EnumerateArray());
                Assert.Equal(completed.AgentRunId, task.GetProperty("terminalRunId").GetGuid());
                Assert.NotEqual(parentSession, task.GetProperty("workerSessionId").GetGuid());
                if (choice is "approve" or "reviewProfiles" or "inspect")
                {
                    var taskId = task.GetProperty("taskId").GetGuid();
                    var before = await Json(await owner.GetAsync(api + $"/tasks/{taskId}"));
                    Assert.Equal("Succeeded", before.GetProperty("state").GetString()); Assert.Equal("Native Sandbox background task finished.", before.GetProperty("result").GetString());
                    var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                    var removedPermission = choice == "approve" ? "sandbox.execute" : "sandbox.read";
                    var assignments = state.Assignments.Where(a => a.PrincipalId == executor && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(removedPermission))).ToArray();
                    Assert.NotEmpty(assignments);
                    foreach (var assignment in assignments) await access.RevokeAssignmentAsync(actor, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                    var after = await Json(await owner.GetAsync(api + $"/tasks/{taskId}"));
                    Assert.Equal("Succeeded", after.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, after.GetProperty("result").ValueKind);
                    Assert.Equal(completed.AgentRunId, (await factory.Services.GetRequiredService<ITenantTaskStore>().FindTaskAsync(tenant, taskId))!.TerminalRunId);
                }

            }
            await peer.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }
}
