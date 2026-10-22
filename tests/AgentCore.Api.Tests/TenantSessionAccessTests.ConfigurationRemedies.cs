using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ConfigurationRemedyModel : ILanguageModel
    {
        public SkillAuthoringModel Source { get; } = new("configuration.propose", "configuration.inspect", """{"target":"workspace.write","goal":"Save one pending note"}""");
        public ConcurrentQueue<ModelRequest> Requests { get; } = new(); public Guid InitialResponse; public Action<JsonObject>? Alter;
        public ModelCapabilities Capabilities => Source.Capabilities;
        public const string WriteArguments = """{"path":"/working/continued-note.txt","content":"FRESH-EXACT-TASK-CONTENT"}""";
        public ConfigurationRemedyModel()
        {
            Source.PrepareArguments = request => {
                var message = request.Messages.Last(m => m.Role == ModelRole.Tool && m.Name == "configuration.inspect");
                using var body = JsonDocument.Parse(message.Text);
                var option = JsonNode.Parse(body.RootElement.GetProperty("observation").GetProperty("data").GetProperty("remediationOptions")[0].GetRawText())!.AsObject();
                option.Remove("operation"); Alter?.Invoke(option); return option.ToJsonString();
            };
        }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); if (InitialResponse == Guid.Empty) InitialResponse = request.ResponseId;
            if (request.ResponseId == InitialResponse) { await foreach (var item in Source.GenerateAsync(request, ct)) yield return item; yield break; }
            var messages = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); ModelToolCall? next = null;
            if (!messages.Any(m => m.Name == ToolCatalog.WorkspaceWrite))
                next = request.Tools?.Any(t => t.Name == ToolCatalog.WorkspaceWrite) == true ? new("fresh-task-write", ToolCatalog.WorkspaceWrite, WriteArguments)
                    : new("fresh-task-discover", ToolCatalog.CapabilitiesLoad, """{"query":"workspace.write","limit":1}""");
            else if (!messages.Any(m => m.ToolCallId == "fresh-task-authorize"))
            {
                using var result = JsonDocument.Parse(messages.Last(m => m.Name == ToolCatalog.WorkspaceWrite).Text);
                if (result.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
                    next = new("fresh-task-authorize", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool = ToolCatalog.WorkspaceWrite, arguments = JsonSerializer.Deserialize<JsonElement>(WriteArguments) } }));
            }
            if (next is not null) { yield return new ModelToolCallEvent(next); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            yield return new ModelTextDelta("The fresh task has its exact recorded outcome."); yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    public static IEnumerable<object[]> ConfigurationModuleCases => from sqlite in new[] { false, true }
        from scenario in new[] { "remedy", "remedy_reject", "forged_option", "forged_target", "forged_fields", "binding", "binding_reject", "binding_grant", "preference", "binding_stale", "remedy_stale", "remove", "remove_disabled", "unchanged", "conflict", "no_parent", "no_read" }
        select new object[] { scenario, sqlite };
    private static bool BindingScenario(string scenario) => scenario is "binding" or "binding_reject" or "binding_grant" or "binding_stale" or "remove" or "remove_disabled" or "unchanged" or "conflict" or "no_parent" or "no_read";
    public static IEnumerable<object[]> ConfigurationBindingCases => ConfigurationModuleCases.Where(row => BindingScenario((string)row[0]));
    public static IEnumerable<object[]> ConfigurationRemedyCases => ConfigurationModuleCases.Where(row => !BindingScenario((string)row[0]) && (string)row[0] != "remedy");
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public Task Canonical_configuration_fresh_task_retains_boundary_and_requires_new_exact_write_consent(bool sqlite) => ExerciseConfigurationModule("remedy", sqlite);
    [Theory(Timeout = 120000)] [MemberData(nameof(ConfigurationRemedyCases))]
    public Task Canonical_configuration_remedies_use_exact_inspected_settings_and_current_policy(string scenario, bool sqlite) => ExerciseConfigurationModule(scenario, sqlite);
    [Theory(Timeout = 120000)] [MemberData(nameof(ConfigurationBindingCases))]
    public Task Canonical_existing_credential_bindings_use_independent_rights_and_incumbent_association_owner(string scenario, bool sqlite) => ExerciseConfigurationModule(scenario, sqlite);
    private async Task ExerciseConfigurationModule(string scenario, bool sqlite)
    {
        var binding = BindingScenario(scenario);
        var remove = scenario is "remove" or "remove_disabled";
        var reject = scenario is "remedy_reject" or "binding_reject";
        var denied = scenario.StartsWith("forged_", StringComparison.Ordinal) || scenario is "conflict" or "no_parent" or "no_read";
        var root = Path.Combine(Path.GetTempPath(), "tenant-configuration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var remedyModel = new ConfigurationRemedyModel(); var bindingModel = new SkillAuthoringModel(remove ? "credentials.binding.remove" : "credentials.binding.propose", "configuration.inspect", "{}");
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            await using WebApplicationFactory<Program> initialHost = sqlite ? new NativeTenantSqliteFactory(root, binding ? bindingModel : remedyModel) : new NativeTenantMemoryFactory(root, binding ? bindingModel : remedyModel);
            WebApplicationFactory<Program> host = initialHost;
            using var initialOwner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            HttpClient owner = initialOwner;
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = binding ? await SetupManagementInspection(host, owner, scenario != "no_read") : await SetupManagementRead(host, owner, false);
            var api = $"/api/v2/tenants/{tenant}"; var path = api + $"/agents/{instance}"; long revision = binding ? 6 : 2; Guid credential = default;
            if (binding)
            {
                var existing = Assert.Single((await host.Services.GetRequiredService<ITenantCredentialBindingStore>().ReadTenantCredentialBindingsAsync(tenant, instance))!.Bindings);
                if (remove || scenario == "unchanged") credential = existing.Binding.CredentialId;
                else credential = (await Json(await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("New exact binding", "Token", "General", await Revision(owner, tenant), ProtectedValue: "CONFIGURATION-SECRET-SENTINEL")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
                if (scenario == "remove_disabled")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/credentials/{credential}", new TenantCredentialUpdateRequest(existing.Credential.Value.DisplayName, "Disabled", await Revision(owner, tenant), existing.Credential.Value.Revision))).StatusCode);
                bindingModel.Arguments = remove ? JsonSerializer.Serialize(new { expectedInstanceRevision = revision, bindingId = existing.Binding.BindingId, expectedBindingRevision = existing.Binding.Revision })
                    : JsonSerializer.Serialize(new { expectedInstanceRevision = revision, credentialId = credential, reference = scenario is "unchanged" or "conflict" ? existing.Binding.Reference : "proposed-account" });
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision,
                    overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Settings" } }, autoApply = new { value = scenario == "preference" ? new[] { "ConversationPreferences" } : Array.Empty<string>() } } })).StatusCode); revision++;
                if (scenario == "preference") remedyModel.Source.ReadArguments = """{"target":"settings.patch","goal":"Keep responses concise"}""";
                remedyModel.Alter = option => {
                    if (scenario == "forged_option") option["option"] = new string('a', 64);
                    if (scenario == "forged_target") option["target"] = "workspace.delete";
                    if (scenario == "forged_fields") option["set"]!["selected"] = new JsonArray("workspace.write");
                };
                var settings = (await Json(await owner.GetAsync(path + "/settings"))).GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "capabilities");
                var selected = settings.GetProperty("effective").GetProperty("selected").EnumerateArray().Select(t => t.GetString()).Where(t => t != ToolCatalog.WorkspaceWrite).ToArray();
                var disabled = await owner.PatchAsJsonAsync(path + "/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, set = new { selected }, clear = Array.Empty<string>() });
                Assert.True(disabled.StatusCode == HttpStatusCode.OK, await disabled.Content.ReadAsStringAsync()); revision++;
            }
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single().PrincipalId;
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!; var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact configuration author", binding ? new[] { "agents.read", "agents.update", "credentials.read", "credentials.bind" }.Where(key => !(scenario == "no_parent" && key == "agents.update") && !(scenario == "no_read" && key == "credentials.read") && !(scenario == "binding_grant" && key == "credentials.bind")).ToArray()
                : ["agents.read", "agents.update", "sessions.read", "workspaces.read", "workspaces.write"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision }))).GetProperty("sessionId").GetGuid();
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = binding ? "Bind the existing reviewed account." : "Enable workspace writing through the exact remedy, then save one pending note after a separate exact approval.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var initial = (await Json(sent)).GetProperty("runId").GetGuid();
            string Trace() => JsonSerializer.Serialize((binding ? bindingModel.Requests : remedyModel.Requests).SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.Text })) + "\n" + string.Join('\n', logs.Errors);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
            var pending = await AwaitManagementRun(runs, tenant, initial, !denied && scenario is not ("unchanged" or "preference"));
            if (scenario == "preference")
            {
                Assert.True(pending.Status == AgentRunStatus.Completed, pending.Failure?.Summary + Trace()); Assert.Null(pending.ApprovalRequestId);
                Assert.Equal(revision, pending.Admission.Configuration!.InstanceRevision);
                Assert.Equal(revision + 1, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                var effective = (await Json(await owner.GetAsync(path + "/settings"))).GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "conversationPolicy").GetProperty("effective");
                Assert.Equal("concise", effective.GetProperty("responseLength").GetString());
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items);
                return;
            }
            if (denied || scenario == "unchanged")
            {
                Assert.True(pending.IsTerminal, pending.Failure?.Summary + Trace());
                Assert.Null(pending.ApprovalRequestId);
                Assert.Equal(revision, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items);
                Assert.DoesNotContain("CONFIGURATION-SECRET-SENTINEL", Trace());
                if (scenario == "unchanged") Assert.Contains("operation_unchanged", Trace());
                return;
            }
            Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, pending.Failure?.Summary + Trace());
            var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            Assert.Equal(binding ? "credentials.bind" : "agents.update", request.Permission);
            Assert.Equal(scenario == "binding_grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
            if (binding) { Assert.NotNull(request.Operation.ManagementMutation!.Binding); Assert.DoesNotContain("CONFIGURATION-SECRET-SENTINEL", JsonSerializer.Serialize(request)); }
            else Assert.NotNull(request.Operation.ManagementMutation!.Remedy);
            if (scenario is "binding_stale" or "remedy_stale")
            {
                var changed = binding ? await owner.PatchAsJsonAsync(api + $"/credentials/{credential}", new TenantCredentialUpdateRequest("Changed credential witness", "Disabled", await Revision(owner, tenant), 1))
                    : await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, overrides = new { mode = new { value = "Assisted" } } });
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
                Assert.Equal(binding ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                Assert.DoesNotContain((await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
                Assert.Null(await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                return;
            }
            if (scenario == "binding")
            {
                var viewer = await FloorHuman(host, owner, tenant, "configuration-binding-reviewer", ["agents.read", "approvals.read"]);
                using var inspector = viewer.Client;
                Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                var parentRole = await access.SaveRoleAsync(human, tenant, null, "Exact binding parent", ["agents.update"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, viewer.Principal, parentRole.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
                Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                var childRole = await access.SaveRoleAsync(human, tenant, null, "Exact binding credential", ["credentials.read", "credentials.bind"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, viewer.Principal, childRole.RoleId, AccessScope.ForResource(tenant, "credential", credential.ToString("D")), await Revision(owner, tenant));
                Assert.Equal(1, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
            }
            if (sqlite && scenario is "remedy" or "binding")
            {
                await using (var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
                {
                    await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson','{{}}') WHERE RequestId={request.RequestId}"));
                    var stored = await db.Set<AgentCore.Infrastructure.Security.TenantApprovalRecord>().AsNoTracking().SingleAsync(r => r.RequestId == request.RequestId);
                    var altered = JsonNode.Parse(stored.PayloadJson)!; var newRequest = Guid.NewGuid(); altered["RequestId"] = newRequest;
                    altered["Operation"]!["ManagementMutation"]![binding ? "Binding" : "Remedy"]!["protectedPayload"] = "forged-closed-field";
                    var columns = typeof(AgentCore.Infrastructure.Security.TenantApprovalRecord).GetProperties().Select(p => p.Name).ToArray();
                    await db.Database.OpenConnectionAsync(); using var insert = db.Database.GetDbConnection().CreateCommand();
                    insert.CommandText = "INSERT INTO TenantApprovalRequests (" + string.Join(",", columns.Select(c => "\"" + c + "\"")) + ") SELECT "
                        + string.Join(",", columns.Select(c => c == "RequestId" ? "$id" : c == "PayloadJson" ? "$payload" : "\"" + c + "\"")) + " FROM TenantApprovalRequests WHERE RequestId=$old";
                    foreach (var (key, value) in new (string,object)[] { ("$id",newRequest),("$payload",altered.ToJsonString()),("$old",request.RequestId) })
                    { var parameter = insert.CreateParameter(); parameter.ParameterName=key; parameter.Value=value; insert.Parameters.Add(parameter); }
                    var refused = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
                    Assert.Contains(binding ? "Use the closed safe credential binding plan" : "Use the closed exact inspected configuration remedy", refused.Message);
                    await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261023120000_TenantHarnessCommandAuthoring"));
                    await db.GetService<IMigrator>().MigrateAsync();
                }
                await initialHost.DisposeAsync();
                reopened = new NativeTenantSqliteFactory(root, binding ? bindingModel : remedyModel); host = reopened;
                reopenedOwner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
                runs = host.Services.GetRequiredService<ITenantRunStore>(); approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
                var restored = (await approvals.FindApprovalAsync(tenant, request.RequestId))!;
                Assert.Equal(JsonSerializer.Serialize(request), JsonSerializer.Serialize(restored));
            }
            async Task Approve(TenantApprovalRequest value)
            {
                var result = await owner.PostAsJsonAsync(api + $"/approvals/{value.RequestId}/decision", new { decision = reject ? "reject" : "approve", actionHash = value.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = value.Revision });
                Assert.True(result.StatusCode == HttpStatusCode.OK, await result.Content.ReadAsStringAsync() + Trace());
            }
            await Approve(request); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); AgentRun done;
            try
            {
                while (true) { done = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != initial && r.ApprovalRequestId == request.RequestId)!;
                    if (done?.IsTerminal == true) break; await Task.Delay(25, deadline.Token); }
                Assert.True(done.Status == AgentRunStatus.Completed, done.Failure?.Summary + Trace()); Assert.Equal(revision, done.Admission.Configuration!.InstanceRevision);
                if (reject)
                {
                    Assert.Equal(revision, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                    Assert.DoesNotContain((await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
                    return;
                }
                if (sqlite)
                {
                    await using var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                    await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET ReasonCode='forged' WHERE OperationId={request.Operation.OperationId}"));
                }
                Assert.Equal(revision + 1, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                if (binding)
                {
                    var bindings = (await host.Services.GetRequiredService<ITenantCredentialBindingStore>().ReadTenantCredentialBindingsAsync(tenant, instance))!.Bindings;
                    if (remove) Assert.DoesNotContain(bindings, b => b.Binding.CredentialId == credential);
                    else Assert.Contains(bindings, b => b.Binding.CredentialId == credential && b.Binding.Reference == "proposed-account");
                    return;
                }
                var savedRequests = new Dictionary<Guid,TenantApprovalRequest>(); var savedReceipts = new Dictionary<Guid,TenantOperationReceipt>();
                foreach (var id in TenantConfigurationContinuationRules.RequestIds(done))
                {
                    var known = (await approvals.FindApprovalAsync(tenant, id))!; savedRequests[id] = known;
                    var effect = await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, known.Operation.OperationId);
                    if (effect is not null) savedReceipts[effect.OperationId] = effect;
                }
                var candidate = TenantConfigurationContinuationRules.Pending(done, id => savedRequests.GetValueOrDefault(id), id => savedReceipts.GetValueOrDefault(id));
                Assert.Null(TenantConfigurationContinuationRules.Pending(done, _ => null, id => savedReceipts.GetValueOrDefault(id)));
                Assert.Null(TenantConfigurationContinuationRules.Pending(done, id => savedRequests.GetValueOrDefault(id) is { } known ? known with { Status = TenantApprovalStatus.Rejected } : null, id => savedReceipts.GetValueOrDefault(id)));
                Assert.Null(TenantConfigurationContinuationRules.Pending(done, id => savedRequests.GetValueOrDefault(id), id => savedReceipts.GetValueOrDefault(id) is { } effect ? effect with { Outcome = TenantOperationOutcome.Denied } : null));
                AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryReadState(done.Checkpoint, out var checkpointMessages, out var uncertain, out var blocked);
                Assert.True(candidate is not null, JsonSerializer.Serialize(new { done.Status, done.CancellationRequested, done.SideEffect.Disposition, done.Admission.ExecutionContext, Messages = checkpointMessages?.Count, uncertain, blocked }) + Trace());
                var sessionState = (await host.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var originalRoot = (await runs.FindTenantRunAsync(tenant, initial))!;
                if (!(await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items.Any(r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation))
                Assert.True(TenantConfigurationContinuationRules.SafeSession(done, originalRoot, sessionState.Snapshot, (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items, (await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items),
                    JsonSerializer.Serialize(new { sessionState.Snapshot.Status, sessionState.Snapshot.LifecycleStatus, sessionState.Snapshot.Surfaces, sessionState.Snapshot.PendingAgentInputIds, Entries = sessionState.Snapshot.Entries.Select(e => new { e.EntryId, e.Sequence, e.Role }) }) + Trace());
                var history = (await host.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items;
                var oldRuns = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items.Where(r => r.Admission.Activation.Kind != ActivationKind.ConfigurationContinuation).ToArray();
                Assert.False(TenantConfigurationContinuationRules.SafeSession(done, originalRoot, sessionState.Snapshot with { ArchivedAt = DateTimeOffset.UtcNow }, oldRuns, history));
                var later = history.First(e => e.Role == ConversationRole.User) with { EntryId = Guid.NewGuid(), Sequence = history.Max(e => e.Sequence) + 1 };
                Assert.False(TenantConfigurationContinuationRules.SafeSession(done, originalRoot, sessionState.Snapshot, oldRuns, history.Append(later)));
                AgentRun fresh;
                while (true) { fresh = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation)!;
                    if (fresh?.Status == AgentRunStatus.WaitingForApproval || fresh?.IsTerminal == true) break; await Task.Delay(25, deadline.Token); }
                Assert.True(fresh.Status == AgentRunStatus.WaitingForApproval, fresh.Failure?.Summary + Trace()); Assert.Equal(revision + 1, fresh.Admission.Configuration!.InstanceRevision);
                Assert.Equal(done.AgentRunId, fresh.Admission.Activation.SourceAgentRunId); Assert.NotEqual(done.ResponseId, fresh.ResponseId);
                Assert.Equal(done.Admission.ExecutionContext!.RootActivationId, fresh.Admission.ExecutionContext!.RootActivationId);
                Assert.Throws<AccessDeniedException>(() => TenantConfigurationContinuationRules.RequireDirectWrite(fresh));
                if (sqlite)
                {
                    await using var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                    var freshId = fresh.AgentRunId.ToString("D");
                    await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.admission.activation.evidenceJson','{{}}') WHERE AgentRunId={freshId}"));
                }
                var taskRequest = (await approvals.FindApprovalAsync(tenant, fresh.ApprovalRequestId!.Value))!; Assert.Equal("workspaces.write", taskRequest.Permission); Assert.Equal(ApprovalKind.Review, taskRequest.Kind);
                await Approve(taskRequest);
                while (true) { var final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != fresh.AgentRunId && r.ApprovalRequestId == taskRequest.RequestId);
                    if (final?.IsTerminal == true) { Assert.True(final.Status == AgentRunStatus.Completed, final.Failure?.Summary + Trace()); Assert.Equal(ToolCatalog.WorkspaceWrite, TenantConfigurationContinuationRules.Target(final)); Assert.Throws<AccessDeniedException>(() => TenantConfigurationContinuationRules.RequireDirectWrite(final)); break; } await Task.Delay(25, deadline.Token); }
                Assert.Equal("FRESH-EXACT-TASK-CONTENT", await owner.GetStringAsync(api + $"/sessions/{session}/workspace/content?path=" + Uri.EscapeDataString("/working/continued-note.txt")));
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items, r => r.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation);
                Assert.All(remedyModel.Requests.Where(r => r.ResponseId == fresh.ResponseId), r => Assert.DoesNotContain(r.Tools ?? [], t => ManagementTools.IsManagement(t.Name)));
            }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(Trace()); }
        }
        finally { reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
