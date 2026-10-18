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

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    private sealed class SandboxModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public string Arguments = "";
        public Func<ModelRequest, Task>? Seed;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (request.Context?.Sections.All(s => s.Id != AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true
                && request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("run_sandbox_command", StringComparison.Ordinal)) == true)
            {
                if (Seed is { } seed) { Seed = null; await seed(request); }
                yield return new ModelToolCallEvent(new("model-sandbox-request", ToolCatalog.RequestAuthorization, Arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    private sealed class SandboxModelFactory(string root, string endpoint) : AgentCoreApiFactory
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

    [Theory] [InlineData("approve")] [InlineData("reject")] [InlineData("cancel")] [InlineData("uncertain")] [InlineData("provision")] [InlineData("background")]
    public async Task Tenant_Sandbox_model_request_waits_for_exact_Review_then_shared_worker_delivers_bounded_result_once(string choice)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-sandbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            SandboxLease lease = null!; Guid tenant = default; var commands = 0; var contacts = 0; var creates = 0; var initializations = 0; Dictionary<string,string> metadata = []; var paths = new ConcurrentQueue<string>();
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); await using var peer = builder.Build(); peer.Urls.Add("http://127.0.0.1:0");
            peer.Run(async http => {
                contacts++; Assert.Equal("synthetic-model-command", http.Request.Headers["OPEN-SANDBOX-API-KEY"]);
                var path = http.Request.Path.Value!; paths.Enqueue(http.Request.Method + " " + path);
                if (path == "/agent-core/generations/capabilities") await http.Response.WriteAsJsonAsync(new { contractVersion = 1, retirementFencing = true });
                else if (path == "/v1/sandboxes" && http.Request.Method == "POST")
                {
                    creates++; using var body = await JsonDocument.ParseAsync(http.Request.Body);
                    Assert.Equal("sha256:" + new string('a', 64), body.RootElement.GetProperty("image").GetProperty("uri").GetString());
                    metadata = body.RootElement.GetProperty("metadata").Deserialize<Dictionary<string,string>>()!;
                    await http.Response.WriteAsJsonAsync(new { id = "model-owned", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow, metadata });
                }
                else if (path.StartsWith("/agent-core/mounts/", StringComparison.Ordinal))
                {
                    using var body = await JsonDocument.ParseAsync(http.Request.Body); var value = body.RootElement;
                    Assert.Equal(metadata["agent-core-generation"], value.GetProperty("ownerValue").GetString());
                    await http.Response.WriteAsJsonAsync(new { contractVersion = 4, runtimeUser = value.GetProperty("uid").GetInt32() + ":" + value.GetProperty("gid").GetInt32(), topology = "linux", imageId = "fixture", imageUser = "fixture" });
                }
                else if (path.Contains("/endpoints/")) await http.Response.WriteAsJsonAsync(new { endpoint = new Uri(peer.Urls.Single()).Authority });
                else if (path == "/ping") await http.Response.WriteAsJsonAsync(new { status = "ok" });
                else if (path == "/v1/sandboxes/model-owned" && http.Request.Method == "GET")
                    await http.Response.WriteAsJsonAsync(new { id = "model-owned", entrypoint = new[] { "/bin/sh" }, status = new { state = "Running" }, createdAt = DateTimeOffset.UtcNow,
                        metadata = metadata.Count > 0 ? metadata : new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N"), ["agent-core-tenant"] = tenant.ToString("N"),
                            ["agent-core-session"] = lease.SessionId.ToString("N"), ["agent-core-executor"] = lease.ExecutorPrincipalId!.Value.ToString("N") } });
                else if (path.EndsWith("/renew-expiration")) await http.Response.WriteAsJsonAsync(new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
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
            await peer.StartAsync(); await using var factory = new SandboxModelFactory(root, peer.Urls.Single());
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
                Environment = new(Capabilities: new("Selected", [ToolCatalog.SandboxExec])) }, await Revision(owner, tenant), draft.Revision);
            await definitions.PublishAsync(actor, tenant, draft.DraftId, await Revision(owner, tenant), draft.Revision);
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = Guid.Parse(draft.DefinitionId), version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = created.GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            foreach (var grant in new[] { ("sandbox.execute", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))), ("sandbox.read", TenantSandboxProfileRules.Scope(tenant, profileId)) })
            {
                var role = await access.SaveRoleAsync(actor, tenant, null, grant.Item1, [grant.Item1], await Revision(owner, tenant));
                await access.AssignRoleAsync(actor, tenant, executor, role.RoleId, grant.Item2, await Revision(owner, tenant));
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var storage = factory.Services.GetRequiredService<ITenantSandboxRuntimeStore>();
            factory.Model.Arguments = JsonSerializer.Serialize(new { operation = "run_sandbox_command", profileId, command = new { command = "printf model-command", cwd = "/working", timeoutSeconds = 30, background = choice == "background" } });
            if (choice != "provision") factory.Model.Seed = async request => {
                var candidates = new List<AgentRun>();
                foreach (var id in await runs.ListTenantRunIdsAsync(tenant, session))
                    if (await runs.FindTenantRunAsync(tenant, id) is { } candidate) candidates.Add(candidate);
                var run = Assert.Single(candidates, r => r.ResponseId == request.ResponseId && r.Status == AgentRunStatus.Running);
                var pinned = Assert.Single(run.Admission.Configuration!.Definition.ExecutionSandboxProfiles!);
                var options = factory.Services.GetRequiredService<OpenSandboxOptions>();
                var mounts = new CanonicalSandboxMountResolver(options).Resolve(instance, session, run.Admission.Configuration.Definition.StateIsolationPolicy?.HomeAccess ?? AgentCore.Domain.Definitions.HomeAccess.ReadWrite, create: true);
                var at = DateTimeOffset.UtcNow;
                lease = new(session, instance, Guid.NewGuid(), 1, pinned, SandboxLeaseState.Provisioning, null, at, at.AddMinutes(10), MountAuthority: mounts.Authority,
                    AdmissionRunId: run.AgentRunId, AdmissionToolCallId: "seed-existing", TenantId: tenant, ExecutorPrincipalId: executor,
                    ProviderHostScope: factory.Services.GetRequiredService<ISandboxVerificationHost>().Scope);
                Assert.True(await storage.TryReserveTenantSandboxLeaseAsync(new(new(run.Owner, session, run.AgentRunId, run.Claim!.Generation), await Revision(owner, tenant), run.Revision), lease, 0, at));
                Assert.True(await storage.TrySaveTenantSandboxLeaseFactsAsync(tenant, lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "model-owned", BaselineProcesses = [] }, 1));
            };
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Run the exact sandbox command after Review", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runId = submitted.GetProperty("runId").GetGuid();
            async Task<AgentRun> Await(Func<AgentRun, bool> done)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    var current = (await runs.FindTenantRunAsync(tenant, runId))!;
                    if (current.Result?.ContinuationRunId is { } next) { runId = next; continue; }
                    if (done(current) || current.IsTerminal) return current;
                    await Task.Delay(25, deadline.Token);
                }
            }
            var waiting = await Await(r => r.Status == AgentRunStatus.WaitingForApproval);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}"); Assert.Null(waiting.Claim);
            Assert.Equal(0, commands); Assert.Equal(0, contacts);
            var capacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.SessionHost>(); var released = false;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (!deadline.IsCancellationRequested)
                {
                    if (capacity.TryReserveTenantRuntime()) { released = true; capacity.ReleaseTenantRuntime(); break; }
                    await Task.Delay(25, deadline.Token);
                }
            Assert.True(released, "The approval wait must release the sole Runtime slot.");
            var approval = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, approval.Kind); Assert.Equal(TenantOperationKind.SandboxCommand, approval.Operation.Kind);
            var route = api + $"/approvals/{approval.RequestId}";
            var detail = await Json(await owner.GetAsync(route)); Assert.Equal("run_sandbox_command", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + (choice == "cancel" ? "/cancel" : "/decision"), new { decision = choice == "reject" ? "reject" : "approve", actionHash = approval.Operation.ActionHash,
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
            if (choice is "approve" or "uncertain" or "provision" or "background")
            {
                Assert.Equal(1, commands); var observed = payload.RootElement.GetProperty("sandbox");
                Assert.Equal(choice == "uncertain" ? "Indeterminate" : choice == "background" ? "Running" : "Completed", observed.GetProperty("status").GetString());
                if (choice is "approve" or "provision") Assert.Equal("controlled model output", observed.GetProperty("stdout").GetString());
                Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("applied").ValueKind);
                Assert.Equal(choice == "background", observed.GetProperty("combinedLogs").GetBoolean());
                if (choice == "background") Assert.Equal("sandbox_command_started", payload.RootElement.GetProperty("reasonCode").GetString());
                if (choice == "uncertain") Assert.All(factory.Model.Requests.Where(r => r.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome) == true), r => Assert.All(r.Tools ?? [], t => Assert.Equal("agent_core_respond", t.Name)));
                Assert.Contains(factory.Model.Requests, r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text == result.Text));
            }
            else { Assert.Equal(0, commands); Assert.Equal(0, contacts); Assert.False(payload.RootElement.GetProperty("applied").GetBoolean()); }
            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "approvals.request");
            Assert.DoesNotContain(AgentRunToolCallCheckpoint.PendingCalls(messages!), c => c.Name == ToolCatalog.RequestAuthorization);
            await peer.StopAsync();
        }
        finally { if (Directory.Exists(root)) CanonicalWorkspaceFilesystem.PurgeOwner(root, default); }
    }
}
