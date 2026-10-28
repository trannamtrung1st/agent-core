using AgentCore.Infrastructure.Workspaces;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false, "allowed")] [InlineData(true, false, "allowed")]
    [InlineData(false, true, "allowed")] [InlineData(true, true, "allowed")]
    [InlineData(false, false, "missing")] [InlineData(true, true, "missing")]
    [InlineData(false, true, "review")] [InlineData(true, false, "review")]
    [InlineData(false, false, "unknown")] [InlineData(true, true, "unknown")]
    [InlineData(false, true, "unsettled")] [InlineData(true, false, "unsettled")]
    [InlineData(false, false, "invalid")] [InlineData(true, true, "invalid")]
    [InlineData(false, false, "home-disabled")] [InlineData(true, false, "home-disabled")]
    public async Task Tenant_workspace_model_mutations_HTTP_dispatch_once_under_current_policy_and_keep_body_free_results(bool sqlite, bool working, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-workspace-model-effects-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var logical = working ? "/working" : "/home";
            var model = new WorkspaceMutationRecordingModel(logical, mode == "allowed", mode == "invalid") { FirstRequestGate = mode == "home-disabled" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null }; var writes = 0;
            void Configure(IServiceCollection services)
            {
                if (mode == "unsettled") services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceOperationStore>(p => new UnavailableHostedWorkspaceSettlement((ITenantWorkspaceOperationStore)p.GetRequiredService<IIamStore>())));
                if (mode == "unknown") services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p =>
                    new LostWorkspaceAcknowledgement((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(), () => Interlocked.Increment(ref writes))));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace");
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current model mutation authority", mode == "missing" ? ["agents.read", "sessions.read", "workspaces.read"]
                : ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "workspaces.delete"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.write", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Perform the exact bounded workspace changes", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var runId = (await Json(sent)).GetProperty("runId").GetGuid();
            if (mode == "home-disabled")
            {
                await model.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var admitted = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
                Assert.Equal(AgentCore.Domain.Definitions.HomeAccess.ReadWrite, (admitted.Admission.Configuration!.Definition.StateIsolationPolicy ?? AgentCore.Domain.Definitions.StateIsolationPolicy.Default).HomeAccess);
                try { Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() })).StatusCode); }
                finally { model.FirstRequestGate!.TrySetResult(); }
            }
            var run = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, false);
            if (mode is "unknown" or "unsettled")
            {
                Assert.Equal(AgentRunStatus.Failed, run.Status); Assert.Equal("side-effect-indeterminate", run.Failure!.Code); if (mode == "unknown") Assert.Equal(1, writes);
                Assert.Contains("not replayed", run.Failure.Summary);
                var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
                var failed = history.GetProperty("items").EnumerateArray().Last();
                Assert.Equal("failed", failed.GetProperty("status").GetString());
                Assert.Equal("workspaceOutcomeUnknown", failed.GetProperty("failure").GetProperty("failureReason").GetString());
                Assert.Equal("", failed.GetProperty("text").GetString()); // Failure detail is separate from assistant text. Assert.DoesNotContain("private native", run.Failure.Summary);
                Assert.Equal(WorkspaceMutationRecordingModel.Original, Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(logical + "/source.txt"))));
            }
            else
            {
                Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
                var results = model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool && TenantWorkspaceMutationContract.IsMutation(m.Name ?? "")).ToArray();
                Assert.Equal(mode == "allowed" ? 6 : 1, results.Length);
                foreach (var message in results)
                {
                    using var doc = JsonDocument.Parse(message.Text); var data = doc.RootElement;
                    if (mode == "home-disabled") { Assert.Equal("forbidden", data.GetProperty("error").GetString()); Assert.Equal("home_access_disabled", data.GetProperty("reasonCode").GetString()); continue; }
                    if (mode == "invalid") { Assert.Equal("invalid", data.GetProperty("error").GetString()); Assert.Equal("invalid_arguments", data.GetProperty("reasonCode").GetString()); continue; }
                    var completed = mode == "allowed" && message.Name != ToolCatalog.WorkspaceDelete;
                    Assert.Equal(completed, data.GetProperty("applied").GetBoolean()); Assert.False(data.GetProperty("replayAllowed").GetBoolean());
                    Assert.Equal(completed ? "completed" : "notDispatched", data.GetProperty("outcome").GetString());
                    Assert.DoesNotContain(WorkspaceMutationRecordingModel.Original, message.Text); Assert.DoesNotContain("source.txt", message.Text);
                    if (completed) Assert.NotEqual(Guid.Empty, data.GetProperty("operationId").GetGuid());
                    else Assert.Equal("requiresApproval", data.GetProperty("decision").GetString());
                }
                if (mode == "allowed")
                {
                    foreach (var path in new[] { "/source.txt", "/tree/copy.txt", "/batch/copy.txt" })
                        Assert.Equal(WorkspaceMutationRecordingModel.Edited, Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(logical + path))));
                    Assert.Contains(model.Requests.First().Tools!, t => t.Name == ToolCatalog.WorkspaceBatch && t.Description.Contains("sole call"));
                }
                else Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(workspace + "/content?path=" + Uri.EscapeDataString(logical + "/source.txt"))).StatusCode);
            }
            var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
            Assert.Equal(mode == "allowed" ? 5 : mode is "unknown" or "unsettled" ? 1 : 0, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
            Assert.Equal(mode == "allowed" ? 5 : mode == "unknown" ? 1 : 0, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Result));
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (sqlite && mode == "allowed")
            {
                var receipt = await factory.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, runId, "mutation-0"); Assert.NotNull(receipt);
                await factory.DisposeAsync();
                await using var recreated = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()); using var reopened = recreated.CreateClient();
                await Login(reopened, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(receipt, await recreated.Services.GetRequiredService<ITenantDirectOperationStore>().FindDirectOperationReceiptAsync(tenant, runId, "mutation-0"));
                Assert.Equal(0, await recreated.Services.GetRequiredService<TenantWorkspaceMutationEffectService>().RecoverAsync());
                Assert.Equal(WorkspaceMutationRecordingModel.Edited, Encoding.UTF8.GetString(await reopened.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(logical + "/source.txt"))));
                Assert.Equal(5, (await recreated.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class WorkspaceMutationRecordingModel(string root, bool sequence, bool invalid = false) : ILanguageModel
    {
        public const string Original = "Private workspace body: thử nghiệm";
        public const string Edited = "Edited workspace body: thử nghiệm";
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource? FirstRequestGate { get; init; }
        public TaskCompletionSource FirstRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Requests.Count == 1 && FirstRequestGate is { } gate) { FirstRequestEntered.TrySetResult(); await gate.Task.WaitAsync(cancellationToken); }
            var count = request.Messages.Count(m => m.Role == ModelRole.Tool && TenantWorkspaceMutationContract.IsMutation(m.Name ?? ""));
            (string Tool, object Args)[] changes = [
                (ToolCatalog.WorkspaceWrite, new { path = invalid ? "/working" : root + "/source.txt", content = Original }),
                (ToolCatalog.WorkspacePatch, new { path = root + "/source.txt", edits = new[] { new { oldText = "Private", newText = "Edited" } } }),
                (ToolCatalog.WorkspaceMkdir, new { path = root + "/tree/empty" }),
                (ToolCatalog.WorkspaceCopy, new { source = root + "/source.txt", destination = root + "/tree/copy.txt" }),
                (ToolCatalog.WorkspaceBatch, new { operations = new object[] { new { op = "mkdir", path = root + "/batch" }, new { op = "copy", source = root + "/source.txt", destination = root + "/batch/copy.txt" } } }),
                (ToolCatalog.WorkspaceDelete, new { path = root + "/source.txt" }) ];
            if (count < (sequence ? changes.Length : 1))
            {
                Assert.Contains(request.Tools!, t => t.Name == changes[count].Tool);
                yield return new ModelToolCallEvent(new("mutation-" + count, changes[count].Tool, JsonSerializer.Serialize(changes[count].Args)));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }

    private sealed class UnavailableHostedWorkspaceSettlement(ITenantWorkspaceOperationStore inner) : ITenantWorkspaceOperationStore
    {
        public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenant, Guid run, string call, CancellationToken ct = default) => inner.FindDirectOperationReceiptAsync(tenant, run, call, ct);
        public ValueTask<TenantWorkspaceEffectStart?> TryStartWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantWorkspaceEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryStartWorkspaceEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantPublicDownloadReceipt? download = null) => ValueTask.FromResult<TenantOperationReceipt?>(null);
        public ValueTask<IReadOnlyList<TenantWorkspaceDispatch>> RecoverableWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableWorkspaceEffectsAsync(at, limit, ct);
        public ValueTask<TenantApprovedWorkspaceStart?> TryStartApprovedWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryStartApprovedWorkspaceEffectAsync(next, revision, commit, audit, ct);
        public ValueTask<TenantOperationReceipt?> TrySettleApprovedWorkspaceEffectAsync(TenantSecurityState next, long revision, TenantApprovedWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantPublicDownloadReceipt? download = null) => ValueTask.FromResult<TenantOperationReceipt?>(null);
        public ValueTask<IReadOnlyList<TenantApprovedWorkspaceDispatch>> RecoverableApprovedWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableApprovedWorkspaceEffectsAsync(at, limit, ct);
    }

    private sealed class LostWorkspaceAcknowledgement(ITenantWorkspaceMutationStore inner, Action wrote) : ITenantWorkspaceMutationStore
    {
        public ValueTask<bool> TryWriteNewTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TryWriteNewTenantAsync(owner, path, bytes, authorizeCommit, ct);
        public ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create, Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TransformOfficeTenantAsync(owner, path, create, process, authorizeCommit, ct);
        public async ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
        { await inner.WriteTenantAsync(owner, path, bytes, authorizeCommit, ct); wrote(); throw new IOException("private native acknowledgement lost"); }
        public ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.DeleteTenantAsync(owner, path, authorizeCommit, ct);
        public ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.PatchTenantAsync(owner, path, edits, authorizeCommit, ct);
        public ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.StructureTenantAsync(owner, operations, authorizeCommit, ct);
        public ValueTask CopyTenantAsync(TenantWorkspaceReference sourceOwner, string source, TenantWorkspaceReference destinationOwner, string destination, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.CopyTenantAsync(sourceOwner, source, destinationOwner, destination, authorizeCommit, ct);
    }
}
