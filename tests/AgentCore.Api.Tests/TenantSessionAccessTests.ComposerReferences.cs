using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ComposerRecordingModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource? Release { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    [Theory] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "missing")] [InlineData(true, "missing")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    public async Task Tenant_Composer_reference_HTTP_admission_projects_bounded_body_only_with_independent_current_executor_read(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-composer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ComposerRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            const string path = "/home/reference.txt"; const string body = "Private Composer body → thử nghiệm";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes(body)))).StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Composer source read", mode == "missing" ? ["agents.read", "sessions.read"] : ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var choices = await Json(await owner.GetAsync(api + $"/agents/{instance}/composer?category=homeFile&search=reference"));
            var choice = Assert.Single(choices.GetProperty("items").EnumerateArray()); Assert.Equal(path, choice.GetProperty("reference").GetProperty("path").GetString()); Assert.DoesNotContain(body, choices.ToString());
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var text = "Inspect @homeFile:" + instance.ToString("D") + path;
            object[] parts = [new { kind = "text", text = "Inspect " }, new { kind = "reference", reference = new { kind = "homeFile", agentInstanceId = instance, path } }];
            var message = Guid.NewGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = message, text, parts, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var receipt = await Json(response); var runId = receipt.GetProperty("runId").GetGuid();
            var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            var evidence = Assert.Single(done.Admission.ComposerInput!.References);
            Assert.Equal(mode == "allowed" ? "valid" : mode == "review" ? "requiresApproval" : "forbidden", evidence.Status);
            var prompt = string.Join('\n', Assert.Single(model.Requests).Messages.Select(m => m.Text));
            if (mode == "allowed") { Assert.Equal(body, evidence.Text); Assert.Contains(body, prompt); }
            else { Assert.Empty(evidence.Text); Assert.DoesNotContain(body, prompt); }
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
            Assert.Equal(path, history.GetProperty("items")[0].GetProperty("parts")[1].GetProperty("reference").GetProperty("path").GetString());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = message, text, parts, expectedRevision = 1, expectedSessionRevision = 1 })).StatusCode);
            // Same fallback with changed typed revision/target is not an accepted-input replay.
            object[] changed = [new { kind = "text", text = text }];
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = message, text, parts = changed, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "mismatched", parts, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })).StatusCode);
            if (mode == "allowed")
            {
                var restricted = await FloorHuman(factory, owner, tenant, "composer-restricted-reader", ["agents.read", "agents.chat", "sessions.read", "sessions.create"]);
                using var reader = restricted.Client;
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/agents/{instance}/composer?category=homeFile")).StatusCode);
                var hiddenSkills = await Json(await reader.GetAsync(api + $"/agents/{instance}/composer?category=skill")); Assert.Empty(hiddenSkills.GetProperty("items").EnumerateArray());
                var hiddenHistory = await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"));
                Assert.Contains("Inspect [Resource unavailable]", hiddenHistory.GetProperty("items")[0].GetProperty("text").GetString());
                Assert.DoesNotContain(path, hiddenHistory.ToString());
                var currentRevision = (await Json(await reader.GetAsync(api + $"/sessions/{session}"))).GetProperty("revision").GetInt64();
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text, parts, expectedRevision = await Revision(reader, tenant), expectedSessionRevision = currentRevision })).StatusCode);
                Assert.Single(model.Requests);
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class ComposerAllKindsModel : TenantBackgroundModel
    {
        public bool ReferencesMode;
        public TaskCompletionSource ChildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChildRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelRequest? ReferenceRequest;
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (ReferencesMode)
            {
                ReferenceRequest = request;
                await foreach (var item in new ScriptedLanguageModel().GenerateAsync(request, cancellationToken)) yield return item;
                yield break;
            }
            if (Origin(request) == ExecutionOrigin.ImmediateBackground)
            { ChildEntered.TrySetResult(); await ChildRelease.Task.WaitAsync(cancellationToken); }
            await foreach (var item in base.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Composer_all_six_reference_categories_resolve_real_owned_sources_without_activating_referenced_Skill(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-composer-six-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ComposerAllKindsModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await StartTenantBackground(factory, owner, reportReads: true);
            await model.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(25));
            var storage = factory.Services.GetRequiredService<ITenantRunStore>();
            var source = (await storage.FindTenantRunAsync(value.Tenant, value.Source))!; var instance = source.AgentInstanceId;
            var childSession = (await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(value.Tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == value.Source);
            var child = (await storage.FindTenantRunAsync(value.Tenant, childSession.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
            var artifact = await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(child.Owner, child.SessionId, child.AgentRunId, child.Claim!.Generation), Guid.NewGuid(), "Reference artifact.txt", "text/plain", Encoding.UTF8.GetBytes("Bounded Artifact source body" + new string('A', 5000)));
            model.ChildRelease.TrySetResult(); await WaitTenantBackground(factory, value.Tenant, value.Session, value.Source, true);
            var api = $"/api/v2/tenants/{value.Tenant}"; var human = source.Admission.ExecutionContext!.OriginalInitiatorPrincipalId!.Value;
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, value.Tenant, null, "All Composer source reads", ["agents.read", "sessions.read", "agent-runs.read", "artifacts.read", "workspaces.read", "skills.read"], await Revision(owner, value.Tenant));
            await access.AssignRoleAsync(human, value.Tenant, source.Owner.ExecutorPrincipalId!.Value, role.RoleId, AccessScope.ForTenant(value.Tenant), await Revision(owner, value.Tenant));
            const string path = "/home/all-reference.txt";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, value.Tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes("Bounded Home source body" + new string('H', 5000))))).StatusCode);
            var references = new List<UserResourceReference>();
            foreach (var (category, search) in new[] { ("homeFile", "all-reference"), ("session", value.Session.ToString("D")), ("backgroundSession", child.SessionId.ToString("D")), ("artifact", artifact.ArtifactId.ToString("D")), ("agentRun", child.AgentRunId.ToString("D")), ("skill", "definition:browser.record.lookup") })
            {
                var page = await Json(await owner.GetAsync(api + $"/agents/{instance}/composer?category={category}&search={Uri.EscapeDataString(search)}"));
                var item = Assert.Single(page.GetProperty("items").EnumerateArray());
                references.Add(item.GetProperty("reference").Deserialize<UserResourceReference>(UserMessageContent.Json)!);
                Assert.DoesNotContain("Bounded Home source body", page.ToString()); Assert.DoesNotContain("Bounded Artifact source body", page.ToString());
            }
            foreach (var (name, bytes) in new[] {
                ("extra-one.txt", Encoding.UTF8.GetBytes(new string('X', 5000))),
                ("extra-two.txt", Encoding.UTF8.GetBytes(new string('Y', 5000))),
                ("binary.bin", new byte[] { 1, 2, 3 }),
                ("invalid-utf8.txt", new byte[] { 255, 254 }),
                ("oversized.txt", Encoding.UTF8.GetBytes(new string('Z', 65537))) })
            {
                var extra = "/home/" + name;
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(extra)}&expectedRevision={await Revision(owner, value.Tenant)}", new ByteArrayContent(bytes))).StatusCode);
                references.Add(new("homeFile", AgentInstanceId: instance, Path: extra));
            }
            model.ReferencesMode = true;
            var target = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, value.Tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var parts = references.SelectMany(r => new[] { new UserMessagePart("reference", Reference: r), new UserMessagePart("text", Text: " ") }).ToArray();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{target}/messages", new { messageId = Guid.NewGuid(), text = UserMessageContent.DisplayText(parts), parts, expectedRevision = await Revision(owner, value.Tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var done = await AwaitBrowserRestartRun(storage, value.Tenant, (await Json(response)).GetProperty("runId").GetGuid(), false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.Equal(11, done.Admission.ComposerInput!.References.Count);
            Assert.Equal(6, done.Admission.ComposerInput.References.Select(r => r.Reference.Kind).Distinct().Count());
            Assert.Equal(12000, done.Admission.ComposerInput.References.Sum(r => r.Text.Length));
            Assert.All(done.Admission.ComposerInput.References, r => Assert.True(r.Text.Length <= 4000));
            Assert.Equal(3, done.Admission.ComposerInput.References.Count(r => r.Status == "metadataOnly"));
            Assert.All(done.Admission.ComposerInput.References.Where(r => r.Status == "metadataOnly"), r => Assert.Empty(r.Text));
            Assert.Contains(done.Admission.ComposerInput.References, r => r.Reference.Kind == "backgroundSession" && r.Text.Contains("Owned background result."));
            Assert.DoesNotContain("definition:browser.record.lookup", done.ActiveSkillKeys);
            Assert.Contains("Bounded Home source body", string.Join('\n', model.ReferenceRequest!.Messages.Select(m => m.Text)));
            Assert.Contains("Bounded Artifact source body", string.Join('\n', model.ReferenceRequest.Messages.Select(m => m.Text)));
            // Current source policy is required even after a terminal Run, including cold readback.
            await factory.Services.GetRequiredService<TenantComposerReferenceService>().RequireAsync(done, default);
            var foreign = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other Composer organization", slug = "other-composer" }))).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{foreign}/agents/{instance}/composer?category=homeFile")).StatusCode);
            var sibling = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, value.Tenant) }));
            var siblingId = sibling.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var foreignPart = new[] { new UserMessagePart("reference", Reference: new("homeFile", AgentInstanceId: siblingId, Path: path)) };
            var targetRevision = (await Json(await owner.GetAsync(api + $"/sessions/{target}"))).GetProperty("revision").GetInt64();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + $"/sessions/{target}/messages", new { messageId = Guid.NewGuid(), text = UserMessageContent.DisplayText(foreignPart), parts = foreignPart, expectedRevision = await Revision(owner, value.Tenant), expectedSessionRevision = targetRevision })).StatusCode);
            if (sqlite)
            {
                await factory.DisposeAsync();
                await using var reopened = new NativeTenantSqliteFactory(root, new ComposerRecordingModel());
                using var ready = reopened.CreateClient();
                var recovered = (await reopened.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(value.Tenant, done.AgentRunId))!;
                Assert.Equal(11, recovered.Admission.ComposerInput!.References.Count);
                await reopened.Services.GetRequiredService<TenantComposerReferenceService>().RequireAsync(recovered, default);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false, "changed")] [InlineData(true, "changed")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    [InlineData(false, "disabled")] [InlineData(true, "disabled")]
    public async Task Tenant_Composer_consumed_source_is_rechecked_before_generated_output(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-composer-fence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ComposerRecordingModel { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            const string path = "/home/source-fence.txt";
            var workspace = api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(path)}";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes("Consumed reference evidence")))).StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Recheck Composer reads", ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var reference = new UserResourceReference("homeFile", AgentInstanceId: instance, Path: path); var parts = new[] { new UserMessagePart("reference", Reference: reference) };
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = UserMessageContent.DisplayText(parts), parts, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Contains("Consumed reference evidence", string.Join('\n', Assert.Single(model.Requests).Messages.Select(m => m.Text)));
            if (mode == "changed") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + $"&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent(Encoding.UTF8.GetBytes("Changed reference evidence")))).StatusCode);
            else if (mode == "revoked") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            else Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() })).StatusCode);
            model.Release.TrySetResult(); var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, false);
            await Assert.ThrowsAnyAsync<AccessDeniedException>(async () => await factory.Services.GetRequiredService<TenantComposerReferenceService>().RequireAsync(done, default));
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
            Assert.DoesNotContain(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("role").GetString() == "assistant" && item.GetProperty("text").GetString() == "Hello from synthetic.");
            Assert.Single(model.Requests);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Composer_home_cursor_preserves_long_unicode_native_paths_and_rejects_query_rebinding(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-composer-cursor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ComposerRecordingModel()) : new NativeTenantMemoryFactory(root, new ComposerRecordingModel());
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var directory = "/home/" + string.Join('/', Enumerable.Repeat(string.Concat(Enumerable.Repeat("資料", 30)), 3));
            for (var i = 0; i < 41; i++)
            {
                var path = directory + $"/needle-{i:00}.txt";
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{instance}/workspace/content?path={Uri.EscapeDataString(path)}&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent([65]))).StatusCode);
            }
            var endpoint = api + $"/agents/{instance}/composer?category=homeFile&search=needle";
            var first = await Json(await owner.GetAsync(endpoint)); Assert.Equal(40, first.GetProperty("items").GetArrayLength());
            var cursor = first.GetProperty("nextCursor").GetString()!; Assert.InRange(cursor.Length, 1, 8192);
            Assert.Contains("資料", Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            var next = await Json(await owner.GetAsync(endpoint + "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Single(next.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
            Assert.Equal(41, first.GetProperty("items").EnumerateArray().Concat(next.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetString()).Distinct().Count());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(endpoint.Replace("search=needle", "search=other", StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
