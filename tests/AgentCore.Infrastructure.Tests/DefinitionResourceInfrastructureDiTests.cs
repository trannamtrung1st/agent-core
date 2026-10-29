using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed class DefinitionResourceInfrastructureDiTests
{
    [Fact]
    public void InMemory_infrastructure_resolves_definition_catalog_without_deadlock()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        services.AddAgentCoreIdentity();
        using var provider = services.BuildServiceProvider();
        var definitions = provider.GetRequiredService<IAgentDefinitionStore>();
        var resources = provider.GetRequiredService<IAgentDefinitionResourceAdminStore>();
        var content = provider.GetRequiredService<IDefinitionResourceContentStore>();
        Assert.NotNull(definitions);
        Assert.NotNull(resources);
        Assert.NotNull(content);
        Assert.Same(content, provider.GetRequiredService<ITenantResourceSourceContentStore>());
        Assert.NotNull(provider.GetRequiredService<AgentCore.Application.Security.TenantResourceSourceTarget>());
        Assert.NotNull(provider.GetRequiredService<IIamStore>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_catalog_and_tenant_built_in_resources_ignore_colliding_retained_publications(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-native-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic", persistence: new PersistenceOptions
            {
                Provider = sqlite ? "Sqlite" : "InMemory",
                ConnectionString = $"Data Source={Path.Combine(root, "store.db")}",
                CredentialProtectionKeyRoot = Path.Combine(root, "keys"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
                DefinitionResourceRoot = Path.Combine(root, "definition-resources")
            });
            services.AddAgentCoreIdentity();
            await using var provider = services.BuildServiceProvider();
            if (sqlite) await provider.GetRequiredService<SqliteMemoryStore>().EnsureCreatedAsync();
            var builtIns = provider.GetRequiredService<IBuiltInAgentDefinitionStore>();
            var catalog = provider.GetRequiredService<IAgentDefinitionStore>();
            Assert.Same(builtIns, catalog);
            var baseline = (await builtIns.ListAsync()).First(d => DefinitionExecutionPolicies.Of(d).KnowledgeList.Count > 0);
            var expected = await builtIns.GetAsync(baseline.Id);
            var admin = provider.GetRequiredService<IAgentDefinitionAdminStore>();
            var resources = provider.GetRequiredService<IAgentDefinitionResourceAdminStore>();
            var content = provider.GetRequiredService<IDefinitionResourceContentStore>();
            var inherited = provider.GetRequiredService<InheritedDefinitionResourceCatalog>();
            var expectedResources = await inherited.ListAsync(baseline);
            Assert.NotEmpty(expectedResources);
            var bytes = System.Text.Encoding.UTF8.GetBytes("Private retained resource sentinel.");
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await content.StoreVerifiedAsync(hash, bytes);
            var now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
            foreach (var id in new[] { baseline.Id, "retained-unowned-history" })
            {
                // Physical historical rows remain readable to native fixtures. They are not
                // part of a current shared runtime catalog or a tenant publication lookup.
                var candidate = AgentDefinitionCandidate.FromDefinition(baseline) with
                    { DefinitionId = id, AgentInstructions = "Retained unowned publication sentinel." };
                var draft = await admin.CreateDraftAsync(new(id, candidate, DefinitionDraftSourceKind.New, null, now));
                var resource = await resources.UpsertDraftResourceAsync(new(draft.DraftId, draft.Revision, null,
                    "private-shadow.txt", AgentDefinitionResourceKind.Reference, "text/plain", hash, bytes.Length, now));
                draft = (await admin.GetDraftAsync(draft.DraftId))!;
                await resources.UpsertDraftResourceAsync(new(draft.DraftId, draft.Revision, null,
                    expectedResources[0].LogicalPath, AgentDefinitionResourceKind.Knowledge, "text/plain", hash, bytes.Length, now));
                draft = (await admin.GetDraftAsync(draft.DraftId))!;
                int[] occupied = baseline.Version > 1 ? [baseline.Version - 1] : [];
                var publication = await admin.PublishDraftAsync(new(draft.DraftId, draft.Revision, occupied, now));
                Assert.Equal(baseline.Version, publication.Version);
                Assert.Equal(candidate.AgentInstructions, (await admin.GetPublicationAsync(id, publication.Version))!.Payload.AgentInstructions);
                Assert.Contains(await resources.ListPublicationResourcesAsync(id, publication.Version), r => r.ResourceId == resource.ResourceId);
                var resolved = await catalog.GetAsync(id, publication.Version);
                Assert.True(resolved is null || resolved.AgentInstructions != candidate.AgentInstructions);
            }
            Assert.Equal(expected, await catalog.GetAsync(baseline.Id));
            Assert.Null(await catalog.GetAsync("retained-unowned-history"));
            Assert.DoesNotContain(await catalog.ListAsync(), d => d.AgentInstructions == "Retained unowned publication sentinel.");
            var currentResources = await inherited.ListAsync(baseline);
            Assert.Equal(expectedResources, currentResources);
            Assert.DoesNotContain(currentResources, r => r.ContentSha256 == hash || r.LogicalPath == "private-shadow.txt");
            var owner = await provider.GetRequiredService<IdentityService>().BootstrapAsync("catalog-owner@example.test",
                "Catalog owner", "synthetic-catalog-password", "Catalog organization", "catalog-organization");
            var tenant = await provider.GetRequiredService<IIamStore>().ReadTenantAsync(owner.TenantId);
            var agent = await provider.GetRequiredService<TenantAgentService>().CreateAsync(owner.OwnerPrincipalId,
                owner.TenantId, baseline.Id, baseline.Version, tenant!.Tenant.Revision);
            var configuration = await provider.GetRequiredService<TenantAgentConfigurationService>()
                .ReadAsync(owner.OwnerPrincipalId, owner.TenantId, agent.Instance.InstanceId);
            Assert.Equal(expectedResources.Select(r => r.ContentSha256), configuration.Resources.Select(r => r.ContentSha256));
            Assert.DoesNotContain(configuration.Resources, r => r.ContentSha256 == hash || r.LogicalPath == "private-shadow.txt");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tenant_automation_planner_resolves_without_global_profile_or_trigger_services()
    {
        var services = new ServiceCollection();
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IMemoryStore)
            || d.ServiceType == typeof(ITriggerStore)).ToArray()) services.Remove(descriptor);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<InstanceAutomationPolicy>());
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return Path.Combine(dir.FullName, "agents");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
