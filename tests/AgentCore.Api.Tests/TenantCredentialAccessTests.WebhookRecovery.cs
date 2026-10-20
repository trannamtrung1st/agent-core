using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory] [InlineData("active")] [InlineData("rotated")] [InlineData("revoked")]
    public async Task Tenant_webhook_SQLite_host_recreation_retains_source_token_revocation_and_exact_receipt_facts(string mode)
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-webhook-recovery-" + Guid.NewGuid().ToString("N"));
        Guid tenant = default, source = default, receipt = default; string token = "", originalToken = "", ingress = "";
        void Configure(IServiceCollection services) { services.RemoveAll<IExternalEventStore>(); services.AddSingleton<IExternalEventStore, SqliteExternalEventStore>(); }
        async Task<HttpResponseMessage> Send(HttpClient client, string key, string sourceEventId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ingress) { Content = new StringContent(ExternalEventEnvelope.Build(sourceEventId, "retained original data"), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return await client.SendAsync(request);
        }
        try
        {
            await using (var first = new OAuthFactory(path, sqlite: true, preserve: true, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(first); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var route = $"/api/v2/tenants/{tenant}/webhooks";
                var create = await owner.PostAsJsonAsync(route, new { displayName = "Retained exact webhook", eventKey = "retained.received", expectedRevision = await Revision(owner, tenant) });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode); var body = await Json(create); source = body.GetProperty("source").GetProperty("resourceId").GetGuid();
                ingress = body.GetProperty("source").GetProperty("ingressPath").GetString()!; token = originalToken = body.GetProperty("token").GetString()!;
                using var sender = ConnectedClient(first); var admitted = await Send(sender, token, "before-recreation"); Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode); receipt = (await Json(admitted)).GetProperty("eventId").GetGuid();
                if (mode != "active")
                {
                    var result = await owner.PostAsJsonAsync(route + $"/{source}/" + (mode == "rotated" ? "rotate" : "revoke"), new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 });
                    Assert.Equal(HttpStatusCode.OK, result.StatusCode); if (mode == "rotated") token = (await Json(result)).GetProperty("token").GetString()!;
                }
            }
            await using (var second = new OAuthFactory(path, sqlite: true, preserve: true, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var route = $"/api/v2/tenants/{tenant}/webhooks";
                var read = await Json(await owner.GetAsync(route + $"/{source}")); Assert.False(read.TryGetProperty("token", out _)); Assert.False(read.TryGetProperty("credentialHash", out _));
                var facts = await Json(await owner.GetAsync(route + $"/{source}/receipts")); Assert.Equal(1, facts.GetProperty("total").GetInt32()); Assert.Equal(receipt, facts.GetProperty("items")[0].GetProperty("eventId").GetGuid());
                using var sender = ConnectedClient(second);
                if (mode == "revoked") Assert.Equal(HttpStatusCode.Unauthorized, (await Send(sender, originalToken, "after-recreation")).StatusCode);
                else
                {
                    if (mode == "rotated") Assert.Equal(HttpStatusCode.Unauthorized, (await Send(sender, originalToken, "after-recreation")).StatusCode);
                    var replay = await Send(sender, token, "before-recreation"); Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(receipt, (await Json(replay)).GetProperty("eventId").GetGuid());
                    Assert.Equal(HttpStatusCode.Accepted, (await Send(sender, token, "after-recreation")).StatusCode);
                }
                await using var db = second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
                await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check"; using var rows = await command.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
                Assert.Empty((await second.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items); Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
