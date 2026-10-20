using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static TheoryData<bool, string> TenantWebhookCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
                foreach (var mode in new[] { "receipt", "cookie-no-token", "concurrent-replay", "builtin", "restore", "duplicate", "changed-evidence", "rename", "rotate", "revoke", "stale", "cross-tenant", "scoped-reader", "manage-without-read", "untrusted-root", "unexpected-identity", "large", "bad-token-before-body", "raw-owner", "raw-receipt", "direct-stale-token", "same-key" }) cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(TenantWebhookCases))]
    public async Task Tenant_webhook_management_and_authenticated_receipts_repeat_current_source_authority_without_creating_work(bool sqlite, string mode)
    {
        await using var factory = new OAuthFactory(sqlite: sqlite, servicesOverride: services => { if (sqlite) { services.RemoveAll<IExternalEventStore>(); services.AddSingleton<IExternalEventStore, SqliteExternalEventStore>(); } }); using var owner = ConnectedClient(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}"; var principal = (await Json(await owner.GetAsync(root + "/access/me"))).GetProperty("principalId").GetGuid();
        var route = root + "/webhooks"; var created = await owner.PostAsJsonAsync(route, new { displayName = "Exact authenticated source", eventKey = "orders.received", expectedRevision = await Revision(owner, tenant), tenantId = Guid.NewGuid(), createdByPrincipalId = Guid.NewGuid() });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await Json(created); var source = body.GetProperty("source"); var id = source.GetProperty("resourceId").GetGuid(); var token = body.GetProperty("token").GetString()!;
        var ingress = source.GetProperty("ingressPath").GetString()!; Assert.Equal($"/api/v2/hooks/{tenant:D}/{id:D}", ingress);
        var store = factory.Services.GetRequiredService<ITenantWebhookStore>(); var saved = (await store.FindTenantWebhookAsync(tenant, id))!;
        Assert.Equal(tenant, saved.TenantId); Assert.Equal(principal, saved.CreatedByPrincipalId); Assert.NotEqual(token, saved.CredentialHash);
        Assert.Null(await store.FindTenantWebhookAsync(Guid.NewGuid(), id));
        var global = factory.Services.GetRequiredService<IExternalEventStore>();
        Assert.Null(await global.GetAsync(id)); Assert.Null(await global.GetByKeyAsync(saved.EventKey));
        Assert.DoesNotContain(await global.ListAsync(), s => s.ResourceId == id); Assert.Equal(0, (await store.ListTenantWebhooksAsync(tenant, [], 0, 1)).Total);
        var list = await Json(await owner.GetAsync(route)); Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.DoesNotContain(token, list.GetRawText()); Assert.DoesNotContain(saved.CredentialHash!, list.GetRawText());
        using var unsigned = ConnectedClient(factory);
        async Task<HttpResponseMessage> Send(string data, string? credential = null, string? target = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target ?? ingress) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential ?? token); return await unsigned.SendAsync(request);
        }
        var evidence = ExternalEventEnvelope.Build("source-1", "order-original");
        if (mode is "scoped-reader" or "manage-without-read")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("webhook-reader@example.test", "Scoped webhook member", "strong-webhook-member-password");
            var role = await access.SaveRoleAsync(principal, tenant, null, "Exact webhook membership", mode == "manage-without-read" ? ["webhooks.manage"] : ["sessions.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var reader = ConnectedClient(factory); await Login(reader, "webhook-reader@example.test", "strong-webhook-member-password");
            Assert.Equal(0, (await Json(await reader.GetAsync(route))).GetProperty("total").GetInt32()); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route + $"/{id}")).StatusCode);
            if (mode == "manage-without-read")
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(route + $"/{id}/rotate", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 })).StatusCode); return;
            }
            var read = await access.SaveRoleAsync(principal, tenant, null, "Read only this source", ["webhooks.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(principal, tenant, member.PrincipalId, read.RoleId, TenantWebhookRules.Scope(tenant, id), await Revision(owner, tenant));
            var visible = await Json(await reader.GetAsync(route + "?page=0&pageSize=1")); Assert.Equal(1, visible.GetProperty("total").GetInt32()); Assert.False(visible.GetProperty("items")[0].GetProperty("canManage").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(route + $"/{id}/receipts")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(root + "/events/builtin")).StatusCode); return;
        }
        if (mode is "same-key" or "cross-tenant")
        {
            var duplicateKey = await owner.PostAsJsonAsync(route, new { displayName = "Conflicting source", eventKey = saved.EventKey, expectedRevision = await Revision(owner, tenant) });
            Assert.Equal(HttpStatusCode.Conflict, duplicateKey.StatusCode);
            var otherTenant = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other webhook tenant", slug = "webhook-" + Guid.NewGuid().ToString("N")[..8] }))).GetProperty("tenantId").GetGuid();
            var other = await owner.PostAsJsonAsync($"/api/v2/tenants/{otherTenant}/webhooks", new { displayName = "Independent same event key", eventKey = saved.EventKey, expectedRevision = await Revision(owner, otherTenant) });
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{otherTenant}/webhooks/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(evidence, target: $"/api/v2/hooks/{otherTenant}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(evidence, (await Json(other)).GetProperty("token").GetString())).StatusCode); return;
        }
        if (mode == "builtin")
        {
            var catalog = await Json(await owner.GetAsync(root + "/events/builtin")); Assert.NotEmpty(catalog.GetProperty("items").EnumerateArray());
            Assert.DoesNotContain(token, catalog.GetRawText()); Assert.DoesNotContain(id.ToString("D"), catalog.GetRawText()); return;
        }
        if (mode == "cookie-no-token")
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsync(ingress, new StringContent(evidence, Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(0, (await store.ListTenantWebhookReceiptsAsync(tenant, id, 0, 100)).Total); return;
        }
        if (mode == "concurrent-replay")
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(evidence)));
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(3, replies.Count(reply => reply.StatusCode == HttpStatusCode.OK));
            var ids = new List<Guid>(); foreach (var reply in replies) ids.Add((await Json(reply)).GetProperty("eventId").GetGuid());
            Assert.Single(ids.Distinct()); Assert.Equal(1, (await store.ListTenantWebhookReceiptsAsync(tenant, id, 0, 100)).Total);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items); Assert.Equal(0, factory.Outbound); return;
        }
        if (mode == "stale")
        {
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(route + $"/{id}", new { displayName = "Must not save", expectedRevision = await Revision(owner, tenant) - 1, expectedSourceRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(route + $"/{id}/rotate", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 0 })).StatusCode);
            Assert.Equal(saved.CredentialHash, (await store.FindTenantWebhookAsync(tenant, id))!.CredentialHash); return;
        }
        if (mode is "raw-owner" or "raw-receipt")
        {
            if (sqlite)
            {
                await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
                foreach (var sql in new[] { $"UPDATE WebhookEvents SET TenantId = '{Guid.NewGuid():D}' WHERE ResourceId = '{id:D}'", $"UPDATE WebhookEvents SET EventKey = 'retargeted.event', Revision = Revision+1 WHERE ResourceId = '{id:D}'", $"UPDATE WebhookEvents SET CredentialHash = NULL, Revision = Revision+1 WHERE ResourceId = '{id:D}'" })
                    await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261021010000_TenantAutomationAdmissionApproval"));
                Assert.Contains("20261021020000_TenantWebhookOwnership", await db.Database.GetAppliedMigrationsAsync());
            }
            if (mode == "raw-owner") return;
        }
        if (mode == "bad-token-before-body") { Assert.Equal(HttpStatusCode.Unauthorized, (await Send(new string('x', 9000), "wrong-token")).StatusCode); return; }
        if (mode == "large") { Assert.Equal(HttpStatusCode.BadRequest, (await Send(new string('x', 9000))).StatusCode); return; }
        if (mode == "untrusted-root") { Assert.Equal(HttpStatusCode.BadRequest, (await Send(JsonSerializer.Serialize(new { eventId = "source-1", data = new { orderReference = "a" }, rootAgentRunId = Guid.NewGuid(), triggerDepth = 1 }))).StatusCode); return; }
        if (mode == "unexpected-identity") { Assert.Equal(HttpStatusCode.BadRequest, (await Send(JsonSerializer.Serialize(new { eventId = "source-1", data = new { orderReference = "a" }, principalId = principal }))).StatusCode); return; }
        var admitted = await Send(evidence); Assert.True(admitted.StatusCode == HttpStatusCode.Accepted, await admitted.Content.ReadAsStringAsync());
        var receiptId = (await Json(admitted)).GetProperty("eventId").GetGuid();
        Assert.Null(await global.GetByEventIdAsync(receiptId)); Assert.Null(await global.GetEventAsync(id, "source-1")); Assert.Empty((await global.ReadActivityAsync(id)).Receipts);
        ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(evidence), out var rawEvidence, out _, out _, out _);
        await Assert.ThrowsAsync<AgentCoreException>(() => global.AdmitAsync(new ExternalEvent(Guid.NewGuid(), id, "unowned-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, rawEvidence), []).AsTask());
        var facts = await Json(await owner.GetAsync(route + $"/{id}/receipts")); Assert.Equal(1, facts.GetProperty("total").GetInt32());
        Assert.Equal(receiptId, facts.GetProperty("items")[0].GetProperty("eventId").GetGuid()); Assert.Equal(1, facts.GetProperty("items")[0].GetProperty("sourceRevision").GetInt64());
        Assert.DoesNotContain(token, facts.GetRawText()); Assert.Equal("order-original", facts.GetProperty("items")[0].GetProperty("data").GetProperty("orderReference").GetString());
        if (mode == "changed-evidence") Assert.Equal(HttpStatusCode.Conflict, (await Send(ExternalEventEnvelope.Build("source-1", "order-changed"))).StatusCode);
        else if (mode == "duplicate") { var replay = await Send(evidence); Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(receiptId, (await Json(replay)).GetProperty("eventId").GetGuid()); }
        else if (mode == "rename")
        {
            var renamed = await owner.PutAsJsonAsync(route + $"/{id}", new { displayName = "Renamed exact source", expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 }); Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal(saved.EventKey, (await Json(renamed)).GetProperty("eventKey").GetString()); Assert.Equal(saved.CredentialHash, (await store.FindTenantWebhookAsync(tenant, id))!.CredentialHash);
        }
        else if (mode is "rotate" or "direct-stale-token")
        {
            var rotated = await owner.PostAsJsonAsync(route + $"/{id}/rotate", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 }); Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            var newToken = (await Json(rotated)).GetProperty("token").GetString()!; Assert.NotEqual(token, newToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(ExternalEventEnvelope.Build("source-2", "old"))).StatusCode);
            if (mode == "direct-stale-token")
            {
                ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(evidence), out var normalized, out _, out _, out _);
                var candidate = new ExternalEvent(Guid.NewGuid(), id, "source-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, normalized, tenant, 1);
                Assert.Equal(TenantWebhookAdmissionKind.Unauthorized, (await store.TryAdmitTenantWebhookAsync(tenant, id, token, candidate)).Kind);
            }
            var replay = await Send(evidence, newToken); Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(receiptId, (await Json(replay)).GetProperty("eventId").GetGuid());
            Assert.Equal(HttpStatusCode.Accepted, (await Send(ExternalEventEnvelope.Build("source-2", "new"), newToken)).StatusCode);
        }
        else if (mode is "revoke" or "restore")
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + $"/{id}/revoke", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(ExternalEventEnvelope.Build("source-2", "revoked"))).StatusCode);
            Assert.Null((await store.FindTenantWebhookAsync(tenant, id))!.CredentialHash);
            if (mode == "restore")
            {
                var restored = await owner.PostAsJsonAsync(route + $"/{id}/rotate", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 2 });
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode); var value = await Json(restored); Assert.Equal("active", value.GetProperty("source").GetProperty("status").GetString());
                Assert.Equal(HttpStatusCode.Accepted, (await Send(ExternalEventEnvelope.Build("source-2", "restored"), value.GetProperty("token").GetString())).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await Send(ExternalEventEnvelope.Build("source-3", "old"))).StatusCode);
            }
        }
        if (mode == "raw-receipt" && sqlite)
        {
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ExternalEvents SET SourceRevision=999 WHERE EventId={receiptId.ToString("D")}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM WebhookEvents WHERE ResourceId={id.ToString("D")}"));
        }
        Assert.Empty((await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items); Assert.Equal(0, factory.Outbound);
    }
}
