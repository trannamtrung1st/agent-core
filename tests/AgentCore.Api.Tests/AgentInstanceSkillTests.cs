using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;

public sealed class AgentInstanceSkillTests
{
    [Fact]
    public async Task Optional_readable_ids_are_owner_scoped_auto_generated_and_stable()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var admin = host.Services.GetRequiredService<AdminAgentInstanceService>();
        var first = await admin.CreateManagedAsync("general-assistant", 21); var second = await admin.CreateManagedAsync("general-assistant", 21);
        string Path(Guid id) => $"/api/v2/admin/agent-instances/{id}/skills";
        var input = new { id = "review", name = "Review", description = "Review evidence", procedure = "Check evidence", projection = "OnDemand", enabled = true, requiredCapabilities = Array.Empty<string>() };
        foreach (var owner in new[] { first, second }) (await client.PostAsJsonAsync(Path(owner.InstanceId), input)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path(first.InstanceId), input)).StatusCode);
        var generated = await client.PostAsJsonAsync(Path(first.InstanceId), input with { id = "" }); generated.EnsureSuccessStatusCode();
        Assert.Equal("instance:review-2", (await generated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());
        var invalid = await client.PostAsJsonAsync(Path(first.InstanceId), input with { id = "Bad ID" }); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var unicode = await client.PostAsJsonAsync(Path(first.InstanceId), input with { id = "", name = "İstanbul Review" }); unicode.EnsureSuccessStatusCode();
        Assert.Equal("instance:stanbul-review", (await unicode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());
        var renamed = await client.PatchAsJsonAsync(Path(first.InstanceId) + "/instance:review", new { id = "different", expectedRevision = 1, input.name, input.description, input.procedure, input.projection, input.enabled, input.requiredCapabilities });
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Equal("instance:review", (await client.GetFromJsonAsync<JsonElement>(Path(second.InstanceId) + "/instance:review")).GetProperty("key").GetString());
        var resolver = new EffectiveSkillCatalogResolver(host.Services.GetRequiredService<IAgentInstanceStore>());
        var definition = (await host.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(first.DefinitionId, 21))!;
        Assert.Contains(await resolver.ResolveAsync(first.InstanceId, definition), s => s.Key == "instance:review-2");
    }

    [Fact]
    public async Task Combined_Always_budget_returns_validation_and_preserves_owner_and_skills()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var owner = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var path = $"/api/v2/admin/agent-instances/{owner.InstanceId}/skills";
        var input = new { name = "Always", description = "Always procedure", procedure = new string('a', 4000), projection = "Always", enabled = true, requiredCapabilities = Array.Empty<string>() };
        (await client.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(path, input)).EnsureSuccessStatusCode();
        var before = await client.GetStringAsync(path);
        var ownerBefore = await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId);
        var rejected = await client.PostAsJsonAsync(path, input);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("8000-character", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(before, await client.GetStringAsync(path));
        Assert.Equal(ownerBefore, await host.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId));
    }

    [Fact]
    public async Task Owner_skill_journey_shares_tool_policy_and_rejects_stale_wrong_origin_and_foreign_scope()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var instances = host.Services.GetRequiredService<AdminAgentInstanceService>();
        var owner = await instances.CreateManagedAsync("general-assistant", 21);
        var other = await instances.CreateManagedAsync("general-assistant", 21);
        var path = $"/api/v2/admin/agent-instances/{owner.InstanceId}/skills";
        var initial = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Single(initial.EnumerateArray());
        var sourceKey = initial[0].GetProperty("key").GetString()!;
        var sourceUrl = path + "/" + Uri.EscapeDataString(sourceKey);
        (await client.PutAsJsonAsync(sourceUrl + "/enabled", new { enabled = false, expectedRevision = 1 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(sourceUrl + "/enabled", new { enabled = true, expectedRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(sourceUrl + "?expectedRevision=2")).StatusCode);
        var custom = await client.PostAsJsonAsync(sourceUrl + "/customize", new { expectedRevision = 2 }); custom.EnsureSuccessStatusCode();
        var customized = await custom.Content.ReadFromJsonAsync<JsonElement>();
        var copy = customized.GetProperty("instanceSkill");
        var sourceResult = customized.GetProperty("definitionSkill");
        Assert.Equal(sourceKey, sourceResult.GetProperty("key").GetString());
        Assert.False(sourceResult.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, sourceResult.GetProperty("revision").GetInt64());
        Assert.Equal("Instance", copy.GetProperty("origin").GetString());
        Assert.Equal("browser.record.lookup", copy.GetProperty("sourceDefinitionSkillId").GetString());
        var copyUrl = path + "/" + Uri.EscapeDataString(copy.GetProperty("key").GetString()!);
        var created = await client.PostAsJsonAsync(path, new { name = "Accounting", description = "Accounting procedure", procedure = "OLD_ACCOUNTING", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "workspace.read" } });
        created.EnsureSuccessStatusCode(); var local = await created.Content.ReadFromJsonAsync<JsonElement>();
        var localKey = local.GetProperty("key").GetString()!;
        var localUrl = path + "/" + Uri.EscapeDataString(localKey);
        var resolver = new EffectiveSkillCatalogResolver(host.Services.GetRequiredService<IAgentInstanceStore>());
        var definition = (await host.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(owner.DefinitionId, 21))!;
        var pin = await resolver.ResolveAsync(owner.InstanceId, definition);
        var input = new { name = "Accounting", description = "Accounting procedure", procedure = "NEW_ACCOUNTING", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "workspace.read" }, expectedRevision = 1 };
        (await client.PatchAsJsonAsync(localUrl, input)).EnsureSuccessStatusCode();
        Assert.Equal("OLD_ACCOUNTING", pin.Single(s => s.Key == localKey).Procedure);
        Assert.Equal("NEW_ACCOUNTING", (await resolver.ResolveAsync(owner.InstanceId, definition)).Single(s => s.Key == localKey).Procedure);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/admin/agent-instances/{other.InstanceId}/skills/{Uri.EscapeDataString(localKey)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(localUrl + "?expectedRevision=1")).StatusCode);
        var executor = host.Services.GetRequiredService<SessionToolExecutor>();
        var customizeCall = new ModelToolCall("self-customize", "skills.customize", JsonSerializer.Serialize(new { key = sourceKey, expectedRevision = 3 }));
        var customizeResult = await executor.ExecuteAsync(definition, Guid.NewGuid(), customizeCall, 8192,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId));
        using var resultJson = JsonDocument.Parse(customizeResult.Text);
        var agentCopy = resultJson.RootElement.GetProperty("instanceSkill");
        Assert.Equal("Instance", agentCopy.GetProperty("origin").GetString());
        Assert.Equal("Agent", agentCopy.GetProperty("createdBy").GetString());
        var disabled = resultJson.RootElement.GetProperty("definitionSkill");
        Assert.Equal(sourceKey, disabled.GetProperty("key").GetString());
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, disabled.GetProperty("revision").GetInt64());
        (await client.DeleteAsync(path + "/" + Uri.EscapeDataString(agentCopy.GetProperty("key").GetString()!) + "?expectedRevision=1")).EnsureSuccessStatusCode();
        var call = new ModelToolCall("self-create", "skills.create", JsonSerializer.Serialize(new { name = "Review", description = "Review evidence", procedure = "Review evidence", projection = "Always", enabled = true, requiredCapabilities = Array.Empty<string>() }));
        var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), call, 8192, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId));
        Assert.DoesNotContain("\"error\"", result.Text);
        var skills = host.Services.GetRequiredService<AgentInstanceSkillService>();
        Assert.Contains(await skills.ListAsync(owner.InstanceId), s => s.Name == "Review");
        Assert.DoesNotContain(await skills.ListAsync(other.InstanceId), s => s.Name == "Review");
        var denied = await executor.ExecuteAsync(definition with { Environment = RoleEnvironment.Empty }, Guid.NewGuid(), call, 8192, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: other.InstanceId));
        Assert.Contains("forbidden", denied.Text);
        var foreign = call with { ArgumentsJson = call.ArgumentsJson[..^1] + ",\"agentInstanceId\":\"" + other.InstanceId + "\"}" };
        Assert.Contains("Unknown Skill argument", (await executor.ExecuteAsync(definition, Guid.NewGuid(), foreign, 8192, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId))).Text);
        (await client.DeleteAsync(copyUrl + "?expectedRevision=1")).EnsureSuccessStatusCode();
        (await client.DeleteAsync(localUrl + "?expectedRevision=2")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(localUrl)).StatusCode);
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
    }
}
