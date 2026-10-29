using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class SkillSourceTrace(ITenantManagementStore inner, ConcurrentQueue<string> stages) : ITenantManagementStore, ITenantDirectOperationStore
    {
        public ValueTask<TenantResourcePinRetention?> ReadRetainedResourcePinAsync(TenantRunReference reference, AgentCore.Domain.Definitions.EffectiveAgentResource resource, CancellationToken ct = default) =>
            inner.ReadRetainedResourcePinAsync(reference, resource, ct);
        public ValueTask<TenantManagementResourceContext> ReadTenantManagementResourceContextAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default) =>
            inner.ReadTenantManagementResourceContextAsync(reference, generation, tool, arguments, inspectionHuman, ct);
        public async ValueTask<TenantManagementSource> ReadTenantManagementSourceAsync(TenantRunReference reference, Guid? generation, Guid? inspectionHuman = null, CancellationToken ct = default)
        { stages.Enqueue("source.start"); var value = await inner.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct); stages.Enqueue("source.end"); return value; }
        public async ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
        { stages.Enqueue("raw.start"); await inner.RequireTenantManagementSkillSourcesAsync(reference, generation, tool, arguments, inspectionHuman, ct); stages.Enqueue("raw.end"); }
        public ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
            => inner.ObserveTenantManagementReadAsync(reference, generation, tool, arguments, expected, inspectionHuman, ct);
        public ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources, Guid? generation, CancellationToken ct = default)
            => inner.RequireTenantManagementReadSourcesAsync(reference, sources, generation, ct);
        public async ValueTask<TenantOperationReceipt?> TryCommitManagementCommandAsync(TenantSecurityState next, long revision, TenantManagementCommandCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        { stages.Enqueue("commit.start"); var receipt = await inner.TryCommitManagementCommandAsync(next, revision, commit, audit, ct); stages.Enqueue("commit.end"); return receipt; }
        public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenant, Guid run, string call, CancellationToken ct = default)
            => ((ITenantDirectOperationStore)inner).FindDirectOperationReceiptAsync(tenant, run, call, ct);
    }
    private sealed class SkillAuthoringModel(string tool, string read, string readArguments) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public Func<ModelRequest, string>? PrepareArguments;
        public string ReadArguments = readArguments; public string Arguments = "{}"; public bool SuppressConsent; public bool ReadConsent; public ModelToolCall? AfterCommandRead;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var messages = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); ModelToolCall? next = null;
            ModelToolCall? Offered(string name, string arguments, string callId) => request.Tools?.Any(t => t.Name == name) == true ? new(callId, name, arguments)
                : messages.Any(m => m.Name == ToolCatalog.CapabilitiesLoad && m.ToolCallId == "discover-" + callId) ? null
                : new("discover-" + callId, ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = name, limit = 1 }));
            var readMessage = messages.LastOrDefault(m => m.Name == read);
            using var readBody = JsonDocument.Parse(readMessage?.Text ?? "{}");
            var readReview = readBody.RootElement.TryGetProperty("decision", out var readDecision) && readDecision.GetString() == "requiresApproval";
            var readApproved = messages.Any(m => m.Name == ToolCatalog.RequestAuthorization && m.ToolCallId == "approve-skill-read" && m.Text.Contains("\"managementReadObservation\"", StringComparison.Ordinal));
            if (readMessage is null) next = Offered(read, ReadArguments, "read-skill-source");
            else if (ReadConsent && readReview && !readApproved)
            {
                if (!messages.Any(m => m.ToolCallId == "approve-skill-read")) next = new("approve-skill-read", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "inspect_management", read = new { tool = read, arguments = JsonSerializer.Deserialize<JsonElement>(ReadArguments) } }));
            }
            else if (!messages.Any(m => m.Name == tool)) next = Offered(tool, Arguments = PrepareArguments?.Invoke(request) ?? Arguments, "write-skill");
            else if (!SuppressConsent && !messages.Any(m => m.Name == ToolCatalog.RequestAuthorization))
            {
                using var body = JsonDocument.Parse(messages.Last(m => m.Name == tool).Text);
                if (body.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
                    next = new("approve-skill", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_management", change = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(Arguments) } }));
            }
            if (next is null && AfterCommandRead is { } extra && !messages.Any(m => m.ToolCallId == extra.Id)
                && messages.Any(m => m.ToolCallId == "approve-skill" && m.Text.Contains("\"outcome\":\"completed\"", StringComparison.Ordinal)))
                next = Offered(extra.Name, extra.ArgumentsJson, extra.Id);
            if (next is not null) { yield return new ModelToolCallEvent(next); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            yield return new ModelTextDelta("The exact Skill operation has its recorded outcome; this Run retained its original procedures.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    public static IEnumerable<object[]> SkillAuthoringCases =>
        from sqlite in new[] { false, true }
        from item in new[] {
            ("create", "managed", "direct"), ("create", "assisted", "approve"), ("create", "assisted", "reject"),
            ("update", "managed", "direct"), ("update", "assisted", "approve"), ("update", "assisted", "reject"),
            ("set_enabled", "managed", "direct"), ("definition_enabled", "managed", "direct"), ("reset_enabled", "managed", "direct"),
            ("delete", "managed", "approve"), ("delete", "managed", "reject"),
            ("customize", "managed", "approve"), ("customize", "managed", "reject"),
            ("builtin_enabled", "managed", "direct"), ("builtin_enabled", "assisted", "approve"), ("builtin_enabled", "assisted", "reject"),
            ("reset_unchanged", "managed", "unchanged"),
            ("update_unchanged", "managed", "unchanged"), ("enabled_unchanged", "managed", "unchanged"),
            ("update", "visibility", "reject"), ("update", "scoped_reviewer", "approve"),
            ("update", "read_approved", "direct_after_read"),
            ("update", "grant", "grant"), ("update", "child", "direct"), ("update", "policy", "approve"),
            ("update", "independent", "denied"), ("update", "no_read", "withheld"), ("update", "parent_only", "withheld"), ("update", "read_policy", "withheld") }
        select new object[] { sqlite, item.Item1, item.Item2, item.Item3 };

    [Theory(Timeout = 120000)] [MemberData(nameof(SkillAuthoringCases))]
    public async Task Canonical_Skill_authoring_uses_actual_child_authority_exact_effects_and_historical_source_retention(bool sqlite, string operation, string mode, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-skill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var key = operation is "customize" or "definition_enabled" or "reset_enabled" or "reset_unchanged" ? "definition:browser.record.lookup"
            : operation == "builtin_enabled" ? "builtin:configuration-assistance.v1" : "instance:authored-source";
        var tool = "skills." + (operation is "definition_enabled" or "builtin_enabled" or "enabled_unchanged" ? "set_enabled" : operation == "update_unchanged" ? "update" : operation == "reset_unchanged" ? "reset_enabled" : operation);
        var read = operation == "create" ? "skills.list" : "skills.inspect";
        try
        {
            var stages = new ConcurrentQueue<string>();
            var model = new SkillAuthoringModel(tool, read, read == "skills.list" ? "{}" : JsonSerializer.Serialize(new { key })) { SuppressConsent = mode == "parent_only", ReadConsent = mode == "read_approved" };
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                s.Replace(ServiceDescriptor.Singleton<ITenantManagementStore>(sp => new SkillSourceTrace((ITenantManagementStore)sp.GetRequiredService<IIamStore>(), stages)));
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}"; var path = api + $"/agents/{instance}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = mode != "assisted" ? "Managed" : "Assisted" }, scopes = new { value = new[] { "Skills", "Settings" } },
                    autoApply = new { value = mode != "assisted" ? new[] { "SkillChanges" } : Array.Empty<string>() } } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3,
                input = new { id = "authored-source", name = "Private authoring source", description = "Original private description", procedure = "ORIGINAL-PRIVATE-SKILL-PROCEDURE", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
            long instanceRevision = 4; long skillRevision = (await Json(await owner.GetAsync(path + "/skills?pageSize=100"))).GetProperty("items").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("revision").GetInt64();
            if (operation == "reset_enabled")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "set_enabled", key, enabled = false, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4, expectedSkillRevision = 1 })).StatusCode);
                instanceRevision++; skillRevision++;
            }
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var access = host.Services.GetRequiredService<TenantAccessService>();
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var permissions = mode == "no_read" ? new[] { "agents.read", "skills.manage" } : mode == "parent_only" ? new[] { "agents.read", "agents.update", "skills.read" }
                : mode == "grant" ? new[] { "agents.read", "skills.read" } : new[] { "agents.read", "skills.read", "skills.manage" };
            var role = await access.SaveRoleAsync(state.Users.Single().PrincipalId, tenant, null, "Independent Skill author", permissions, await Revision(owner, tenant));
            await access.AssignRoleAsync(state.Users.Single().PrincipalId, tenant, agent.PrincipalId, role.RoleId,
                mode == "child" ? TenantAgentChildScopes.Scope(tenant, instance, "skill", key) : AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode is "policy" or "independent" or "read_policy" or "visibility" or "scoped_reviewer" or "read_approved")
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + (mode is "read_policy" or "read_approved" ? "skills.read" : "skills.manage"), new {
                    mode = "always", independentReview = mode == "independent", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            model.Arguments = operation is "create" or "update" or "update_unchanged" ? JsonSerializer.Serialize(new { key = operation == "create" ? (string?)null : key,
                expectedRevision = operation == "create" ? (long?)null : skillRevision, expectedInstanceRevision = instanceRevision,
                name = operation == "update_unchanged" ? "Private authoring source" : "Authored exact Skill <literal>", description = operation == "update_unchanged" ? "Original private description" : "Revised exact description", procedure = operation == "update_unchanged" ? "ORIGINAL-PRIVATE-SKILL-PROCEDURE" : "NEW-PRIVATE-SKILL-PROCEDURE", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } })
                : JsonSerializer.Serialize(new { key, expectedRevision = skillRevision, expectedInstanceRevision = instanceRevision, enabled = operation == "enabled_unchanged" });
            // Match the closed tool schemas; optional create inputs are omitted, not null.
            var arguments = System.Text.Json.Nodes.JsonNode.Parse(model.Arguments)!.AsObject();
            if (operation == "create") { arguments.Remove("key"); arguments.Remove("expectedRevision"); arguments["id"] = "new-authored"; }
            if (operation is not ("create" or "update" or "update_unchanged" or "set_enabled" or "definition_enabled" or "builtin_enabled" or "enabled_unchanged")) arguments.Remove("enabled");
            model.Arguments = arguments.ToJsonString();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect and perform the exact local Skill change using the current authoring policy.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            string Trace() => JsonSerializer.Serialize(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.ToolCallId, m.Text }));
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
            AgentRun admitted;
            try { admitted = await AwaitManagementRun(runs, tenant, runId, decision is "approve" or "reject" or "grant" or "direct_after_read"); }
            catch (OperationCanceledException)
            {
                using var inspect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stalled = await runs.FindTenantRunAsync(tenant, runId, inspect.Token);
                var stalledReceipt = await ((ITenantDirectOperationStore)host.Services.GetRequiredService<IIamStore>()).FindDirectOperationReceiptAsync(tenant, runId, "write-skill", inspect.Token);
                var current = await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance, inspect.Token);
                throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(new { Run = stalled?.Status, Revision = stalled?.Revision, Failure = stalled?.Failure, Effect = stalled?.SideEffect,
                    Receipt = stalledReceipt?.Outcome, Reason = stalledReceipt?.ReasonCode, InstanceRevision = current?.Instance.Revision, Stages = stages.ToArray() }) + "\n" + Trace() + "\n" + string.Join('\n', logs.Errors));
            }
            AgentRun final = admitted; TenantApprovalRequest? request = null; HttpClient? inspector = null; Guid? inspectorPrincipal = null; Guid? inspectorRole = null;
            if (decision is "approve" or "reject" or "grant" or "direct_after_read")
            {
                Assert.True(admitted.Status == AgentRunStatus.WaitingForApproval, $"{admitted.Status}: {admitted.Failure?.Code}: {admitted.Failure?.Summary}\n" + string.Join('\n', logs.Errors) + "\n" + Trace());
                request = (await approvals.FindApprovalAsync(tenant, admitted.ApprovalRequestId!.Value))!;
                Assert.Equal(mode == "read_approved" ? "agents.read" : "skills.manage", request.Operation.Permission); Assert.Equal(decision == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
                Assert.Equal(mode == "read_approved" ? "agent" : operation is "create" or "customize" ? "tenant" : "skill", request.Operation.ResourceType);
                if (mode == "read_approved") Assert.NotNull(request.Operation.ManagementRead); else Assert.NotNull(request.Operation.ManagementMutation!.Skill);
                var detail = await owner.GetAsync(api + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                if (mode is "visibility" or "scoped_reviewer")
                {
                    var viewer = await FloorHuman(host, owner, tenant, "skill-plan-inspector", mode == "visibility" ? ["agents.read", "skills.manage", "approvals.read"] : ["agents.read", "approvals.read"]);
                    inspector = viewer.Client; inspectorPrincipal = viewer.Principal;
                    if (mode == "visibility")
                    {
                        var hidden = await inspector.GetAsync(api + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
                        Assert.DoesNotContain("ORIGINAL-PRIVATE-SKILL-PROCEDURE", await hidden.Content.ReadAsStringAsync());
                        Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                        await access.SaveRoleAsync(state.Users.Single().PrincipalId, tenant, viewer.Role, "skill-plan-inspector", ["agents.read", "skills.manage", "skills.read", "approvals.read"], await Revision(owner, tenant));
                    }
                    else
                    {
                        var scoped = await access.SaveRoleAsync(state.Users.Single().PrincipalId, tenant, null, "Exact Skill approval reader", ["skills.read", "skills.manage"], await Revision(owner, tenant)); inspectorRole = scoped.RoleId;
                        await access.AssignRoleAsync(state.Users.Single().PrincipalId, tenant, viewer.Principal, scoped.RoleId, TenantAgentChildScopes.Scope(tenant, instance, "skill", key), await Revision(owner, tenant));
                    }
                    Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                    Assert.Equal(1, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                    if (mode == "visibility")
                    {
                        await access.SaveRoleAsync(state.Users.Single().PrincipalId, tenant, viewer.Role, "skill-plan-inspector", ["agents.read", "skills.manage", "approvals.read"], await Revision(owner, tenant));
                        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                        Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                    }
                }
                var decided = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = decision is "grant" or "direct_after_read" ? "approve" : decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                Assert.True(decided.StatusCode == HttpStatusCode.OK, await decided.Content.ReadAsStringAsync() + "\n" + string.Join('\n', logs.Errors) + "\n" + Trace());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.ApprovalRequestId == request.RequestId && r.AgentRunId != runId)!;
                    if (final?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            }
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}\n" + string.Join('\n', logs.Errors) + "\n" + Trace());
            Assert.Equal(instanceRevision, final.Admission.Configuration!.InstanceRevision);
            var source = (await host.Services.GetRequiredService<ITenantAgentStore>().ReadTenantAgentConfigurationAsync(tenant, instance))!;
            var changed = decision is "approve" or "direct" or "grant" or "direct_after_read";
            Assert.True(instanceRevision + (changed ? 1 : 0) == source.Agent.Instance.Revision, Trace());
            var retained = TenantManagementReadReferences.Read(final.Checkpoint);
            if (mode is "no_read" or "read_policy") Assert.Empty(retained);
            else
            {
                Assert.Single(retained, r => r.ToolName == read);
                Assert.All(retained.Where(r => r.ToolName != read), r => {
                    Assert.Equal(ToolCatalog.SkillsLoad, r.ToolName);
                    Assert.All(TenantManagementInspectionSources.Read(r.SourcesJson), source => {
                        Assert.Equal("skillProcedure", source.Kind);
                        Assert.Contains(final.PinnedSkillCatalog, pin => pin.Key == source.Key);
                    });
                });
            }
            await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, final.AgentRunId), retained, null);
            TenantOperationReceipt? receipt = request is null || mode == "read_approved" ? await ((ITenantDirectOperationStore)host.Services.GetRequiredService<IIamStore>()).FindDirectOperationReceiptAsync(tenant, final.AgentRunId, "write-skill")
                : await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (changed)
            {
                Assert.NotNull(receipt); Assert.NotNull(receipt.ManagementSkill); Assert.Equal(TenantManagementMutationRules.Completed, receipt.ReasonCode);
                Assert.Equal(instanceRevision, receipt.ManagementSkill.BeforeInstanceRevision);
                var effect = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == (request is null || mode == "read_approved" ? tool : ToolCatalog.RequestAuthorization));
                using var actualResult = JsonDocument.Parse(effect.Text);
                if (operation == "delete") Assert.Equal(key, actualResult.RootElement.GetProperty("deletedKey").GetString());
                else
                {
                    Assert.Equal(receipt.ManagementSkill.Result.Key, actualResult.RootElement.GetProperty("skill").GetProperty("key").GetString());
                    Assert.Equal(receipt.ManagementSkill.Result.Revision, actualResult.RootElement.GetProperty("skill").GetProperty("revision").GetInt64());
                }
                if (operation == "customize")
                {
                    Assert.Equal("definition:browser.record.lookup", actualResult.RootElement.GetProperty("definitionSkill").GetProperty("key").GetString());
                    Assert.False(actualResult.RootElement.GetProperty("definitionSkill").GetProperty("enabled").GetBoolean());
                    Assert.StartsWith("instance:", actualResult.RootElement.GetProperty("instanceSkill").GetProperty("key").GetString());
                }
                if (operation == "create") Assert.Contains(source.Skills.InstanceSkills, s => s.SkillId == "new-authored" && s.Procedure == "NEW-PRIVATE-SKILL-PROCEDURE");
                if (operation == "update") Assert.Equal("NEW-PRIVATE-SKILL-PROCEDURE", source.Skills.InstanceSkills.Single(s => s.SkillId == "authored-source").Procedure);
                if (operation == "delete") Assert.DoesNotContain(source.Skills.InstanceSkills, s => s.SkillId == "authored-source");
                if (operation == "customize")
                {
                    Assert.Contains(source.Skills.InstanceSkills, s => s.SourceDefinitionSkillId == "browser.record.lookup" && s.SourceDefinitionId == source.Agent.Instance.DefinitionId && s.SourceDefinitionVersion == 1);
                    Assert.False(source.Skills.DefinitionStates.Single(s => s.DefinitionSkillId == "browser.record.lookup").EnabledOverride);
                }
            }
            else Assert.Null(receipt);
            if (request is not null)
            {
                if (decision == "grant")
                {
                    Assert.Equal("Consumed", (await approvals.FindOperationGrantAsync(tenant, request.RequestId))!.Status.ToString());
                    Assert.False((await host.Services.GetRequiredService<IamAuthorizer>().CheckAsync(agent.PrincipalId, "skills.manage", TenantAgentChildScopes.Scope(tenant, instance, "skill", key))).Allowed);
                }
                else Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                Assert.Equal(1, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
            }
            else Assert.Equal(0, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
            if (mode is "no_read" or "read_policy") Assert.DoesNotContain("ORIGINAL-PRIVATE-SKILL-PROCEDURE", Trace());
            if (mode == "parent_only") Assert.Contains("agent_missing_delegatable_permission", Trace());
            if (mode == "independent") Assert.Contains("no_eligible_approver", Trace());
            Assert.DoesNotContain(final.PinnedSkillCatalog, s => s.Procedure == "NEW-PRIVATE-SKILL-PROCEDURE");
            if (mode == "scoped_reviewer")
            {
                Assert.Equal(HttpStatusCode.OK, (await inspector!.GetAsync(api + $"/approvals/{request!.RequestId}")).StatusCode);
                Assert.Equal(1, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "delete", key, expectedSkillRevision = 2, expectedInstanceRevision = instanceRevision + 1, expectedRevision = await Revision(owner, tenant) })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/skills/commands", new { operation = "create", expectedInstanceRevision = instanceRevision + 2, expectedRevision = await Revision(owner, tenant),
                    input = new { id = "authored-source", name = "Recreated private source", description = "A distinct later source", procedure = "RECREATED-PRIVATE-SKILL-PROCEDURE", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
                await access.AssignRoleAsync(state.Users.Single().PrincipalId, tenant, inspectorPrincipal!.Value, inspectorRole!.Value, TenantAgentChildScopes.Scope(tenant, instance, "skill", key), await Revision(owner, tenant));
                Assert.True((await host.Services.GetRequiredService<IamAuthorizer>().CheckAsync(inspectorPrincipal.Value, "skills.read", TenantAgentChildScopes.Scope(tenant, instance, "skill", key))).Allowed);
                Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                await Assert.ThrowsAsync<AccessDeniedException>(() => host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, final.AgentRunId), retained, null).AsTask());
            }
            inspector?.Dispose();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
