using System.Data.Common;
using System.Reflection;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_chat_large_catalog_filters_public_labels_visibility_and_exact_selection_in_one_store_page(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var at = f.Clock.GetUtcNow();
        var baseline = (await f.BuiltIns.GetAsync("examiner", 1))!;
        var instances = Enumerable.Range(0, 1024).Select(i => new AgentInstance(f.Ids.NewId(), baseline.Id, baseline.Version,
            baseline.Identity with { Name = $"Ångström {i:D4}", Role = "Café support", Tone = "private-search-marker" },
            AgentInstanceLifecycle.Active, at, at, ShowInChat: i % 3 != 0)).ToArray();
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            db.AgentInstances.AddRange(instances.Select(SqliteAgentInstanceStore.Map)); await db.SaveChangesAsync();
        }
        else foreach (var instance in instances) await f.Instances.InsertAsync(instance);
        var identities = instances.Select(i => new SystemIdentity(f.Ids.NewId(), setup.Tenant, f.Ids.NewId(), SystemIdentityKind.Agent, i.InstanceId, IdentityStatus.Active)).ToArray();
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 },
            SystemIdentities = state.SystemIdentities.Concat(identities).ToArray(),
            Principals = state.Principals.Concat(identities.Select(i => new Principal(i.PrincipalId, PrincipalKind.SystemIdentity, setup.Tenant, null, null, i.SystemIdentityId, IdentityStatus.Active))).ToArray() };
        Assert.True(await f.Iam.TrySaveTenantAsync(next, state.Tenant.Revision,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "test.catalog.seed", setup.Tenant.ToString("D"), "allowed", "fixture", at)));
        var commands = new ChatQueryCounter();
        ITenantAgentStore backing = sqlite ? new SqliteIamStore(new ChatQueryContexts(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={f.Root}/store.db").AddInterceptors(commands).Options)) : (ITenantAgentStore)f.Iam;
        var store = DispatchProxy.Create<ITenantAgentStore, ChatStoreProbe>(); var probe = (ChatStoreProbe)(object)store; probe.Inner = backing;
        var chat = f.ChatService(store);
        var first = await chat.ListAgentsAsync(setup.Tenant, setup.Owner, pageSize: 30, query: "åNGSTRÖM");
        Assert.Equal(instances.Count(i => i.ShowInChat), first.Total); Assert.Equal(30, first.Items.Count);
        Assert.Equal(0, probe.Finds); Assert.Equal(1, probe.Pages); if (sqlite) Assert.Equal(2, commands.Reads);
        var last = await chat.ListAgentsAsync(setup.Tenant, setup.Owner, page: first.Total / 30, pageSize: 30, query: "CAFÉ");
        Assert.Equal(first.Total, last.Total); Assert.Equal(first.Total % 30, last.Items.Count);
        Assert.Empty((await chat.ListAgentsAsync(setup.Tenant, setup.Owner, query: "private-search-marker")).Items);
        Assert.Empty((await chat.ListAgentsAsync(setup.Tenant, setup.Owner, instanceId: instances[0].InstanceId)).Items);
        Assert.Equal(instances[1].InstanceId, (await chat.ListAgentsAsync(setup.Tenant, setup.Owner, instanceId: instances[1].InstanceId)).Items.Single().InstanceId);
        Assert.Equal(0, probe.Finds); Assert.Equal(5, probe.Pages); if (sqlite) Assert.Equal(10, commands.Reads);
    }

    public class ChatStoreProbe : DispatchProxy
    {
        public ITenantAgentStore Inner = null!; public int Finds; public int Pages;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITenantAgentStore.FindTenantAgentAsync)) Finds++;
            if (method.Name == nameof(ITenantAgentStore.ListTenantAgentsAsync)) Pages++;
            return method.Invoke(Inner, args);
        }
    }
    private sealed class ChatQueryContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
    private sealed class ChatQueryCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Reads++; return ValueTask.FromResult(result); }
    }
}
