using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ChatInstanceCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_exact_definitions_do_not_block_valid_chat_instances(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "chat-instance-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite
                ? new DurableSqliteHostFactory(Path.Combine(root, "store.db"), runScheduler: false)
                : new AgentCoreApiFactory();
            using var client = TestOwnerCapability.CreateOwnerClient(factory);
            var service = factory.Services.GetRequiredService<IAgentInstanceService>();
            var store = factory.Services.GetRequiredService<IAgentInstanceStore>();
            var valid = await service.CreateAsync("examiner", 1);
            var staleVersion = valid with { InstanceId = Guid.NewGuid(), ActiveVersion = int.MaxValue };
            var missingDefinition = valid with { InstanceId = Guid.NewGuid(), DefinitionId = "retired-definition" };
            await store.InsertAsync(staleVersion);
            await store.InsertAsync(missingDefinition);
            var storedStaleVersion = await store.FindAsync(staleVersion.InstanceId);
            var storedMissingDefinition = await store.FindAsync(missingDefinition.InstanceId);

            var response = await client.GetAsync("/api/v2/agent-instances");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var catalog = (await response.Content.ReadFromJsonAsync<ChatAgentInstanceListResponse>())!;
            Assert.Contains(catalog.Items, item => item.InstanceId == valid.InstanceId.ToString("D")
                && item.ActiveVersion == 1 && item.Name == valid.Persona.Name);
            Assert.DoesNotContain(catalog.Items, item => item.InstanceId == staleVersion.InstanceId.ToString("D")
                || item.InstanceId == missingDefinition.InstanceId.ToString("D"));
            Assert.Equal(storedStaleVersion, await store.FindAsync(staleVersion.InstanceId));
            Assert.Equal(storedMissingDefinition, await store.FindAsync(missingDefinition.InstanceId));

            var create = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = valid.InstanceId, mode = "text" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var rejected = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = staleVersion.InstanceId, mode = "text" });
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

            await service.SetLifecycleAsync(valid.InstanceId, AgentInstanceLifecycle.Archived, valid.Revision);
            var empty = await client.GetFromJsonAsync<ChatAgentInstanceListResponse>("/api/v2/agent-instances");
            Assert.Empty(empty!.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
