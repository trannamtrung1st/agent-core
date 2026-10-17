using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Tenant_Runtime_uses_current_adopted_policy_while_model_execution_retains_admitted_instructions()
    {
        await using var factory = new ApprovalExecutionFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var definitions = factory.Services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(actor, tenant, "Policy freshness", await Revision(owner, tenant));
        draft = await definitions.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with {
            AgentInstructions = "PINNED_OLD_INSTRUCTIONS: answer the current task clearly.",
            Environment = new(Capabilities: new("Selected", [ToolCatalog.WebFetch])), StateIsolationPolicy = new(HomeAccess: HomeAccess.ReadWrite)
        }, await Revision(owner, tenant), draft.Revision);
        await definitions.PublishAsync(actor, tenant, draft.DraftId, await Revision(owner, tenant), draft.Revision);
        var created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = Guid.Parse(draft.DefinitionId), version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Hello Synthetic policy", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
        var run = (await runs.FindTenantRunAsync(tenant, runId))!; var now = DateTimeOffset.UtcNow;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)), Guid.NewGuid());
        var authority = factory.Services.GetRequiredService<TenantRunExecutionAuthority>();
        Assert.True(RolePermissions.AllowsTool((await authority.CurrentDefinitionAsync(run))!, ToolCatalog.WebFetch));
        var fork = await definitions.ForkAsync(actor, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(owner, tenant));
        fork = await definitions.UpdateAsync(actor, tenant, fork.DraftId, fork.Candidate with {
            AgentInstructions = "NEW_INSTRUCTIONS_MUST_WAIT_FOR_NEXT_RUN", Environment = new(Capabilities: new("Selected", [ToolCatalog.WorkspaceRead])),
            StateIsolationPolicy = new(HomeAccess: HomeAccess.Disabled)
        }, await Revision(owner, tenant), fork.Revision);
        await definitions.PublishAsync(actor, tenant, fork.DraftId, await Revision(owner, tenant), fork.Revision);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/active-version", new { version = 2, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        var policy = (await authority.CurrentDefinitionAsync(run))!;
        Assert.Equal(2, policy.Version); Assert.False(RolePermissions.AllowsTool(policy, ToolCatalog.WebFetch));
        Assert.Equal(HomeAccess.Disabled, policy.StateIsolationPolicy!.HomeAccess);
        Assert.True(await factory.Services.GetRequiredService<ITenantRunDispatcher>().DispatchAsync(run));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            run = (await runs.FindTenantRunAsync(tenant, runId))!;
            if (run.IsTerminal) break;
            await Task.Delay(25, timeout.Token);
        }
        Assert.Equal(AgentRunStatus.Completed, run.Status); Assert.False(string.IsNullOrWhiteSpace(run.Result!.Text));
        Assert.Equal(1, run.Admission.Configuration!.Definition.Version); Assert.Null(run.Owner.ProfileIdOrNull);
        var requests = factory.Model.Requests.Where(r => r.Context is not null).ToArray(); Assert.NotEmpty(requests);
        foreach (var request in requests)
        {
            var instructions = Assert.Single(request.Context!.Sections, s => s.Id == ContextSectionId.AgentInstructions).Message.Text;
            Assert.Contains("PINNED_OLD_INSTRUCTIONS", instructions); Assert.DoesNotContain("NEW_INSTRUCTIONS", instructions);
        }
    }
}
