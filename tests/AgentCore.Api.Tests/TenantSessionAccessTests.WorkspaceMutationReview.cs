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
using AgentCore.Api.Security;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(true, "restart")]
    [InlineData(true, "unsettled-review")] [InlineData(true, "unsettled-grant")]
    public async Task Tenant_workspace_mutation_Review_model_HTTP_prepares_inspects_approves_and_continues_exactly_once(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-mutation-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            var unsettled = mode.StartsWith("unsettled-", StringComparison.Ordinal);
            var exceptional = mode is "grant" or "unsettled-grant";
            var proof = !sqlite && mode == "review" && System.Environment.GetEnvironmentVariable("AGENTCORE_WORKSPACE_MUTATION_UI_PROOF") == "1";
            const string proofPath = "/private/tmp/agent-core-workspace-mutation-ui";
            var path = proof ? "/working/reviewed-multilingual-workspace-change-with-a-complete-long-name-thử-nghiệm-試験-2026.txt" : "/working/reviewed.txt";
            var content = proof ? "# Exact proposed replacement\n<script>literal text only</script>\nUTF-8: thử nghiệm and 試験\nReview the entire saved text before approving.\n" + string.Join("\n", Enumerable.Range(1, 18).Select(i => $"Exact reviewed line {i}: preserve this supplied literal text.")) + "\nEnd of exact proposed content." : "Exact reviewed model mutation: thử nghiệm";
            var model = new WorkspaceMutationReviewModel(path, content);
            var writes = 0; var injectLoss = false;
            void Configure(IServiceCollection services)
            {
                if (unsettled) services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceOperationStore>(p => new UnavailableHostedWorkspaceSettlement((ITenantWorkspaceOperationStore)p.GetRequiredService<IIamStore>())));
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5179"];
                if (mode == "unknown") services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p => new ReviewedWorkspaceAcknowledgement((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(), () => injectLoss, () => Interlocked.Increment(ref writes))));
            }
            await using WebApplicationFactory<Program> initial = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            WebApplicationFactory<Program> factory = initial;
            if (proof) factory.UseKestrel(0);
            using var firstOwner = factory.CreateClient(); HttpClient owner = firstOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = api + $"/sessions/{session}/workspace";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Private existing source"u8.ToArray()))).StatusCode);
            injectLoss = mode == "unknown";
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact model write", exceptional ? ["agents.read", "sessions.read"] : ["agents.read", "sessions.read", "workspaces.write"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.write", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Apply only the exact reviewed workspace change", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var source = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, sent.GetProperty("runId").GetGuid(), true);
            Assert.Equal(AgentRunStatus.WaitingForApproval, source.Status);
            if (mode == "restart")
            {
                await factory.DisposeAsync(); model = new WorkspaceMutationReviewModel(path, content);
                recreated = new NativeTenantSqliteFactory(root, model); recreatedOwner = recreated.CreateClient(); factory = recreated; owner = recreatedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(AgentRunStatus.WaitingForApproval, (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, source.AgentRunId))!.Status);
            }
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, source.ApprovalRequestId!.Value))!;
            Assert.Equal(TenantOperationKind.WorkspaceMutation, request.Operation.Kind); Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("mutate_workspace", detail.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.DoesNotContain("Private existing source", detail.GetRawText()); Assert.DoesNotContain(request.Operation.WorkspaceMutation!.ObservationHash, detail.GetRawText());
            var conversation = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals"))).GetProperty("items").EnumerateArray());
            Assert.Equal("mutate_workspace", conversation.GetProperty("operation").GetString());
            if (mode == "stale")
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Concurrent source replacement"u8.ToArray()))).StatusCode);
                detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
                Assert.Equal("workspace_source_changed", detail.GetProperty("review").GetProperty("decisionUnavailableReason").GetString());
            }
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, instance, session, path, requestId = request.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-decided")) await Task.Delay(100, deadline.Token);
            }
            else if (mode == "stale") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            else Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, source.AgentRunId, false);
            if (mode == "unknown" || unsettled) { Assert.Equal(AgentRunStatus.Failed, done.Status); Assert.Equal("side-effect-indeterminate", done.Failure!.Code); Assert.Contains("not replayed", done.Failure.Summary); }
            else Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.NotEqual(source.AgentRunId, done.AgentRunId);
            if (!unsettled)
            {
                var result = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).Text;
                using var resultJson = JsonDocument.Parse(result); var outcome = resultJson.RootElement;
                if (mode == "unknown") { Assert.Equal("indeterminate", outcome.GetProperty("outcome").GetString()); Assert.Equal(JsonValueKind.Null, outcome.GetProperty("applied").ValueKind); Assert.Equal(1, writes); }
                else Assert.Equal(mode is not ("reject" or "stale"), outcome.GetProperty("applied").GetBoolean());
                Assert.Contains("\"replayAllowed\":false", result); Assert.DoesNotContain(content, result);
            }
            Assert.Equal(mode == "reject" ? "Private existing source" : mode == "stale" ? "Concurrent source replacement" : content,
                Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path))));
            Assert.Equal(mode is "reject" or "stale" ? 0 : 1, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
            var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
            Assert.Equal(exceptional, grant is not null); if (grant is not null) Assert.Equal(unsettled ? OperationGrantStatus.Claimed : OperationGrantStatus.Consumed, grant.Status);
            if (unsettled)
            {
                var receipts = factory.Services.GetRequiredService<ITenantApprovedOperationStore>();
                Assert.Null(await receipts.FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
                var assignment = Assert.Single(state.Assignments, a => a.PrincipalId == executor);
                await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, state.Tenant.Revision);
                // Stop the complete host with the native effect retained but its receipt unavailable.
                await factory.DisposeAsync();
                var replayModel = new WorkspaceMutationReviewModel(path, content); var reopenedWrites = 0;
                recreated = new NativeTenantSqliteFactory(root, replayModel, services => services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p =>
                    new ReviewedWorkspaceAcknowledgement((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(), () => true, () => { }, () => Interlocked.Increment(ref reopenedWrites)))));
                recreatedOwner = recreated.CreateClient(); factory = recreated; owner = recreatedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                TenantOperationReceipt? recovered = null;
                using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (recovered is null)
                {
                    recovered = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId, recoveryDeadline.Token);
                    if (recovered is null) await Task.Delay(50, recoveryDeadline.Token);
                }
                Assert.Equal(TenantOperationOutcome.Indeterminate, recovered.Outcome); Assert.Equal(request.RequestId, recovered.RequestId);
                Assert.Equal(done.AgentRunId, recovered.RunId); Assert.Equal(request.Operation.ActionHash, recovered.ActionHash);
                Assert.Equal(0, reopenedWrites); Assert.Empty(replayModel.Requests);
                Assert.Equal(content, Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path))));
                if (exceptional) Assert.Equal(OperationGrantStatus.Indeterminate, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId))!.Status);
                var recoveredAudit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
                Assert.Single(recoveredAudit, a => a.Action == TenantWorkspaceEffectRules.Dispatch); Assert.Single(recoveredAudit, a => a.Action == TenantWorkspaceEffectRules.Result);
                Assert.Equal(0, await factory.Services.GetRequiredService<TenantWorkspaceMutationEffectService>().RecoverAsync());
                Assert.Equal(recovered, await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
            }
            if (proof)
            {
                await File.WriteAllTextAsync(proofPath + "-completed.json", "{}");
                model.Batch = true;
                var secondSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var secondWorkspace = api + $"/sessions/{secondSession}/workspace";
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(secondWorkspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Private batch source"u8.ToArray()))).StatusCode);
                var secondSent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{secondSession}/messages", new { messageId = Guid.NewGuid(), text = "Review exact ordered workspace changes", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var secondWaiting = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, secondSent.GetProperty("runId").GetGuid(), true);
                var secondRequest = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, secondWaiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ApprovalKind.ExceptionalGrant, secondRequest.Kind);
                await File.WriteAllTextAsync(proofPath + "-batch.json", JsonSerializer.Serialize(new { tenant, instance, session = secondSession, path, requestId = secondRequest.RequestId }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                while (!File.Exists(proofPath + "-batch-reviewed")) await Task.Delay(100, deadline.Token);
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(secondWorkspace + $"/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Changed batch source"u8.ToArray()))).StatusCode);
                await File.WriteAllTextAsync(proofPath + "-stale.json", "{}");
                while (!File.Exists(proofPath + "-stale-cancelled")) await Task.Delay(100, deadline.Token);
                var secondDone = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, secondWaiting.AgentRunId, false);
                Assert.Equal(AgentRunStatus.Completed, secondDone.Status);
                await File.WriteAllTextAsync(proofPath + "-finished.json", "{}");
                while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
            }
            if (sqlite && mode is "restart" or "unknown")
            {
                var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId); Assert.NotNull(receipt);
                await factory.DisposeAsync(); recreatedOwner?.Dispose();
                recreated = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()); recreatedOwner = recreated.CreateClient(); factory = recreated; owner = recreatedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(receipt, await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                Assert.Equal(0, await factory.Services.GetRequiredService<TenantWorkspaceMutationEffectService>().RecoverAsync());
                Assert.Equal(content, Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + "/content?path=" + Uri.EscapeDataString(path))));
                Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == TenantWorkspaceEffectRules.Dispatch);
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class ReviewedWorkspaceAcknowledgement(ITenantWorkspaceMutationStore inner, Func<bool> inject, Action wrote, Action? entered = null) : ITenantWorkspaceMutationStore
    {
        public ValueTask<bool> TryWriteNewTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TryWriteNewTenantAsync(owner, path, bytes, authorizeCommit, ct);
        public ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create, Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TransformOfficeTenantAsync(owner, path, create, process, authorizeCommit, ct);
        public async ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> check, CancellationToken ct = default)
        { entered?.Invoke(); await inner.WriteTenantAsync(owner, path, bytes, check, ct); if (inject()) { wrote(); throw new IOException("Private native acknowledgement lost"); } }
        public ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.DeleteTenantAsync(owner, path, check, ct);
        public ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.PatchTenantAsync(owner, path, edits, check, ct);
        public ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.StructureTenantAsync(owner, operations, check, ct);
        public ValueTask CopyTenantAsync(TenantWorkspaceReference source, string sourcePath, TenantWorkspaceReference destination, string destinationPath, Func<CancellationToken, ValueTask> check, CancellationToken ct = default) => inner.CopyTenantAsync(source, sourcePath, destination, destinationPath, check, ct);
    }
    private sealed class WorkspaceMutationReviewModel(string path, string content) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(); public ModelCapabilities Capabilities => inner.Capabilities;
        public bool Batch { get; set; }
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("mutate_workspace"));
                yield return new ModelToolCallEvent(new("exact-workspace-consent", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_workspace",
                    mutation = new { tool = Batch ? ToolCatalog.WorkspaceBatch : ToolCatalog.WorkspaceWrite, arguments = Batch ? (object)new { operations = new object[] { new { op = "delete", path, recursive = true }, new { op = "mkdir", path = "/working/reviewed-new-folder" } } } : new { path, content } } })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
}
