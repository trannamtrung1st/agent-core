using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private static async Task GrantAutomationPrivateContext(OAuthFactory factory, HttpClient owner, Guid tenant, Guid human, TenantAgent agent)
    {
        var privateSpace = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Read own automation context", ["spaces.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, TenantSpaceService.Scope(tenant, privateSpace.SpaceId), await Revision(owner, tenant));
    }

    [Theory]
    [InlineData(false, "manual")]
    [InlineData(true, "manual")]
    [InlineData(false, "scheduled")]
    [InlineData(true, "scheduled")]
    public Task Independent_Automation_captures_Private_Space_without_an_Environment_and_completes_its_actual_worker(bool sqlite, string mode) =>
        Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes(sqlite, mode);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpinned_existing_conversation_Automation_refuses_a_changed_Space_without_switching_the_target(bool sqlite)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite); using var owner = ConnectedClient(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32,
            expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        await GrantAutomationPrivateContext(factory, owner, tenant, human, agent);
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Existing context executor", ["automations.run", "agent-runs.create", "sessions.read", "agents.chat"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, mode = "text",
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision }))).GetProperty("sessionId").GetGuid();
        var route = api + $"/agents/{instance}/automations";
        var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Private default", instructions = "Stay in the exact configured context.",
            targetSessionId = session, schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O") },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var automation = (await Json(created)).GetProperty("automationId").GetGuid();
        var directory = factory.Services.GetRequiredService<TenantSpaceService>();
        var shared = await directory.CreateAsync(human, tenant, Guid.NewGuid(), "Different selection", "", "", await Revision(owner, tenant));
        await directory.SetMemberAsync(human, tenant, shared.SpaceId, instance, "", "", [], [], await Revision(owner, tenant), shared.Revision, 0);
        var read = await access.SaveRoleAsync(human, tenant, null, "Read selected Shared Space", ["spaces.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, read.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision(owner, tenant));
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
        var before = (await sessions.FindTenantSessionAsync(tenant, session))!;
        var login = (await factory.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!.Session;
        await factory.Services.GetRequiredService<TenantSessionContextService>().SwitchAsync(tenant, session, new(login.AccountId, login.SessionId, human), Guid.NewGuid(),
            shared.SpaceId, null, await Revision(owner, tenant), before.Snapshot.Revision, before.Snapshot.ContextSelection!.Revision);
        var selected = (await sessions.FindTenantSessionAsync(tenant, session))!;
        var refused = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant),
            expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("automation_context_mismatch", await refused.Content.ReadAsStringAsync());
        Assert.Equal(selected.Snapshot.ContextSelection, (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.ContextSelection);
        Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, session, null, 0, 10)).Items);
        Assert.Equal(0, (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant, automation))!.OccurrenceCount);
    }
}
