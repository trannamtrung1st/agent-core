using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task Workflows_stopped_database_upgrade_preserves_installed_data_and_restores_matching_backup()
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Installed tenant", "workflow-upgrade");
        await using (var db = await f.Contexts!.CreateDbContextAsync())
            await db.GetService<IMigrator>().MigrateAsync("20261028030836_BuiltInGeneralAssistant");
        var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteMemoryStore(f.Contexts!, f.Clock).EnsureCreatedAsync().AsTask());
        var path = Path.Combine(f.Root, "store.db"); var backup = Path.Combine(f.Root, "pre-workflows.db");
        await FormalWorkflowCutover.MigrateAsync(path, backup);
        Assert.Equal(before.Tenant, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant);
        await new SqliteMemoryStore(f.Contexts!, f.Clock).EnsureCreatedAsync();
        var options = new PersistenceOptions { Provider = "Sqlite", ConnectionString = "Data Source=" + path };
        var services = new ServiceCollection(); services.AddLogging(); services.AddFormalWorkflowRuntime(options);
        await using (var provider = services.BuildServiceProvider())
        {
            await provider.InitializeFormalWorkflowRuntimeAsync(freshDatabase: false);
            Assert.True(await provider.GetRequiredService<IFormalWorkflowRuntime>().IsReadyAsync());
        }
        await Assert.ThrowsAsync<ArgumentException>(() => FormalWorkflowCutover.MigrateAsync(path, backup));
        // Exercise the coordinated database restore; the matching previous application owns this restored schema.
        SqliteConnection.ClearAllPools();
        await using (var source = new SqliteConnection("Data Source=" + backup))
        await using (var destination = new SqliteConnection("Data Source=" + path))
        { await source.OpenAsync(); await destination.OpenAsync(); source.BackupDatabase(destination); }
        await using var restored = await f.Contexts!.CreateDbContextAsync();
        Assert.Equal("20261028030836_BuiltInGeneralAssistant", (await restored.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(before.Tenant, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant);
        Assert.DoesNotContain("20261028121054_FormalWorkflowCatalog", await restored.Database.GetAppliedMigrationsAsync());
    }
}
