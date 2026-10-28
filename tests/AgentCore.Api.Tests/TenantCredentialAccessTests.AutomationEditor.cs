using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData(false, "owner")] [InlineData(true, "owner")]
    [InlineData(false, "reader")] [InlineData(true, "reader")]
    [InlineData(false, "writer")] [InlineData(true, "writer")]
    [InlineData(false, "archived")] [InlineData(true, "archived")]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    public async Task Tenant_automation_editor_filters_before_paging_and_uses_independent_exact_action_hints(bool sqlite, string mode)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite);
        using var owner = ConnectedClient(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var route = api + $"/agents/{instance}/automations";
        var ids = new List<Guid>();
        foreach (var name in new[] { "Exact readable schedule", "Private schedule" })
        {
            using var created = await owner.PostAsJsonAsync(route, new { enabled = false, name, instructions = "Keep the exact saved instructions.", schedule = new { kind = "daily", interval = 1, timeZone = "UTC", localTime = "09:00" }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode); ids.Add((await Json(created)).GetProperty("automationId").GetGuid());
        }
        if (mode == "archived") Assert.True((await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).IsSuccessStatusCode);
        if (mode == "foreign")
        {
            var other = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/agents/{other}/automations/{ids[0]}/editor")).StatusCode); return;
        }
        HttpClient reader = owner; HttpClient? memberClient = null; Guid? assignment = null; var access = factory.Services.GetRequiredService<TenantAccessService>();
        if (mode is "reader" or "writer")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>(); var account = await identity.RegisterAsync("automation-editor@example.test", "Scoped reader", "strong-editor-member-password");
            var basic = await access.SaveRoleAsync(human, tenant, null, "Membership", ["sessions.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(human, tenant, account.LoginKey, basic.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var agentRead = await access.SaveRoleAsync(human, tenant, null, "Read agent", ["agents.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, member.PrincipalId, agentRead.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            var permissions = mode == "writer" ? new[] { "automations.read", "automations.update" } : new[] { "automations.read" };
            var role = await access.SaveRoleAsync(human, tenant, null, "One schedule", permissions, await Revision(owner, tenant));
            assignment = (await access.AssignRoleAsync(human, tenant, member.PrincipalId, role.RoleId, TenantAutomationRules.Scope(tenant, ids[0]), await Revision(owner, tenant))).AssignmentId;
            memberClient = ConnectedClient(factory); await Login(memberClient, account.LoginKey, "strong-editor-member-password"); reader = memberClient;
        }
        using var memberLifetime = memberClient;
        var view = await Json(await reader.GetAsync(route + "/editor?page=0&pageSize=1"));
        Assert.Equal(mode is "reader" or "writer" ? 1 : 2, view.GetProperty("total").GetInt32());
        Assert.Equal(mode == "archived" ? "Archived" : "Active", view.GetProperty("agent").GetProperty("instance").GetProperty("lifecycle").GetString());
        Assert.Equal(await Revision(owner, tenant), view.GetProperty("securityRevision").GetInt64());
        Assert.Equal(mode == "owner", view.GetProperty("canCreate").GetBoolean());
        if (mode is "reader" or "writer")
        {
            var row = Assert.Single(view.GetProperty("items").EnumerateArray()); Assert.Equal(ids[0], row.GetProperty("automation").GetProperty("automationId").GetGuid());
            Assert.Equal(mode == "writer", row.GetProperty("canUpdate").GetBoolean()); Assert.False(row.GetProperty("canCancel").GetBoolean()); Assert.False(row.GetProperty("canRun").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route + $"/{ids[1]}/editor")).StatusCode);
            Assert.Empty((await Json(await reader.GetAsync(route + "/editor?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
            await access.RevokeAssignmentAsync(human, tenant, assignment!.Value, await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route + $"/{ids[0]}/editor")).StatusCode);
        }
        if (mode == "archived") Assert.All(view.GetProperty("items").EnumerateArray(), item => { Assert.False(item.GetProperty("canUpdate").GetBoolean()); Assert.False(item.GetProperty("canCancel").GetBoolean()); });
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(route + "/editor?page=-1")).StatusCode);
    }
}
