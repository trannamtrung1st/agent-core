using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> ResourceAuthoringCases =>
        from sqlite in new[] { false, true }
        from item in new[] {
            ("create", "approve"), ("create", "reject"), ("create", "auto"), ("create", "auto_policy"), ("update", "approve"), ("update", "reject"),
            ("enable", "approve"), ("inherited_disable", "approve"), ("inherited_reset", "unchanged"), ("copy", "approve"), ("copy", "reject"), ("delete", "approve"), ("delete", "reject"),
            ("update_unchanged", "unchanged"), ("enabled_unchanged", "unchanged"),
            ("update", "parent_only"), ("update", "no_read"), ("update", "grant"),
            ("update", "scoped"), ("delete", "scoped"), ("update", "forged_source"), ("update", "stale") }
        select new object[] { sqlite, item.Item1, item.Item2 };

    [Fact(Timeout = 120000)]
    public async Task Resource_authoring_representative_consent_and_durable_creation()
    {
        await Canonical_Resource_authoring_records_exact_consent_and_keeps_original_Run_sources(false, "update", "approve");
        await Canonical_Resource_authoring_records_exact_consent_and_keeps_original_Run_sources(true, "create", "approve");
    }

    public static IEnumerable<object[]> ResourcePinFollowupCases => from sqlite in new[] { false, true }
        from item in new[] { ("update", "approve"), ("enable", "approve"), ("delete", "approve"), ("inherited_disable", "approve"), ("update", "scoped"), ("delete", "scoped") }
        select new object[] { sqlite, item.Item1, item.Item2 };
    [Theory(Timeout = 120000)] [MemberData(nameof(ResourcePinFollowupCases))]
    public Task Resource_authoring_pinned_followup_and_external_gap(bool sqlite, string operation, string scenario) =>
        Canonical_Resource_authoring_records_exact_consent_and_keeps_original_Run_sources(sqlite, operation, scenario);

    [Theory(Timeout = 120000)] [MemberData(nameof(ResourceAuthoringCases))]
    public async Task Canonical_Resource_authoring_records_exact_consent_and_keeps_original_Run_sources(bool sqlite, string operation, string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-resource-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var tool = operation.StartsWith("update", StringComparison.Ordinal) || operation == "create" ? "resources.upsert" : operation == "delete" ? "resources.delete" : operation == "copy" ? "resources.copy" : "resources.set_enabled";
        var read = operation == "create" ? "management.inspect" : "resources.inspect";
        try
        {
            // The same existing deterministic management model drives real ingress, worker, consent and continuation.
            var model = new SkillAuthoringModel(tool, read, "{}");
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => { b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning)); b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))); });
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = await SetupManagementRead(host, owner, false, inheritedResource: operation.StartsWith("inherited_", StringComparison.Ordinal) || operation == "copy"); var api = $"/api/v2/tenants/{tenant}"; var path = api + $"/agents/{instance}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Resources", "Settings" } }, autoApply = new { value = scenario.StartsWith("auto", StringComparison.Ordinal) ? new[] { "TextResources" } : Array.Empty<string>() } } })).StatusCode);
            var uploaded = await Json(await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3,
                input = new { logicalPath = "original.txt", kind = 2, mediaType = "text/plain", content = Encoding.UTF8.GetBytes("ORIGINAL-RESOURCE-TEXT"), enabled = true } }));
            var selected = uploaded.GetProperty("resource");
            if (operation.StartsWith("inherited_", StringComparison.Ordinal) || operation == "copy") selected = (await Json(await owner.GetAsync(path + "/resources"))).GetProperty("catalog").GetProperty("resources").EnumerateArray().First(r => r.GetProperty("origin").GetString() == "Definition");
            var key = selected.GetProperty("key").GetString()!; var resourceRevision = selected.GetProperty("revision").GetInt64();
            var registered = (SkillAuthoringModel)host.Services.GetRequiredService<ILanguageModel>();
            registered.ReadArguments = read == "management.inspect" ? "{}" : JsonSerializer.Serialize(new { key });
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            var access = host.Services.GetRequiredService<TenantAccessService>(); var human = state.Users.Single().PrincipalId;
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var primary = tool == "resources.delete" ? "resources.delete" : "resources.manage";
            var permissions = scenario == "no_read" ? new[] { "agents.read", primary }
                : scenario == "parent_only" ? new[] { "agents.read", "agents.update", "resources.read" }
                : scenario == "grant" ? new[] { "agents.read", "resources.read" } : new[] { "agents.read", "resources.read", primary };
            if (scenario == "approve" && operation is "enable" or "delete" or "update" or "inherited_disable") permissions = permissions.Append("sessions.read").ToArray();
            var role = await access.SaveRoleAsync(human, tenant, null, "Independent Resource author", permissions, await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (scenario == "auto_policy") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/resources.manage", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            object arguments = tool == "resources.upsert" ? operation == "create"
                ? new { expectedInstanceRevision = 4L, logicalPath = "created.txt", kind = "Reference", mediaType = "text/plain", content = "REVISED-RESOURCE-TEXT", source = "conversation:user" }
                : new { key, expectedResourceRevision = resourceRevision, expectedInstanceRevision = 4L, logicalPath = "original.txt", kind = "Reference", mediaType = "text/plain",
                    content = operation == "update_unchanged" ? "ORIGINAL-RESOURCE-TEXT" : "REVISED-RESOURCE-TEXT", source = scenario == "forged_source" ? "workspace:/working/not-observed.txt" : "conversation:user" }
                : tool == "resources.copy" ? new { key, expectedInstanceRevision = 4L, logicalPath = "copied-policy.md" }
                : tool == "resources.delete" ? new { key, expectedResourceRevision = resourceRevision, expectedInstanceRevision = 4L }
                : new { key, expectedResourceRevision = resourceRevision, expectedInstanceRevision = 4L, enabled = operation == "inherited_reset" ? (bool?)null : operation == "enabled_unchanged" };
            if (scenario == "approve" && operation is "enable" or "delete" or "update" or "inherited_disable")
                registered.AfterCommandRead = new("resource-original-pin", ToolCatalog.WorkspaceRead, JsonSerializer.Serialize(new { path = selected.GetProperty("virtualPath").GetString() }));
            registered.Arguments = JsonSerializer.Serialize(arguments); registered.SuppressConsent = scenario == "parent_only";
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Please save REVISED-RESOURCE-TEXT as the reviewed Resource draft.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            string Trace() => JsonSerializer.Serialize(registered.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.ToolCallId, m.Text })) + "\n" + string.Join('\n', logs.Errors);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
            var waiting = scenario is "approve" or "reject" or "grant" or "scoped" or "stale" or "auto_policy";
            var admitted = await AwaitManagementRun(runs, tenant, runId, waiting);
            TenantApprovalRequest? request = null; HttpClient? inspector = null;
            if (waiting)
            {
                Assert.True(admitted.Status == AgentRunStatus.WaitingForApproval, Trace());
                request = (await approvals.FindApprovalAsync(tenant, admitted.ApprovalRequestId!.Value))!;
                Assert.Equal(primary, request.Operation.Permission); Assert.NotNull(request.Operation.ManagementMutation!.Resource);
                Assert.Equal(scenario == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
                Assert.Equal(operation is "create" or "copy" ? "tenant" : "resource", request.ResourceType);
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                Assert.Equal(1, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                if (scenario == "scoped")
                {
                    var viewer = await FloorHuman(host, owner, tenant, "resource-scoped-reviewer", ["agents.read", "approvals.read"]); inspector = viewer.Client;
                    Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                    Assert.Equal(HttpStatusCode.Forbidden, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                    var child = await access.SaveRoleAsync(human, tenant, null, "Exact Resource review reader", new[] { "resources.read", primary }, await Revision(owner, tenant));
                    await access.AssignRoleAsync(human, tenant, viewer.Principal, child.RoleId, TenantAgentChildScopes.Scope(tenant, instance, "resource", key), await Revision(owner, tenant));
                    Assert.Equal(1, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                    Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                }
                if (scenario == "stale")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/resources/" + Uri.EscapeDataString(key) + "/enabled", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4, expectedResourceRevision = 1, enabled = false })).StatusCode);
                var response = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = scenario == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                if (scenario == "stale")
                {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); inspector?.Dispose(); return;
                }
                Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync() + Trace());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    var continuations = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items;
                    var final = continuations.FirstOrDefault(r => r.ApprovalRequestId == request.RequestId && r.AgentRunId != runId);
                    if (final?.IsTerminal == true) { admitted = final; break; }
                    await Task.Delay(25, deadline.Token);
                }
            }
            Assert.True(admitted.Status == AgentRunStatus.Completed, admitted.Failure?.Summary + Trace());
            Assert.Equal(4, admitted.Admission.Configuration!.InstanceRevision);
            var snapshot = (await host.Services.GetRequiredService<ITenantAgentStore>().ReadTenantAgentConfigurationAsync(tenant, instance))!;
            var changed = scenario is "approve" or "grant" or "scoped" or "auto" or "auto_policy";
            Assert.True(snapshot.Agent.Instance.Revision == (changed ? 5 : 4), Trace());
            var retained = TenantManagementReadReferences.Read(admitted.Checkpoint);
            if (scenario != "no_read") Assert.Single(retained, r => r.ToolName == read);
            await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, admitted.AgentRunId), retained, null);
            var receipt = request is null ? await ((ITenantDirectOperationStore)host.Services.GetRequiredService<IIamStore>()).FindDirectOperationReceiptAsync(tenant, admitted.AgentRunId, "write-skill") : await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (changed)
            {
                Assert.NotNull(receipt); Assert.NotNull(receipt.ManagementResource); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
                Assert.Equal(4, receipt.ManagementResource.BeforeInstanceRevision); Assert.Equal(TenantManagementMutationRules.Completed, receipt.ReasonCode);
                Assert.Equal(operation == "delete" ? 0 : operation is "create" or "copy" ? 2 : 1, snapshot.Resources.InstanceResources.Count);
                if (operation == "delete") Assert.Empty(snapshot.Resources.InstanceResources);
                if (operation == "create") Assert.Equal(2, snapshot.Resources.InstanceResources.Count);
                if (operation == "update") Assert.NotEqual(admitted.Admission.Configuration.Resources.Single(r => r.Key == key).ContentSha256,
                    snapshot.Resources.InstanceResources.Single(r => "instance:" + r.ResourceId.ToString("D") == key).ContentSha256);
                if (operation == "copy")
                {
                    var copied = snapshot.Resources.InstanceResources.Single(r => r.LogicalPath == "copied-policy.md");
                    Assert.Equal(selected.GetProperty("contentSha256").GetString(), copied.ContentSha256);
                    Assert.Equal(Guid.Parse(key[11..]), copied.SourceDefinitionResourceId);
                }
                if (operation is not ("create" or "copy"))
                {
                    var pin = admitted.Admission.Configuration.Resources.Single(r => r.Key == key);
                    var retention = await host.Services.GetRequiredService<ITenantManagementStore>().ReadRetainedResourcePinAsync(new(tenant, admitted.AgentRunId), pin);
                    Assert.NotNull(retention); Assert.Equal(resourceRevision, retention.ResourceRevision); Assert.Equal(operation == "delete", retention.Deleted);
                }
            }
            else Assert.True(receipt is null || receipt.Outcome != TenantOperationOutcome.Completed, Trace());
            if (registered.AfterCommandRead is not null)
            {
                var original = registered.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.ToolCallId == "resource-original-pin");
                using var body = JsonDocument.Parse(original.Text);
                Assert.True(body.RootElement.TryGetProperty("content", out _), original.Text);
                Assert.DoesNotContain("REVISED-RESOURCE-TEXT", original.Text);
            }
            if (scenario == "grant")
            {
                Assert.Equal("Consumed", (await approvals.FindOperationGrantAsync(tenant, request!.RequestId))!.Status.ToString());
                Assert.False((await host.Services.GetRequiredService<IamAuthorizer>().CheckAsync(agent.PrincipalId, primary, TenantAgentChildScopes.Scope(tenant, instance, "resource", key))).Allowed);
            }
            if (request is not null) Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
            if (scenario == "scoped" && operation != "delete") Assert.Equal(HttpStatusCode.OK, (await inspector!.GetAsync(api + $"/approvals/{request!.RequestId}")).StatusCode);
            if (scenario == "scoped")
            {
                if (operation == "delete")
                {
                    Assert.Equal(HttpStatusCode.Forbidden, (await inspector!.GetAsync(api + $"/approvals/{request!.RequestId}")).StatusCode);
                    Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/resources/" + Uri.EscapeDataString(key) + "/enabled", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 5, expectedResourceRevision = 2, enabled = false })).StatusCode);
                    Assert.Equal(HttpStatusCode.Forbidden, (await inspector!.GetAsync(api + $"/approvals/{request!.RequestId}")).StatusCode);
                    Assert.Equal(0, (await Json(await inspector.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32());
                    var pin = admitted.Admission.Configuration.Resources.Single(r => r.Key == key);
                    await Assert.ThrowsAsync<AccessDeniedException>(() => host.Services.GetRequiredService<ITenantManagementStore>().ReadRetainedResourcePinAsync(new(tenant, admitted.AgentRunId), pin).AsTask());
                    await Assert.ThrowsAsync<AccessDeniedException>(() => host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, admitted.AgentRunId), retained, null).AsTask());
                }
            }
            if (scenario == "parent_only") Assert.Contains("agent_missing_delegatable_permission", Trace());
            if (scenario == "no_read") Assert.DoesNotContain("ORIGINAL-RESOURCE-TEXT", Trace());
            if (scenario == "forged_source") Assert.Contains("resource_source_unavailable", Trace());
            if (scenario == "unchanged") Assert.Contains("operation_unchanged", Trace());
            inspector?.Dispose();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
