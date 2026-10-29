using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact(Timeout = 120000)]
    public async Task Owner_credential_retirement_preserves_Account_tenant_and_completed_conversation_across_explicit_SQLite_migration_and_host_recreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-owner-retirement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Execute(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution, TenantRunExecution>());
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root, "store.db")};Foreign Keys=True").Options;
        Guid tenant, principal, session; string account, history;
        HistoricalTenantDatabase? historical = null;
        var historicalRoot = Path.Combine(root, "predecessor"); Directory.CreateDirectory(historicalRoot);
        var historicalOptions = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(historicalRoot, "store.db")};Foreign Keys=True").Options;
        try
        {
            await using (var first = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Execute))
            {
                using var owner = first.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                account = (await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetRawText();
                var membership = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0];
                tenant = membership.GetProperty("tenantId").GetGuid(); principal = membership.GetProperty("principalId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new {templateId="examiner", version=1, expectedRevision=await Revision(owner, tenant)}))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                await GrantNativePrivateSpaceRead(owner, tenant, instance);
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new {retentionKind="saved", agentInstanceId=instance, expectedRevision=await Revision(owner, tenant), expectedInstanceRevision=1}))).GetProperty("sessionId").GetGuid();
                var admitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new {messageId=Guid.NewGuid(), text="Preserve this tenant conversation across credential retirement", expectedRevision=await Revision(owner, tenant), expectedSessionRevision=1});
                Assert.Equal(HttpStatusCode.Accepted, admitted.StatusCode); var run = (await Json(admitted)).GetProperty("runId").GetGuid();
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(AgentCore.Domain.Conversation.AgentRunStatus.Completed, (await AwaitCancelledTenantRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, run)).Status);
                history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").GetRawText();
                historical = await HistoricalTenantDatabase.CreateAsync(first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    "20261021060000_TenantAutomationConversation", HistoricalTenantDatabase.AdmissionTables,
                    destinationPath: Path.Combine(historicalRoot, "store.db"));
            }
            // Recreate the real predecessor in an isolated graph; later protected
            // cutovers must never be crossed by downgrading the current source.
            await historical.Db.Database.CloseConnectionAsync();
            await using (var staged = new AgentCoreDbContext(historicalOptions))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RetiredOwnerHeader.Token))).ToLowerInvariant();
                await staged.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OwnerCapabilities (TokenHash, CreatedAtUtc) VALUES ({hash}, {123456L})");
            }
            await using (var refused = new NativeTenantSqliteFactory(historicalRoot, new ScriptedLanguageModel(), Execute))
            {
                var error = Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => refused.CreateClient());
                Assert.Contains("data_reset_required", error.Message, StringComparison.Ordinal);
            }
            await using (var migration = new AgentCoreDbContext(historicalOptions))
            {
                await migration.Database.OpenConnectionAsync(); using (var count = migration.Database.GetDbConnection().CreateCommand())
                { count.CommandText = "SELECT count(*) FROM OwnerCapabilities"; Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync())); }
                async Task<string[]> PreservedRows()
                {
                    var rows = new List<string>();
                    foreach (var table in HistoricalTenantDatabase.AdmissionTables)
                    {
                        await using var read = migration.Database.GetDbConnection().CreateCommand();
                        read.CommandText = $"SELECT * FROM {table}";
                        await using var reader = await read.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            var values = new object[reader.FieldCount]; reader.GetValues(values);
                            rows.Add(table + ":" + System.Text.Json.JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
                        }
                    }
                    return rows.Order(StringComparer.Ordinal).ToArray();
                }
                var retained = await PreservedRows();
                await migration.GetService<IMigrator>().MigrateAsync("20261021102840_RetireOwnerCapabilityStore");
                Assert.Equal("20261021102840_RetireOwnerCapabilityStore", (await migration.Database.GetAppliedMigrationsAsync()).Last());
                Assert.Equal(retained, await PreservedRows());
                Assert.Equal(0, await migration.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='table' AND name='OwnerCapabilities'").SingleAsync());
            }
            // Qualify retirement at its own migration boundary. Later populated
            // cutovers intentionally refuse automatic upgrades (ExistingAgents=0).
            // Recreate the current source host independently and retain all identity,
            // conversation and retired-authority HTTP assertions below.
            await using (var reopened = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Execute))
            {
                using var legacy = RetiredOwnerHeader.CreateClient(reopened);
                Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync("/api/v2/account/me")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync($"/api/v2/tenants/{tenant}/sessions/{session}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await legacy.PostAsJsonAsync("/api/v2/owner-capability", new {})).StatusCode);
                using var owner = reopened.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal(account, (await Json(await owner.GetAsync("/api/v2/account/me"))).GetProperty("account").GetRawText());
                var membership = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0];
                Assert.Equal(tenant, membership.GetProperty("tenantId").GetGuid()); Assert.Equal(principal, membership.GetProperty("principalId").GetGuid());
                Assert.Equal(history, (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{session}/messages"))).GetProperty("items").GetRawText());
                await using var db = new AgentCoreDbContext(options);
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.DoesNotContain(db.Model.GetEntityTypes(), type => type.GetTableName() == "OwnerCapabilities");
                await db.Database.OpenConnectionAsync(); using var query = db.Database.GetDbConnection().CreateCommand();
                query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='OwnerCapabilities'";
                Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync()));
                Assert.Null(typeof(IIdGenerator).Assembly.GetType("AgentCore.Application.Ports.IOwnerCapabilityService"));
                Assert.Null(typeof(AgentCoreDbContext).Assembly.GetType("AgentCore.Infrastructure.Persistence.SqliteOwnerCapabilityStore"));
                Assert.Null(typeof(AgentCore.Contracts.Http.HealthResponse).Assembly.GetType("AgentCore.Contracts.Http.OwnerCapabilityHeaders"));
            }
        }
        finally { if (historical is not null) await historical.DisposeAsync(); SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
