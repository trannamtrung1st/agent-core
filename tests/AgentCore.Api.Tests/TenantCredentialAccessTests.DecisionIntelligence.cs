using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class DecisionFactory : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?>
        {
            ["DecisionIntelligence:Enabled"] = "true", ["DecisionIntelligence:Backend"] = "Synthetic", ["DecisionIntelligence:ShadowMode"] = "false"
        };
    }
    [Fact]
    public async Task DecisionIntelligence_HTTP_Auto_chat_and_manual_automation_use_the_existing_admission_owners()
    {
        await using var factory = new DecisionFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(api + "/models/routing/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v2/system/decision-intelligence")).StatusCode);
        var systemStatus = await owner.GetAsync("/api/v2/system/decision-intelligence");
        Assert.Equal(HttpStatusCode.OK, systemStatus.StatusCode);
        Assert.DoesNotContain("endpoint", await systemStatus.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", await systemStatus.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var instanceRevision = agent.GetProperty("instance").GetProperty("revision").GetInt64();
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var principal = agent.GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<AgentCore.Application.Security.TenantAccessService>();
        var role = await access.SaveRoleAsync(actor, tenant, null, "Decision fixture executor", ["spaces.read", "sessions.create", "agent-runs.create", "automations.run"], await Revision(owner, tenant));
        await access.AssignRoleAsync(actor, tenant, principal, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var settings = await Json(await owner.GetAsync(api + "/models"));
        var save = new { routing = new { enabled = true, allowConfiguredDefault = false, minimumProbability = .7 }, expectedRevision = await Revision(owner, tenant), expectedSettingsRevision = settings.GetProperty("settings").GetProperty("revision").GetInt64() };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/models/routing", save)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(api + "/models/routing", save)).StatusCode);
        var status = await Json(await owner.GetAsync(api + "/models/routing/status"));
        Assert.Equal("ready", status.GetProperty("status").GetString()); Assert.False(status.GetProperty("shadowMode").GetBoolean());
        var created = await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
            retentionKind = "saved", modelPreference = new { mode = "auto", reasoning = new { mode = "modelDefault" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var session = (await Json(created)).GetProperty("sessionId").GetGuid();
        var inputId = Guid.NewGuid();
        var accepted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = inputId, text = "Hello with Auto", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        var runId = (await Json(accepted)).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var run = (await runs.FindTenantRunAsync(tenant, runId))!;
        Assert.Equal("ranked", run.PinnedModel.Decision!.Basis); Assert.Equal("synthetic", run.PinnedModel.Decision.EngineId);
        var view = await Json(await owner.GetAsync(api + $"/sessions/{session}/conversation"));
        Assert.Equal("auto", view.GetProperty("modelPreference").GetProperty("mode").GetString());
        Assert.Equal(run.PinnedModel.OfferingId, view.GetProperty("model").GetProperty("offeringId").GetString());
        Assert.True(view.GetProperty("model").GetProperty("observedFromRun").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(api + $"/sessions/{session}/model", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = view.GetProperty("revision").GetInt64(), modelPreference = new { mode = "inherit" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{runId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = run.Revision })).StatusCode);
        var route = api + $"/agents/{instance}/automations";
        var automationReply = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Auto background", instructions = "Complete the saved bounded task.", schedule = new { kind = "oneShot", atUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O"), timeZone = "UTC" },
            modelPreference = new { mode = "auto", reasoning = new { mode = "modelDefault" } }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision });
        Assert.True(automationReply.StatusCode == HttpStatusCode.Created, await automationReply.Content.ReadAsStringAsync());
        var automation = await Json(automationReply);
        var invoked = await owner.PostAsJsonAsync(route + "/" + automation.GetProperty("automationId").GetString() + "/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, expectedAutomationRevision = automation.GetProperty("revision").GetInt64() });
        Assert.True(invoked.StatusCode is HttpStatusCode.Created or HttpStatusCode.Accepted, await invoked.Content.ReadAsStringAsync());
        var result = await Json(invoked);
        if (result.GetProperty("runId").ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var background = (await runs.FindTenantRunAsync(tenant, result.GetProperty("runId").GetGuid()))!;
            Assert.Equal("ranked", background.PinnedModel.Decision!.Basis);
            Assert.Equal(ExecutionOrigin.ManualInvocation, background.Admission.ExecutionContext!.Origin);
        }
        else
        {
            var approval = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, result.GetProperty("approvalRequestId").GetGuid());
            Assert.Equal("ranked", approval!.Operation.AutomationAdmission!.Model.Decision!.Basis);
        }
    }
}
