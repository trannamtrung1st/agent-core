using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Fact(Timeout = 90000)]
    public async Task Harness_instructions_use_the_exact_tenant_consent_and_existing_settings_owner()
    {
        var model = new SkillAuthoringModel("harness.instructions.update", "harness.inspect", "{}");
        await using var factory = new ApprovalExecutionFactory();
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
        using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
            overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Instructions" } } } })).StatusCode);
        var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single().PrincipalId;
        var access = host.Services.GetRequiredService<TenantAccessService>(); var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var role = await access.SaveRoleAsync(human, tenant, null, "Harness instruction author", ["agents.read", "agents.update"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        model.Arguments = JsonSerializer.Serialize(new { expectedVersion = 32, expectedInstanceRevision = 3, policyRevision = agent.Instance.HarnessManagement!.PolicyRevision,
            content = "Retained exact Harness instructions.", expected = "The next Run uses these instructions.", observed = "Prepared a literal instruction draft; no production task executed." });
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 }))).GetProperty("sessionId").GetGuid();
        var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Update your complete Harness instructions after exact review.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var initial = (await Json(sent)).GetProperty("runId").GetGuid(); Volatile.Write(ref factory.Enabled, true);
        string Trace() => JsonSerializer.Serialize(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.Text }));
        var runs = host.Services.GetRequiredService<ITenantRunStore>(); var pending = await AwaitManagementRun(runs, tenant, initial, true);
        Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, pending.Failure?.Summary + Trace());
        var request = (await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        Assert.Equal("harness.instructions.update", request.Operation.ManagementMutation!.Harness!.ToolName); Assert.Equal("agents.update", request.Permission);
        var approved = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync() + Trace());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun done;
        while (true)
        {
            done = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.Admission.ApprovalResume?.RequestId == request.RequestId)!;
            if (done?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
        }
        Assert.True(done.Status == AgentRunStatus.Completed, done.Failure?.Summary + Trace());
        var updated = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
        Assert.Equal(4, updated.Revision); Assert.Equal("Retained exact Harness instructions.", updated.SettingsOverrides!.AgentInstructions!.Value);
        var assessment = Assert.Single(updated.HarnessManagement!.InstanceChanges!); Assert.Equal(HarnessEvidenceStatus.PartiallyVerified, assessment.Evidence.Status);
        Assert.Equal(3, done.Admission.Configuration!.InstanceRevision);
    }
}
