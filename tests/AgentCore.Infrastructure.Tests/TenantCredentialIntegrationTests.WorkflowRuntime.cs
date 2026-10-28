using AgentCore.Application.Ports;
using AgentCore.Application.Workflows;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static ServiceProvider WorkflowRuntimeProvider(Fixture f, bool sqlite)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(new ConfigurationManager());
        services.AddFormalWorkflowRuntime(new PersistenceOptions { Provider = sqlite ? "Sqlite" : "InMemory", ConnectionString = $"Data Source={f.Root}/store.db;Foreign Keys=True;Default Timeout=30;Pooling=False" });
        return services.BuildServiceProvider();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_embedded_closed_graph_exact_bookmark_duplicate_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Workflow runtime", "workflow-runtime");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var service = Workflows(f); var id = Guid.NewGuid();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        await service.SaveAsync(tenant, id, human, new("Reviewed runtime", "", HumanWorkflow(human.PrincipalId), false, 0, await Revision(), "runtime-draft"));
        var published = await service.PublishAsync(tenant, id, human, new(1, await Revision(), "runtime-publish"));
        var version = published.Version!; var execution = Guid.NewGuid();
        var provider = WorkflowRuntimeProvider(f, sqlite);
        try {
            if (sqlite) await provider.MigrateFormalWorkflowRuntimeAsync();
            await provider.PopulateFormalWorkflowActivitiesAsync();
            var runtime = provider.GetRequiredService<IFormalWorkflowRuntime>();
            Assert.True(await runtime.IsReadyAsync());
            if (sqlite) await new SqliteMemoryStore(f.Contexts!, f.Clock).EnsureCreatedAsync();
            var waiting = await runtime.StartAsync(version, execution); var bookmark = Assert.Single(waiting.Bookmarks);
            Assert.Equal("review", bookmark.NodeId); Assert.False(waiting.Finished); Assert.False(waiting.Faulted);
            Assert.Equal(bookmark, Assert.Single((await runtime.StartAsync(version, execution)).Bookmarks));
            Assert.Null(await runtime.ReadAsync(Guid.NewGuid(), execution));
            Assert.Equal(bookmark, Assert.Single((await runtime.ResumeAsync(tenant, execution, bookmark.BookmarkId, "wrong-node", "approve")).Bookmarks));
            // Core and Elsa write the same physical database using separate contexts/transactions.
            await service.SaveAsync(tenant, id, human, new("Changed draft", "", HumanWorkflow(human.PrincipalId), false, 2, await Revision(), "runtime-edit"));
            Assert.Equal(version, await service.VersionAsync(tenant, id, 1, human));
            if (sqlite) {
                await provider.DisposeAsync(); provider = WorkflowRuntimeProvider(f, true); await provider.PopulateFormalWorkflowActivitiesAsync();
                runtime = provider.GetRequiredService<IFormalWorkflowRuntime>();
                Assert.Equal(bookmark, Assert.Single((await runtime.ReadAsync(tenant, execution))!.Bookmarks));
            }
            var accepted = await runtime.ResumeAsync(tenant, execution, bookmark.BookmarkId, "review", "approve");
            var terminal = Assert.Single(accepted.Bookmarks); Assert.Equal("done", terminal.NodeId); Assert.False(accepted.Finished);
            Assert.Equal(terminal, Assert.Single((await runtime.ResumeAsync(tenant, execution, bookmark.BookmarkId, "review", "approve")).Bookmarks));
            var completed = await runtime.ResumeAsync(tenant, execution, terminal.BookmarkId, "done", "complete");
            Assert.True(completed.Finished); Assert.False(completed.Faulted); Assert.Equal("done", completed.TerminalNodeId); Assert.Empty(completed.Bookmarks);
            Assert.Equal("done", (await runtime.ResumeAsync(tenant, execution, terminal.BookmarkId, "done", "complete")).TerminalNodeId);
            var refusedExecution = Guid.NewGuid(); var refusal = Assert.Single((await runtime.StartAsync(version, refusedExecution)).Bookmarks);
            var rejected = await runtime.ResumeAsync(tenant, refusedExecution, refusal.BookmarkId, "review", "reject");
            Assert.True(rejected.Finished); Assert.Equal("refused", rejected.TerminalNodeId); Assert.Empty(rejected.Bookmarks);
        }
        finally { await provider.DisposeAsync(); }
    }
}
